using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using MemoryTimeline.Data.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Diagnostics;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// LLM service using Anthropic Claude for event extraction.
/// Uses Anthropic.SDK v5.8.0 (unofficial but well-maintained SDK).
/// </summary>
public class AnthropicLlmService : ILlmService
{
    private readonly ISettingsService _settingsService;
    private readonly ILogger<AnthropicLlmService> _logger;
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private AnthropicClient? _client;
    private string? _clientApiKey;
    private string? _model;

    public string ProviderName => "Anthropic Claude";
    public string ModelName => _model ?? AnthropicModels.Claude35Sonnet;
    public bool RequiresInternet => true;

    public AnthropicLlmService(
        ISettingsService settingsService,
        ILogger<AnthropicLlmService> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    /// Extracts events from transcript text.
    /// </summary>
    public async Task<EventExtractionResult> ExtractEventsAsync(string transcript)
    {
        return await ExtractEventsAsync(transcript, null);
    }

    /// <summary>
    /// Extracts events from transcript with context.
    /// </summary>
    public async Task<EventExtractionResult> ExtractEventsAsync(string transcript, ExtractionContext? context)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Starting event extraction from transcript ({Length} chars)", transcript.Length);

            // Initialize client if needed
            await InitializeClientAsync();

            if (_client == null)
            {
                return new EventExtractionResult
                {
                    Success = false,
                    ErrorMessage = "Anthropic API client not initialized. Check API key in settings."
                };
            }

            // Build the prompt
            var prompt = BuildExtractionPrompt(transcript, context);

            // llm_max_tokens / llm_temperature, re-read per call like the key
            // and model. The seeded temperature (0.3) keeps structured output
            // consistent; the budget bounds the extraction response.
            var generation = await LlmPromptSupport.ReadGenerationSettingsAsync(_settingsService);

            _logger.LogDebug("Sending request to Claude API");

            // Call Claude API using the SDK (Anthropic.SDK v5.8.0 API)
            var messages = new List<Message>
            {
                new Message(RoleType.User, prompt)
            };

            var parameters = new MessageParameters
            {
                Messages = messages,
                Model = ModelName,
                MaxTokens = generation.MaxTokens,
                Temperature = (decimal)generation.Temperature,
                Stream = false
            };

            // CORRECT API: GetClaudeMessageAsync takes only parameters (not model name)
            var response = await _client.Messages.GetClaudeMessageAsync(parameters);

            _logger.LogInformation("Received response from Claude API");

            // Extract JSON from response
            var jsonResponse = ExtractJsonFromResponse(response);

            if (string.IsNullOrEmpty(jsonResponse))
            {
                return new EventExtractionResult
                {
                    Success = false,
                    ErrorMessage = "No valid JSON found in Claude's response"
                };
            }

            // Parse the JSON response (guard against malformed LLM output)
            LlmPromptSupport.EventExtractionResponse? extractedData;
            try
            {
                extractedData = JsonSerializer.Deserialize<LlmPromptSupport.EventExtractionResponse>(
                    jsonResponse, LlmPromptSupport.JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Malformed JSON in Claude extraction response");
                return new EventExtractionResult
                {
                    Success = false,
                    ErrorMessage = "Claude returned malformed JSON that could not be parsed"
                };
            }

            if (extractedData == null)
            {
                return new EventExtractionResult
                {
                    Success = false,
                    ErrorMessage = "Failed to parse extraction response"
                };
            }

            stopwatch.Stop();

            var result = new EventExtractionResult
            {
                Events = extractedData.Events ?? new List<ExtractedEvent>(),
                OverallConfidence = extractedData.OverallConfidence,
                Success = true,
                ProcessingDurationSeconds = stopwatch.Elapsed.TotalSeconds,
                TokenUsage = new TokenUsage
                {
                    InputTokens = response.Usage?.InputTokens ?? 0,
                    OutputTokens = response.Usage?.OutputTokens ?? 0,
                    EstimatedCost = CalculateEstimatedCost(
                        response.Usage?.InputTokens ?? 0,
                        response.Usage?.OutputTokens ?? 0)
                }
            };

            _logger.LogInformation("Successfully extracted {Count} events (confidence: {Confidence})",
                result.Events.Count, result.OverallConfidence);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during event extraction");

            stopwatch.Stop();

            return new EventExtractionResult
            {
                Success = false,
                ErrorMessage = $"Extraction failed: {ex.Message}",
                ProcessingDurationSeconds = stopwatch.Elapsed.TotalSeconds
            };
        }
    }

    /// <summary>
    /// Runs a single free-form completion against the configured Claude model.
    /// Reuses the same client-initialization path as extraction (settings are
    /// re-read on every call, so key/model changes apply immediately).
    /// </summary>
    /// <remarks>
    /// The system prompt is prepended to the user message content (clearly
    /// delimited) rather than using dedicated system-message plumbing: the only
    /// Anthropic.SDK call shape proven in this codebase is a plain user
    /// <see cref="Message"/> passed to GetClaudeMessageAsync. Likewise the SDK
    /// call is not visibly cancellable here, so the <paramref name="ct"/> is
    /// honored at the call boundaries instead.
    /// </remarks>
    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens = 2000, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await InitializeClientAsync();

        if (_client == null)
        {
            throw new ConfigurationException("Anthropic API key not configured — add it in Settings");
        }

        var combinedPrompt = string.IsNullOrWhiteSpace(systemPrompt)
            ? userPrompt
            : $"<system_instructions>\n{systemPrompt}\n</system_instructions>\n\n{userPrompt}";

        // The caller owns the output budget here (each feature knows how long
        // its answer may be); only the temperature comes from settings.
        var generation = await LlmPromptSupport.ReadGenerationSettingsAsync(_settingsService);

        var parameters = new MessageParameters
        {
            Messages = new List<Message>
            {
                new Message(RoleType.User, combinedPrompt)
            },
            Model = ModelName,
            MaxTokens = maxTokens,
            Temperature = (decimal)generation.Temperature,
            Stream = false
        };

        _logger.LogInformation("Sending completion request to Claude API ({Length} chars)", combinedPrompt.Length);

        ct.ThrowIfCancellationRequested();
        // Forward the token so cancelling actually aborts the in-flight HTTP
        // request instead of only checking at the call boundaries.
        var response = await _client.Messages.GetClaudeMessageAsync(parameters, ct);
        ct.ThrowIfCancellationRequested();

        var textContent = response?.Message?.Content?
            .FirstOrDefault(c => c is TextContent) as TextContent;

        if (textContent == null || string.IsNullOrWhiteSpace(textContent.Text))
        {
            throw new InvalidOperationException("Claude returned an empty response.");
        }

        return textContent.Text.Trim();
    }

    #region Private Methods

    /// <summary>
    /// Ensures the Anthropic client matches the CURRENT settings.
    /// The (apiKey, model) pair is re-read from settings on every call and the
    /// client is rebuilt whenever either changes — so updating the API key or
    /// model in Settings takes effect immediately instead of being cached for
    /// the lifetime of the singleton.
    /// </summary>
    private async Task InitializeClientAsync()
    {
        try
        {
            var apiKey = await _settingsService.GetSettingAsync<string>(SettingKeys.AnthropicApiKey, string.Empty);
            var model = await _settingsService.GetLlmModelAsync();

            await _clientLock.WaitAsync();
            try
            {
                if (string.IsNullOrEmpty(apiKey))
                {
                    if (_client != null)
                    {
                        _logger.LogWarning("Anthropic API key removed from settings; discarding client");
                    }
                    _client = null;
                    _clientApiKey = null;
                    _model = model;
                    return;
                }

                // Rebuild only when the key or model actually changed
                if (_client == null || _clientApiKey != apiKey || _model != model)
                {
                    var authentication = new APIAuthentication(apiKey);
                    _client = new AnthropicClient(authentication);
                    _clientApiKey = apiKey;
                    _model = model;

                    _logger.LogInformation("Anthropic client (re)initialized with model: {Model}", _model ?? ModelName);
                }
            }
            finally
            {
                _clientLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing Anthropic client");
        }
    }

    /// <summary>
    /// Builds the extraction prompt. The prompt text is shared verbatim with the
    /// OpenAI-compatible provider via <see cref="LlmPromptSupport"/> — never fork it.
    /// </summary>
    private static string BuildExtractionPrompt(string transcript, ExtractionContext? context)
    {
        return LlmPromptSupport.BuildExtractionPrompt(transcript, context);
    }

    /// <summary>
    /// Extracts JSON from Claude's response (handles markdown code blocks).
    /// Anthropic.SDK v5.8.0 returns MessageResponse with Content list of ContentBase
    /// objects; the fence-stripping itself is shared with the OpenAI-compatible
    /// provider via <see cref="LlmPromptSupport.ExtractJsonFromText"/>.
    /// </summary>
    private string ExtractJsonFromResponse(MessageResponse response)
    {
        if (response?.Message?.Content == null || !response.Message.Content.Any())
            return string.Empty;

        try
        {
            // Get the first text content block
            var textContent = response.Message.Content
                .FirstOrDefault(c => c is TextContent) as TextContent;

            if (textContent == null || string.IsNullOrEmpty(textContent.Text))
                return string.Empty;

            return LlmPromptSupport.ExtractJsonFromText(textContent.Text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting JSON from response");
            return string.Empty;
        }
    }

    /// <summary>
    /// Calculates estimated API cost based on token usage.
    /// Claude 3.5 Sonnet pricing: $3/MTok input, $15/MTok output
    /// </summary>
    private decimal CalculateEstimatedCost(int inputTokens, int outputTokens)
    {
        const decimal inputCostPerMillion = 3.00m;
        const decimal outputCostPerMillion = 15.00m;

        var inputCost = (inputTokens / 1_000_000m) * inputCostPerMillion;
        var outputCost = (outputTokens / 1_000_000m) * outputCostPerMillion;

        return inputCost + outputCost;
    }

    #endregion
}
