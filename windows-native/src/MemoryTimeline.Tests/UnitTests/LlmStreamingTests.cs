using FluentAssertions;
using MemoryTimeline.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Text;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Real token streaming through <see cref="ILlmService.StreamCompletionAsync"/>:
/// the OpenAI-compatible SSE protocol parsed by hand, the Anthropic Messages
/// SSE protocol parsed by Anthropic.SDK (fed through the service's internal
/// HttpClient seam), and the routing facade's delegation plus usage
/// accounting. Every test runs against a stub HTTP handler - no network.
/// </summary>
public class LlmStreamingTests
{
    private const string BaseUrl = "http://localhost:11434/v1";

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public int Calls { get; private set; }

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            LastRequestBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }

    private static HttpResponseMessage SseResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
    };

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> stream)
    {
        var chunks = new List<string>();
        await foreach (var chunk in stream)
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static async IAsyncEnumerable<string> Pieces(params string[] pieces)
    {
        foreach (var piece in pieces)
        {
            await Task.Yield();
            yield return piece;
        }
    }

    #region OpenAI-compatible (SSE parsed here)

    private static OpenAiCompatibleLlmService CreateOpenAiService(StubHttpMessageHandler handler, string baseUrl = BaseUrl)
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync<string>(SettingKeys.LlmBaseUrl, It.IsAny<string>())).ReturnsAsync(baseUrl);
        settings.Setup(s => s.GetLlmModelAsync()).ReturnsAsync("llama3");
        return new OpenAiCompatibleLlmService(
            new HttpClient(handler), settings.Object, new Mock<ILogger<OpenAiCompatibleLlmService>>().Object);
    }

    [Fact]
    public async Task OpenAi_StreamCompletionAsync_YieldsDeltasInOrderAndStopsAtDone()
    {
        // Arrange: a realistic chat-completions stream - a role-only opening
        // chunk, a keep-alive comment, a junk data line, a finish chunk that
        // still carries text, [DONE], and a chunk after [DONE] that must be
        // ignored.
        var body =
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n" +
            ": keep-alive\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n" +
            "data: this is not json\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" world\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"IGNORED\"}}]}\n\n";
        var handler = new StubHttpMessageHandler(_ => SseResponse(body));
        var service = CreateOpenAiService(handler);

        // Act
        var chunks = await CollectAsync(service.StreamCompletionAsync("be terse", "what happened?", maxTokens: 256));

        // Assert: the text, in order, nothing more - and a streaming request.
        chunks.Should().Equal("Hel", "lo", " world");
        handler.LastRequest!.RequestUri!.ToString().Should().Be(BaseUrl + "/chat/completions");
        handler.LastRequestBody.Should().Contain("\"stream\":true");
        handler.LastRequestBody.Should().Contain("\"role\":\"system\"");
        handler.LastRequestBody.Should().Contain("\"max_tokens\":256");
    }

    [Fact]
    public async Task OpenAi_StreamCompletionAsync_StreamWithoutText_ThrowsInvalidOperation()
    {
        // Arrange: only the role-only chunk, then [DONE].
        var body = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\ndata: [DONE]\n\n";
        var service = CreateOpenAiService(new StubHttpMessageHandler(_ => SseResponse(body)));

        // Act
        var act = () => CollectAsync(service.StreamCompletionAsync("s", "u"));

        // Assert: mirrors CompleteAsync's empty-response rule.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*empty response*");
    }

    [Fact]
    public async Task OpenAi_StreamCompletionAsync_MissingBaseUrl_ThrowsConfigurationExceptionWithoutRequest()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ => SseResponse("data: [DONE]\n\n"));
        var service = CreateOpenAiService(handler, baseUrl: " ");

        // Act
        var act = () => CollectAsync(service.StreamCompletionAsync("s", "u"));

        // Assert
        await act.Should().ThrowAsync<ConfigurationException>().WithMessage("*base URL*");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task OpenAi_StreamCompletionAsync_NonTransientHttpFailure_Throws()
    {
        // Arrange: 404 is not retried, so the failure surfaces at once.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = CreateOpenAiService(handler);

        // Act
        var act = () => CollectAsync(service.StreamCompletionAsync("s", "u"));

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Calls.Should().Be(1);
    }

    #endregion

    #region Anthropic (SSE parsed by Anthropic.SDK)

    private static AnthropicLlmService CreateAnthropicService(StubHttpMessageHandler handler, string apiKey = "sk-ant-test")
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync<string>(SettingKeys.AnthropicApiKey, It.IsAny<string>())).ReturnsAsync(apiKey);
        settings.Setup(s => s.GetLlmModelAsync()).ReturnsAsync("claude-3-5-sonnet-latest");
        return new AnthropicLlmService(
            settings.Object, new Mock<ILogger<AnthropicLlmService>>().Object, new HttpClient(handler));
    }

    [Fact]
    public async Task Anthropic_StreamCompletionAsync_YieldsTextDeltasFromTheSdkStream()
    {
        // Arrange: the Messages API event sequence. Only content_block_delta
        // events carry text; the rest must not leak into the stream.
        var body =
            "event: message_start\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-3-5-sonnet-latest\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":12,\"output_tokens\":1}}}\n\n" +
            "event: content_block_start\n" +
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
            "event: ping\n" +
            "data: {\"type\":\"ping\"}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hel\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"lo world\"}}\n\n" +
            "event: content_block_stop\n" +
            "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_delta\n" +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":5}}\n\n" +
            "event: message_stop\n" +
            "data: {\"type\":\"message_stop\"}\n\n";
        var handler = new StubHttpMessageHandler(_ => SseResponse(body));
        var service = CreateAnthropicService(handler);

        // Act
        var chunks = await CollectAsync(service.StreamCompletionAsync("be terse", "what happened?", maxTokens: 300));

        // Assert: the deltas, in order, and a streaming request to the Messages API.
        chunks.Should().Equal("Hel", "lo world");
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().EndWith("/messages");
        handler.LastRequestBody.Should().Contain("\"stream\":true");
        handler.LastRequestBody.Should().Contain("\"max_tokens\":300");
        handler.LastRequestBody.Should().Contain("system_instructions"); // the SDK JSON-escapes the angle brackets
    }

    [Fact]
    public async Task Anthropic_StreamCompletionAsync_NoApiKey_ThrowsConfigurationExceptionWithoutRequest()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ => SseResponse(string.Empty));
        var service = CreateAnthropicService(handler, apiKey: string.Empty);

        // Act
        var act = () => CollectAsync(service.StreamCompletionAsync("s", "u"));

        // Assert
        await act.Should().ThrowAsync<ConfigurationException>().WithMessage("*API key*");
        handler.Calls.Should().Be(0);
    }

    #endregion

    #region Routing facade

    [Fact]
    public async Task Routing_StreamCompletionAsync_DelegatesToTheCurrentProviderAndRecordsUsageAtTheEnd()
    {
        // Arrange
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetLlmProviderAsync()).ReturnsAsync("openai-compatible");

        var anthropic = new Mock<ILlmService>();
        var openAi = new Mock<ILlmService>();
        openAi.Setup(s => s.ProviderName).Returns("OpenAI-compatible endpoint");
        openAi.Setup(s => s.StreamCompletionAsync("sys", "user", 777, It.IsAny<CancellationToken>()))
            .Returns(Pieces("one ", "two"));

        var tracker = new LlmUsageTracker();
        var router = new RoutingLlmService(
            key => key == LlmProviderKeys.OpenAiCompatible ? openAi.Object : anthropic.Object,
            settings.Object,
            tracker,
            new Mock<ILogger<RoutingLlmService>>().Object);

        // Act
        var chunks = await CollectAsync(router.StreamCompletionAsync("sys", "user", 777));

        // Assert
        chunks.Should().Equal("one ", "two");
        anthropic.Verify(
            s => s.StreamCompletionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        tracker.CallCount.Should().Be(1);
        tracker.EstimatedTokens.Should().BeGreaterThan(0);
    }

    #endregion
}
