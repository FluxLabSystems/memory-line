using MemoryTimeline.SyncApi.Infrastructure;

namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// Chooses the <see cref="IDetourSuggestionProvider"/> from <see cref="DetourOptions"/>
/// at startup. Misconfiguration is refused before the host runs — a service
/// that silently fell back to "none" because a key was mistyped would look
/// exactly like one that was never configured, and the operator would learn
/// it only from a trip with no suggestions.
/// </summary>
public static class DetourProviderRegistration
{
    /// <summary>Name of the <see cref="IHttpClientFactory"/> client the adapters use.</summary>
    public const string HttpClientName = "detour-provider";

    /// <summary>Registers the options, the request budget and the selected provider.</summary>
    public static IServiceCollection AddDetourSuggestionProvider(this IServiceCollection services, DetourOptions options)
    {
        var provider = options.NormalizedProvider;
        if (!DetourProviderNames.All.Contains(provider))
        {
            throw new InvalidOperationException(
                $"{DetourOptions.SectionName}:Provider must be one of: {string.Join(", ", DetourProviderNames.All)}.");
        }

        if (provider != DetourProviderNames.None && string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                $"{DetourOptions.SectionName}:ApiKey is required when {DetourOptions.SectionName}:Provider is {provider}.");
        }

        if (options.DailyRequestCap < 0 || options.MaxCandidates < 1 || options.SearchRadiusMeters <= 0 || options.RequestTimeoutSeconds < 1)
        {
            throw new InvalidOperationException(
                $"{DetourOptions.SectionName}: DailyRequestCap must be >= 0, MaxCandidates >= 1, SearchRadiusMeters > 0 and RequestTimeoutSeconds >= 1.");
        }

        services.AddSingleton(options);
        services.AddSingleton(new DetourRequestBudget(options.DailyRequestCap));

        switch (provider)
        {
            case DetourProviderNames.Google:
                AddProviderHttpClient(services, options);
                services.AddSingleton<IDetourSuggestionProvider, GooglePlacesDetourProvider>();
                break;
            case DetourProviderNames.Apple:
                AddProviderHttpClient(services, options);
                services.AddSingleton<IDetourSuggestionProvider, AppleMapsDetourProvider>();
                break;
            default:
                services.AddSingleton<IDetourSuggestionProvider, NoDetourSuggestionProvider>();
                break;
        }

        return services;
    }

    /// <summary>
    /// A named, factory-managed client rather than a typed one: the adapters
    /// are singletons (they hold the daily budget and, for Apple, a cached
    /// access token), and a typed client captured by a singleton would defeat
    /// the factory's handler rotation. Creating a client per call from the
    /// factory is the pattern the factory documents for long-lived services.
    /// </summary>
    private static void AddProviderHttpClient(IServiceCollection services, DetourOptions options)
    {
        services.AddHttpClient(HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        });
    }
}
