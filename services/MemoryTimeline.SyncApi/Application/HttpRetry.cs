using System.Net;

namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// A small bounded retry for outbound provider calls, in the spirit of the
/// Windows app's <c>HttpRetryPolicy</c> but without the Polly dependency:
/// up to three attempts, exponential back-off with jitter capped at two
/// seconds, retrying transport failures, timeouts and 408/429/5xx responses
/// and nothing else. Caller cancellation is never retried. A response that
/// is returned (even a 5xx on the last attempt) is the caller's to dispose.
/// </summary>
public sealed class HttpRetry
{
    /// <summary>Attempts in total, including the first.</summary>
    public const int Attempts = 3;

    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(2);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>The production policy: real delays.</summary>
    public static HttpRetry Default { get; } = new(Task.Delay);

    /// <param name="delay">How to wait between attempts; tests pass a no-op.</param>
    public HttpRetry(Func<TimeSpan, CancellationToken, Task> delay)
    {
        _delay = delay;
    }

    /// <summary>Whether a response status is worth another attempt.</summary>
    public static bool IsTransient(HttpStatusCode status)
        => status == HttpStatusCode.RequestTimeout
            || status == HttpStatusCode.TooManyRequests
            || (int)status >= 500;

    /// <summary>
    /// Sends a request built fresh for every attempt (a request message
    /// cannot be sent twice) and returns the first non-transient response.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request(), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception ex) when (attempt < Attempts && IsTransient(ex, cancellationToken))
            {
                await _delay(DelayFor(attempt), cancellationToken);
                continue;
            }

            if (attempt < Attempts && IsTransient(response.StatusCode))
            {
                response.Dispose();
                await _delay(DelayFor(attempt), cancellationToken);
                continue;
            }

            return response;
        }
    }

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
        => exception is HttpRequestException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    private static TimeSpan DelayFor(int attempt)
    {
        var exponential = BaseDelay * Math.Pow(2, attempt - 1);
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 100));
        var delay = exponential + jitter;
        return delay > MaxDelay ? MaxDelay : delay;
    }
}
