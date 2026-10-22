using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Canonical llm_provider keys and alias normalization, shared by the routing
/// facade, the Settings UI, and the queue's configuration pre-flight so the
/// same stored value can never be interpreted differently in two places.
/// </summary>
public static class LlmProviderKeys
{
    public const string Anthropic = "anthropic";
    public const string OpenAiCompatible = "openai-compatible";

    /// <summary>
    /// Normalizes a stored llm_provider value to a canonical key. Accepts the
    /// "ollama" and "openai_compatible" aliases; anything else (including
    /// blank/unknown values) falls back to Anthropic, the seeded default.
    /// </summary>
    public static string Normalize(string? storedValue)
    {
        return storedValue?.Trim().ToLowerInvariant() switch
        {
            OpenAiCompatible or "openai_compatible" or "ollama" => OpenAiCompatible,
            _ => Anthropic
        };
    }
}

/// <summary>
/// Singleton <see cref="ILlmService"/> facade that re-reads
/// <see cref="SettingKeys.LlmProvider"/> on EVERY call and delegates to the
/// matching concrete provider — so switching providers in Settings applies
/// without a restart, preserving the app-wide "settings apply live" contract.
/// <para>
/// Inner services are resolved per call from the service provider:
/// <see cref="AnthropicLlmService"/> is a singleton registered as itself, and
/// <see cref="OpenAiCompatibleLlmService"/> is an HttpClient typed client
/// (transient), so per-call resolution also keeps HttpClient lifetimes healthy.
/// </para>
/// <para>
/// ProviderName/ModelName/RequiresInternet reflect the provider selected by the
/// most recent settings read (a background read primes it at construction);
/// they can therefore lag one call behind an unsaved settings change, which is
/// acceptable for display-only metadata.
/// </para>
/// </summary>
public class RoutingLlmService : ILlmService
{
    private readonly Func<string, ILlmService> _resolveProvider;
    private readonly ISettingsService _settingsService;
    private readonly ILlmUsageTracker _usageTracker;
    private readonly ILogger<RoutingLlmService> _logger;
    private volatile string _lastProviderKey = LlmProviderKeys.Anthropic;

    public RoutingLlmService(
        IServiceProvider serviceProvider,
        ISettingsService settingsService,
        ILlmUsageTracker usageTracker,
        ILogger<RoutingLlmService> logger)
        : this(
            key => key == LlmProviderKeys.OpenAiCompatible
                ? serviceProvider.GetRequiredService<OpenAiCompatibleLlmService>()
                : serviceProvider.GetRequiredService<AnthropicLlmService>(),
            settingsService,
            usageTracker,
            logger)
    {
    }

    /// <summary>
    /// Delegate-resolving constructor (test seam): <paramref name="resolveProvider"/>
    /// receives a canonical <see cref="LlmProviderKeys"/> key and returns the
    /// inner service to use. DI always picks the <see cref="IServiceProvider"/>
    /// constructor because no Func&lt;string, ILlmService&gt; is registered.
    /// </summary>
    public RoutingLlmService(
        Func<string, ILlmService> resolveProvider,
        ISettingsService settingsService,
        ILlmUsageTracker usageTracker,
        ILogger<RoutingLlmService> logger)
    {
        _resolveProvider = resolveProvider;
        _settingsService = settingsService;
        _usageTracker = usageTracker;
        _logger = logger;

        // Prime the display properties with the persisted provider so
        // ProviderName/ModelName are accurate before the first call. Fire and
        // forget: a failure just leaves the default (anthropic) until a call.
        _ = PrimeProviderKeyAsync();
    }

    public string ProviderName => ResolveForDisplay().ProviderName;
    public string ModelName => ResolveForDisplay().ModelName;
    public bool RequiresInternet => ResolveForDisplay().RequiresInternet;

    public Task<EventExtractionResult> ExtractEventsAsync(string transcript)
    {
        return ExtractEventsAsync(transcript, null);
    }

    public async Task<EventExtractionResult> ExtractEventsAsync(string transcript, ExtractionContext? context)
    {
        var inner = await ResolveCurrentAsync();
        var result = await inner.ExtractEventsAsync(transcript, context);

        // Prefer real token usage; fall back to a prompt-size estimate.
        // (long: EstimateTokens and RecordCall both work in long.)
        long tokens = result?.TokenUsage?.TotalTokens ?? 0;
        if (tokens <= 0)
        {
            tokens = LlmUsageTracker.EstimateTokens(transcript);
        }
        _usageTracker.RecordCall(inner.ProviderName, tokens);

        return result!;
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens = 2000, CancellationToken ct = default)
    {
        var inner = await ResolveCurrentAsync();
        var response = await inner.CompleteAsync(systemPrompt, userPrompt, maxTokens, ct);

        _usageTracker.RecordCall(
            inner.ProviderName,
            LlmUsageTracker.EstimateTokens(systemPrompt)
                + LlmUsageTracker.EstimateTokens(userPrompt)
                + LlmUsageTracker.EstimateTokens(response));

        return response;
    }

    /// <summary>
    /// Streams through the provider selected at the moment the stream is
    /// opened. Usage is recorded once the stream has been read to its end
    /// (an abandoned stream records nothing — the estimate would be a guess).
    /// </summary>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        string systemPrompt,
        string userPrompt,
        int maxTokens = 2000,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var inner = await ResolveCurrentAsync();
        var streamed = new StringBuilder();

        await foreach (var chunk in inner.StreamCompletionAsync(systemPrompt, userPrompt, maxTokens, ct).WithCancellation(ct))
        {
            streamed.Append(chunk);
            yield return chunk;
        }

        _usageTracker.RecordCall(
            inner.ProviderName,
            LlmUsageTracker.EstimateTokens(systemPrompt)
                + LlmUsageTracker.EstimateTokens(userPrompt)
                + LlmUsageTracker.EstimateTokens(streamed.ToString()));
    }

    /// <summary>
    /// Re-reads llm_provider and resolves the matching inner service.
    /// </summary>
    private async Task<ILlmService> ResolveCurrentAsync()
    {
        var stored = await _settingsService.GetLlmProviderAsync();
        var key = LlmProviderKeys.Normalize(stored);

        if (!string.Equals(key, _lastProviderKey, StringComparison.Ordinal))
        {
            _logger.LogInformation("LLM provider routing switched to '{Provider}'", key);
            _lastProviderKey = key;
        }

        return _resolveProvider(key);
    }

    /// <summary>
    /// Resolves the provider for the sync display properties using the
    /// last-known provider key (no sync-over-async settings read).
    /// </summary>
    private ILlmService ResolveForDisplay()
    {
        return _resolveProvider(_lastProviderKey);
    }

    private async Task PrimeProviderKeyAsync()
    {
        try
        {
            _lastProviderKey = LlmProviderKeys.Normalize(await _settingsService.GetLlmProviderAsync());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not prime the LLM provider key from settings");
        }
    }
}
