using System.Net;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Bounded, jittered retry for the network calls Core makes — the LLM and
/// embedding providers and the geocoder. Built on Polly 8's
/// <see cref="ResiliencePipeline"/> inside Core rather than as HttpClient
/// handlers in the head, so a typed client, a hand-built <c>HttpClient</c> in a
/// test and the Anthropic SDK (which owns its own HttpClient) all get the same
/// behaviour from the same place.
///
/// <para><b>What is retried.</b> Transport failures (<see cref="HttpRequestException"/>,
/// <see cref="IOException"/>), an HttpClient timeout (a
/// <see cref="TaskCanceledException"/> while the caller's own token is NOT
/// cancelled) and the transient status codes — 408, 429 and 5xx. A 4xx that
/// is not one of those is the caller's mistake and comes straight back; a
/// cancellation by the caller is never retried. Three retries at most, 500 ms
/// doubling with jitter and capped at 4 s, so the worst case is a few seconds,
/// not a hung queue item.</para>
///
/// <para><b>What is not.</b> No circuit breaker and no retry on success-with-
/// garbage: a 200 with malformed JSON is a provider bug, not weather. A
/// superseded failed response is disposed on retry; the final outcome — the
/// last response, or the last exception — is returned to the caller exactly
/// as the un-retried call would have, so <c>EnsureSuccessStatusCode</c> and
/// the existing error handling keep their meaning.</para>
/// </summary>
public static class HttpRetryPolicy
{
    /// <summary>Retries after the first attempt.</summary>
    public const int MaxRetryAttempts = 3;

    /// <summary>First delay; doubles per attempt with jitter.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Ceiling on any one delay.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(4);

    /// <summary>
    /// A pipeline for calls that return the <see cref="HttpResponseMessage"/>:
    /// retries transport exceptions and transient status codes.
    /// </summary>
    /// <param name="logger">Receives one warning per retry; null for silence.</param>
    /// <param name="operationName">Names the call in that warning.</param>
    /// <param name="retryTooManyRequests">
    /// Whether 429 is retried. Off for Nominatim, whose usage policy asks
    /// clients to back off rather than try again on a rate-limit answer.
    /// </param>
    /// <param name="baseDelay">Overrides <see cref="BaseDelay"/>; tests pass zero.</param>
    public static ResiliencePipeline<HttpResponseMessage> CreateForResponses(
        ILogger? logger,
        string operationName,
        bool retryTooManyRequests = true,
        TimeSpan? baseDelay = null)
    {
        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = MaxRetryAttempts,
                Delay = baseDelay ?? BaseDelay,
                MaxDelay = MaxDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = args =>
                {
                    if (args.Context.CancellationToken.IsCancellationRequested)
                    {
                        return PredicateResult.False();
                    }

                    if (args.Outcome.Exception is { } exception)
                    {
                        return IsTransient(exception) ? PredicateResult.True() : PredicateResult.False();
                    }

                    return args.Outcome.Result is { } response && IsTransient(response.StatusCode, retryTooManyRequests)
                        ? PredicateResult.True()
                        : PredicateResult.False();
                },
                OnRetry = args =>
                {
                    // The response we are about to replace would otherwise leak
                    // its connection until finalized.
                    args.Outcome.Result?.Dispose();
                    logger?.LogWarning(
                        args.Outcome.Exception,
                        "{Operation} failed transiently ({Reason}); retry {Attempt} of {Max} in {Delay:F1}s",
                        operationName,
                        args.Outcome.Exception?.GetType().Name ?? ((int?)args.Outcome.Result?.StatusCode)?.ToString() ?? "unknown",
                        args.AttemptNumber + 1,
                        MaxRetryAttempts,
                        args.RetryDelay.TotalSeconds);
                    return default;
                },
            })
            .Build();
    }

    /// <summary>
    /// A pipeline for calls that hide the response behind an SDK (the
    /// Anthropic client): retries transport exceptions only, since a status
    /// code never reaches us as a value.
    /// </summary>
    public static ResiliencePipeline CreateForExceptions(
        ILogger? logger,
        string operationName,
        TimeSpan? baseDelay = null)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = MaxRetryAttempts,
                Delay = baseDelay ?? BaseDelay,
                MaxDelay = MaxDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = args =>
                    !args.Context.CancellationToken.IsCancellationRequested
                    && args.Outcome.Exception is { } exception
                    && IsTransient(exception)
                        ? PredicateResult.True()
                        : PredicateResult.False(),
                OnRetry = args =>
                {
                    logger?.LogWarning(
                        args.Outcome.Exception,
                        "{Operation} failed transiently; retry {Attempt} of {Max} in {Delay:F1}s",
                        operationName, args.AttemptNumber + 1, MaxRetryAttempts, args.RetryDelay.TotalSeconds);
                    return default;
                },
            })
            .Build();
    }

    /// <summary>Whether a status code is worth a second try.</summary>
    public static bool IsTransient(HttpStatusCode statusCode, bool retryTooManyRequests = true)
    {
        var code = (int)statusCode;
        return code >= 500
            || statusCode == HttpStatusCode.RequestTimeout
            || (retryTooManyRequests && statusCode == HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// Whether an exception is a transport failure worth a second try. A
    /// <see cref="TaskCanceledException"/> here is HttpClient's timeout: the
    /// caller's own cancellation is excluded before this is consulted.
    /// </summary>
    public static bool IsTransient(Exception exception) =>
        exception is HttpRequestException or IOException or TaskCanceledException;
}
