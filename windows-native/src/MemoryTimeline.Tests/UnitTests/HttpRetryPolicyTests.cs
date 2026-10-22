using System.Net;
using System.Text;
using FluentAssertions;
using MemoryTimeline.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Polly was referenced and unused. <see cref="HttpRetryPolicy"/> now wraps the
/// provider and geocoder calls: bounded, jittered retry on transport failures
/// and transient status codes, never on the caller's cancellation or on a
/// client error, with the final outcome handed back unchanged.
/// </summary>
public class HttpRetryPolicyTests
{
    [Fact]
    public async Task Responses_TransientStatusThenSuccess_RetriesAndReturnsTheSuccess()
    {
        // Arrange - 503, 503, then 200
        var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        var client = new HttpClient(handler);
        var pipeline = HttpRetryPolicy.CreateForResponses(NullLogger.Instance, "test", baseDelay: TimeSpan.Zero);

        // Act
        using var response = await pipeline.ExecuteAsync(
            async token => await client.GetAsync("http://localhost/x", token), CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Calls.Should().Be(3);
    }

    [Fact]
    public async Task Responses_ClientError_IsNotRetried()
    {
        var handler = new SequenceHandler(() => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var client = new HttpClient(handler);
        var pipeline = HttpRetryPolicy.CreateForResponses(null, "test", baseDelay: TimeSpan.Zero);

        using var response = await pipeline.ExecuteAsync(
            async token => await client.GetAsync("http://localhost/x", token), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Responses_StillFailingAfterAllRetries_ReturnsTheLastResponseRatherThanThrowing()
    {
        // The existing EnsureSuccessStatusCode call sites keep their meaning.
        var handler = new SequenceHandler(() => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var client = new HttpClient(handler);
        var pipeline = HttpRetryPolicy.CreateForResponses(null, "test", baseDelay: TimeSpan.Zero);

        using var response = await pipeline.ExecuteAsync(
            async token => await client.GetAsync("http://localhost/x", token), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        handler.Calls.Should().Be(1 + HttpRetryPolicy.MaxRetryAttempts);
    }

    [Fact]
    public async Task Responses_TransportExceptionThenSuccess_Retries()
    {
        var handler = new SequenceHandler(
            () => throw new HttpRequestException("connection reset"),
            () => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler);
        var pipeline = HttpRetryPolicy.CreateForResponses(null, "test", baseDelay: TimeSpan.Zero);

        using var response = await pipeline.ExecuteAsync(
            async token => await client.GetAsync("http://localhost/x", token), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Responses_CallerCancelled_IsNotRetried()
    {
        // Arrange - the handler observes the token being cancelled mid-flight
        using var cts = new CancellationTokenSource();
        var handler = new SequenceHandler(() =>
        {
            cts.Cancel();
            throw new TaskCanceledException("cancelled by caller");
        });
        var client = new HttpClient(handler);
        var pipeline = HttpRetryPolicy.CreateForResponses(null, "test", baseDelay: TimeSpan.Zero);

        // Act
        var act = () => pipeline.ExecuteAsync(
            async token => await client.GetAsync("http://localhost/x", token), cts.Token).AsTask();

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Responses_TooManyRequests_RetriedByDefaultButNotForNominatim()
    {
        var retrying = HttpRetryPolicy.CreateForResponses(null, "default", baseDelay: TimeSpan.Zero);
        var backingOff = HttpRetryPolicy.CreateForResponses(null, "nominatim", retryTooManyRequests: false, baseDelay: TimeSpan.Zero);

        var handler1 = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            () => new HttpResponseMessage(HttpStatusCode.OK));
        using var r1 = await retrying.ExecuteAsync(
            async token => await new HttpClient(handler1).GetAsync("http://localhost/x", token), CancellationToken.None);
        r1.StatusCode.Should().Be(HttpStatusCode.OK);
        handler1.Calls.Should().Be(2);

        var handler2 = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            () => new HttpResponseMessage(HttpStatusCode.OK));
        using var r2 = await backingOff.ExecuteAsync(
            async token => await new HttpClient(handler2).GetAsync("http://localhost/x", token), CancellationToken.None);
        r2.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        handler2.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Exceptions_TransportFailureThenSuccess_Retries()
    {
        var attempts = 0;
        var pipeline = HttpRetryPolicy.CreateForExceptions(null, "sdk", baseDelay: TimeSpan.Zero);

        var result = await pipeline.ExecuteAsync(token =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new HttpRequestException("reset");
            }

            return ValueTask.FromResult("answer");
        }, CancellationToken.None);

        result.Should().Be("answer");
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task Exceptions_NonTransportFailure_IsNotRetried()
    {
        var attempts = 0;
        var pipeline = HttpRetryPolicy.CreateForExceptions(null, "sdk", baseDelay: TimeSpan.Zero);

        var act = () => pipeline.ExecuteAsync<string>(token =>
        {
            attempts++;
            throw new InvalidOperationException("bad key");
        }, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task OpenAiCompatibleService_TransientFailure_StillExtracts()
    {
        // Arrange - the whole service path, Ollama answering 503 once
        var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"events\\\":[],\\\"overallConfidence\\\":0.4}\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}",
                    Encoding.UTF8, "application/json"),
            });
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync<string>(SettingKeys.LlmBaseUrl, It.IsAny<string>())).ReturnsAsync("http://localhost:11434/v1");
        settings.Setup(s => s.GetLlmModelAsync()).ReturnsAsync("llama3");
        var service = new OpenAiCompatibleLlmService(
            new HttpClient(handler), settings.Object, NullLogger<OpenAiCompatibleLlmService>.Instance);

        // Act
        var result = await service.ExtractEventsAsync("transcript");

        // Assert
        result.Success.Should().BeTrue();
        handler.Calls.Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.OK, false)]
    public void IsTransient_StatusCodes(HttpStatusCode code, bool expected)
    {
        HttpRetryPolicy.IsTransient(code).Should().Be(expected);
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage>[] _responders;

        public int Calls { get; private set; }

        public SequenceHandler(params Func<HttpResponseMessage>[] responders)
        {
            _responders = responders;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var responder = _responders[Math.Min(Calls, _responders.Length - 1)];
            Calls++;
            return Task.FromResult(responder());
        }
    }
}
