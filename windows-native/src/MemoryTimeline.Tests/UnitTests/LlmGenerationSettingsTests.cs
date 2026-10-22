using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MemoryTimeline.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// <c>llm_max_tokens</c> and <c>llm_temperature</c> were seeded but never
/// read; both LLM providers hard-coded 4096 / 0.3. They now come from the
/// shared <see cref="LlmPromptSupport.ReadGenerationSettingsAsync"/> reader,
/// validated so a stray row can never silence the model, and the
/// OpenAI-compatible request body is checked end to end.
/// </summary>
public class LlmGenerationSettingsTests
{
    [Theory]
    [InlineData("4000", 4000)]
    [InlineData(" 1500 ", 1500)]
    [InlineData("", LlmPromptSupport.DefaultMaxTokens)]
    [InlineData(null, LlmPromptSupport.DefaultMaxTokens)]
    [InlineData("0", LlmPromptSupport.DefaultMaxTokens)]
    [InlineData("-5", LlmPromptSupport.DefaultMaxTokens)]
    [InlineData("lots", LlmPromptSupport.DefaultMaxTokens)]
    [InlineData("1.5", LlmPromptSupport.DefaultMaxTokens)]
    public void ParseMaxTokens_AcceptsPositiveIntegersOnly(string? text, int expected)
    {
        LlmPromptSupport.ParseMaxTokens(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("0.3", 0.3)]
    [InlineData("0", 0.0)]
    [InlineData("2", 2.0)]
    [InlineData("0.75", 0.75)]
    [InlineData("", LlmPromptSupport.DefaultTemperature)]
    [InlineData(null, LlmPromptSupport.DefaultTemperature)]
    [InlineData("2.5", LlmPromptSupport.DefaultTemperature)]
    [InlineData("-0.1", LlmPromptSupport.DefaultTemperature)]
    [InlineData("warm", LlmPromptSupport.DefaultTemperature)]
    [InlineData("NaN", LlmPromptSupport.DefaultTemperature)]
    public void ParseTemperature_AcceptsTheApiRangeOnly(string? text, double expected)
    {
        LlmPromptSupport.ParseTemperature(text).Should().Be(expected);
    }

    [Fact]
    public void ParseTemperature_IsCultureInvariant()
    {
        // "0,3" is not a decimal in the invariant culture; it must not become 3.
        LlmPromptSupport.ParseTemperature("0,3").Should().Be(LlmPromptSupport.DefaultTemperature);
    }

    [Fact]
    public async Task ReadGenerationSettingsAsync_ReadsBothRowsThroughTheSettingsService()
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync<string>(SettingKeys.LlmMaxTokens, It.IsAny<string>())).ReturnsAsync("2048");
        settings.Setup(s => s.GetSettingAsync<string>(SettingKeys.LlmTemperature, It.IsAny<string>())).ReturnsAsync("0.9");

        var generation = await LlmPromptSupport.ReadGenerationSettingsAsync(settings.Object);

        generation.MaxTokens.Should().Be(2048);
        generation.Temperature.Should().Be(0.9);
    }

    [Fact]
    public async Task OpenAiCompatible_ExtractEvents_SendsTheConfiguredBudgetAndTemperature()
    {
        // Arrange
        var handler = new CapturingHandler(ChatCompletionFixture("{\"events\":[],\"overallConfidence\":0.5}"));
        var settings = SettingsMock(maxTokens: "1500", temperature: "0.7");
        var service = new OpenAiCompatibleLlmService(
            new HttpClient(handler), settings.Object, NullLogger<OpenAiCompatibleLlmService>.Instance);

        // Act
        var result = await service.ExtractEventsAsync("I moved house in March.");

        // Assert
        result.Success.Should().BeTrue();
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(1500);
        body.RootElement.GetProperty("temperature").GetDouble().Should().Be(0.7);
    }

    [Fact]
    public async Task OpenAiCompatible_Complete_KeepsTheCallersBudgetButUsesTheConfiguredTemperature()
    {
        // Arrange
        var handler = new CapturingHandler(ChatCompletionFixture("fine"));
        var settings = SettingsMock(maxTokens: "1500", temperature: "0.05");
        var service = new OpenAiCompatibleLlmService(
            new HttpClient(handler), settings.Object, NullLogger<OpenAiCompatibleLlmService>.Instance);

        // Act
        await service.CompleteAsync("be terse", "hello", maxTokens: 512);

        // Assert - each feature owns its own answer length; only the temperature is global
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(512);
        body.RootElement.GetProperty("temperature").GetDouble().Should().Be(0.05);
    }

    [Fact]
    public async Task OpenAiCompatible_RowsAbsent_FallsBackToTheOldHardCodedValues()
    {
        // Arrange - a settings service that knows nothing about either row
        var handler = new CapturingHandler(ChatCompletionFixture("{\"events\":[],\"overallConfidence\":0.5}"));
        var settings = SettingsMock(maxTokens: "", temperature: "");
        var service = new OpenAiCompatibleLlmService(
            new HttpClient(handler), settings.Object, NullLogger<OpenAiCompatibleLlmService>.Instance);

        // Act
        await service.ExtractEventsAsync("transcript");

        // Assert
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(4096);
        body.RootElement.GetProperty("temperature").GetDouble().Should().Be(0.3);
    }

    #region Helpers

    private static Mock<ISettingsService> SettingsMock(string maxTokens, string temperature)
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.GetSettingAsync<string>(SettingKeys.LlmBaseUrl, It.IsAny<string>()))
            .ReturnsAsync("http://localhost:11434/v1");
        settings.Setup(s => s.GetLlmModelAsync()).ReturnsAsync("llama3");
        settings.Setup(s => s.GetSettingAsync<string>(SettingKeys.LlmMaxTokens, It.IsAny<string>())).ReturnsAsync(maxTokens);
        settings.Setup(s => s.GetSettingAsync<string>(SettingKeys.LlmTemperature, It.IsAny<string>())).ReturnsAsync(temperature);
        return settings;
    }

    private static string ChatCompletionFixture(string content) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-fixture",
        choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 },
    });

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _responseJson;

        public string? LastRequestBody { get; private set; }

        public CapturingHandler(string responseJson)
        {
            _responseJson = responseJson;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    #endregion
}
