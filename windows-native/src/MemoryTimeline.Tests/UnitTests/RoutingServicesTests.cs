using FluentAssertions;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.Services;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Tests for the F11 provider-routing facades: RoutingLlmService and
/// RoutingEmbeddingService re-read the provider setting on every call (apply
/// without restart), and the per-session usage tracker counts routed calls.
/// </summary>
public class RoutingServicesTests
{
    private readonly Mock<ISettingsService> _settingsServiceMock = new();
    private readonly Mock<ILlmService> _anthropicMock = new();
    private readonly Mock<ILlmService> _openAiCompatibleMock = new();
    private readonly LlmUsageTracker _usageTracker = new();

    private RoutingLlmService CreateLlmRouter()
    {
        return new RoutingLlmService(
            key => key == LlmProviderKeys.OpenAiCompatible ? _openAiCompatibleMock.Object : _anthropicMock.Object,
            _settingsServiceMock.Object,
            _usageTracker,
            new Mock<ILogger<RoutingLlmService>>().Object);
    }

    #region LlmProviderKeys / EmbeddingProviderKeys normalization

    [Theory]
    [InlineData("anthropic", LlmProviderKeys.Anthropic)]
    [InlineData("Anthropic", LlmProviderKeys.Anthropic)]
    [InlineData("openai-compatible", LlmProviderKeys.OpenAiCompatible)]
    [InlineData("openai_compatible", LlmProviderKeys.OpenAiCompatible)]
    [InlineData("ollama", LlmProviderKeys.OpenAiCompatible)]
    [InlineData("OLLAMA", LlmProviderKeys.OpenAiCompatible)]
    [InlineData("", LlmProviderKeys.Anthropic)]
    [InlineData(null, LlmProviderKeys.Anthropic)]
    [InlineData("something-unknown", LlmProviderKeys.Anthropic)]
    public void LlmProviderKeys_Normalize_MapsAliasesAndFallsBackToAnthropic(string? stored, string expected)
    {
        LlmProviderKeys.Normalize(stored).Should().Be(expected);
    }

    [Theory]
    [InlineData("local", EmbeddingProviderKeys.Local)]
    [InlineData("openai", EmbeddingProviderKeys.OpenAi)]
    [InlineData("OpenAI", EmbeddingProviderKeys.OpenAi)]
    [InlineData("", EmbeddingProviderKeys.Local)]
    [InlineData(null, EmbeddingProviderKeys.Local)]
    [InlineData("onnx-text-embedding", EmbeddingProviderKeys.Local)] // legacy seed value degrades gracefully
    public void EmbeddingProviderKeys_Normalize_UnknownValuesFallBackToLocal(string? stored, string expected)
    {
        EmbeddingProviderKeys.Normalize(stored).Should().Be(expected);
    }

    #endregion

    #region RoutingLlmService provider switching

    [Fact]
    public async Task ExtractEventsAsync_ProviderSettingChangesBetweenCalls_SwitchesInnerService()
    {
        // Arrange - the stored provider changes between the two calls
        _settingsServiceMock.SetupSequence(s => s.GetLlmProviderAsync())
            .ReturnsAsync("anthropic")  // ctor priming read
            .ReturnsAsync("anthropic")  // first call
            .ReturnsAsync("openai-compatible"); // second call

        var success = new EventExtractionResult { Success = true };
        _anthropicMock.Setup(s => s.ExtractEventsAsync(It.IsAny<string>(), It.IsAny<ExtractionContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(success);
        _anthropicMock.Setup(s => s.ProviderName).Returns("Anthropic Claude");
        _openAiCompatibleMock.Setup(s => s.ExtractEventsAsync(It.IsAny<string>(), It.IsAny<ExtractionContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(success);
        _openAiCompatibleMock.Setup(s => s.ProviderName).Returns("OpenAI-compatible endpoint");

        var router = CreateLlmRouter();

        // Act
        await router.ExtractEventsAsync("first transcript");
        await router.ExtractEventsAsync("second transcript");

        // Assert - each inner saw exactly one call; no restart was needed
        _anthropicMock.Verify(s => s.ExtractEventsAsync("first transcript", null, It.IsAny<CancellationToken>()), Times.Once);
        _openAiCompatibleMock.Verify(s => s.ExtractEventsAsync("second transcript", null, It.IsAny<CancellationToken>()), Times.Once);

        // Display metadata reflects the provider of the most recent call
        router.ProviderName.Should().Be("OpenAI-compatible endpoint");
    }

    [Fact]
    public async Task ExtractEventsAsync_AliasProviderValue_RoutesToOpenAiCompatible()
    {
        // Arrange - "ollama" is an accepted alias for openai-compatible
        _settingsServiceMock.Setup(s => s.GetLlmProviderAsync()).ReturnsAsync("ollama");
        _openAiCompatibleMock.Setup(s => s.ExtractEventsAsync(It.IsAny<string>(), It.IsAny<ExtractionContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EventExtractionResult { Success = true });
        _openAiCompatibleMock.Setup(s => s.ProviderName).Returns("OpenAI-compatible endpoint");

        var router = CreateLlmRouter();

        // Act
        await router.ExtractEventsAsync("transcript");

        // Assert
        _openAiCompatibleMock.Verify(s => s.ExtractEventsAsync("transcript", null, It.IsAny<CancellationToken>()), Times.Once);
        _anthropicMock.Verify(
            s => s.ExtractEventsAsync(It.IsAny<string>(), It.IsAny<ExtractionContext?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompleteAsync_DelegatesToCurrentProvider()
    {
        // Arrange
        _settingsServiceMock.Setup(s => s.GetLlmProviderAsync()).ReturnsAsync("anthropic");
        _anthropicMock.Setup(s => s.CompleteAsync("system", "user", 1234, It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer");
        _anthropicMock.Setup(s => s.ProviderName).Returns("Anthropic Claude");

        var router = CreateLlmRouter();

        // Act
        var response = await router.CompleteAsync("system", "user", 1234);

        // Assert
        response.Should().Be("answer");
        _anthropicMock.Verify(s => s.CompleteAsync("system", "user", 1234, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Usage tracker

    [Fact]
    public async Task UsageTracker_CountsRoutedCallsAndTokens()
    {
        // Arrange - extraction reports real token usage; completion is estimated
        _settingsServiceMock.Setup(s => s.GetLlmProviderAsync()).ReturnsAsync("anthropic");
        _anthropicMock.Setup(s => s.ProviderName).Returns("Anthropic Claude");
        _anthropicMock.Setup(s => s.ExtractEventsAsync(It.IsAny<string>(), It.IsAny<ExtractionContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EventExtractionResult
            {
                Success = true,
                TokenUsage = new TokenUsage { InputTokens = 60, OutputTokens = 40 }
            });
        _anthropicMock.Setup(s => s.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("answer");

        var router = CreateLlmRouter();

        // Act
        await router.ExtractEventsAsync("transcript");
        await router.CompleteAsync("sys", "user prompt");

        // Assert - 2 calls; 100 real tokens plus a non-zero estimate for the completion
        _usageTracker.CallCount.Should().Be(2);
        _usageTracker.EstimatedTokens.Should().BeGreaterThan(100);
    }

    [Fact]
    public void UsageTracker_EstimateTokens_UsesFourCharsPerToken()
    {
        LlmUsageTracker.EstimateTokens(null).Should().Be(0);
        LlmUsageTracker.EstimateTokens("").Should().Be(0);
        LlmUsageTracker.EstimateTokens("abcd").Should().Be(1);
        LlmUsageTracker.EstimateTokens(new string('x', 401)).Should().Be(101);
    }

    #endregion

    #region RoutingEmbeddingService provider switching

    [Fact]
    public async Task GenerateEmbeddingAsync_ProviderSettingChangesBetweenCalls_SwitchesInnerService()
    {
        // Arrange
        var localMock = new Mock<IEmbeddingService>();
        localMock.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>()))
            .ReturnsAsync(new float[384]);
        localMock.Setup(e => e.EmbeddingDimension).Returns(384);
        var openAiMock = new Mock<IEmbeddingService>();
        openAiMock.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>()))
            .ReturnsAsync(new float[1536]);
        openAiMock.Setup(e => e.EmbeddingDimension).Returns(1536);

        var settingsMock = new Mock<ISettingsService>();
        settingsMock.SetupSequence(s => s.GetSettingAsync<string>(SettingKeys.EmbeddingProvider, It.IsAny<string>()))
            .ReturnsAsync("local")   // ctor priming read
            .ReturnsAsync("local")   // first call
            .ReturnsAsync("openai"); // second call

        var router = new RoutingEmbeddingService(
            key => key == EmbeddingProviderKeys.OpenAi ? openAiMock.Object : localMock.Object,
            settingsMock.Object,
            new Mock<ILogger<RoutingEmbeddingService>>().Object);

        // Act
        var first = await router.GenerateEmbeddingAsync("text one");
        var second = await router.GenerateEmbeddingAsync("text two");

        // Assert
        first.Should().HaveCount(384);
        second.Should().HaveCount(1536);
        localMock.Verify(e => e.GenerateEmbeddingAsync("text one"), Times.Once);
        openAiMock.Verify(e => e.GenerateEmbeddingAsync("text two"), Times.Once);

        // Display metadata reflects the provider of the most recent call
        router.EmbeddingDimension.Should().Be(1536);
    }

    #endregion
}
