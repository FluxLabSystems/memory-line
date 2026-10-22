using System.Text.Json;
using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// <c>rag_similarity_threshold</c> was seeded (0.75) and read by nothing;
/// RagService hard-coded its own floors. The configured value is now the
/// default for similar-event retrieval, validated to 0..1 and re-read per call.
/// </summary>
public class RagThresholdSettingTests
{
    private readonly Mock<IEmbeddingService> _embeddingService = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly TestDbContextFactory _contextFactory = TestDbContextFactory.CreateInMemory();

    private RagService CreateService(bool withSettings = true) => new(
        _embeddingService.Object,
        new Mock<IEventService>().Object,
        _contextFactory,
        new Mock<ICrossReferenceRepository>().Object,
        NullLogger<RagService>.Instance,
        withSettings ? _settings.Object : null);

    private void SetThreshold(string value) =>
        _settings.Setup(s => s.GetSettingAsync<string>(SettingKeys.RagSimilarityThreshold, It.IsAny<string>()))
            .ReturnsAsync(value);

    [Theory]
    [InlineData("0.75", 0.75)]
    [InlineData("0.5", 0.5)]
    [InlineData("1", 1.0)]
    [InlineData("0", 0.0)]
    [InlineData("", RagService.DefaultSimilarityThreshold)]
    [InlineData("1.5", RagService.DefaultSimilarityThreshold)]
    [InlineData("-0.2", RagService.DefaultSimilarityThreshold)]
    [InlineData("high", RagService.DefaultSimilarityThreshold)]
    public async Task GetSimilarityThresholdAsync_ValidatesTheRow(string stored, double expected)
    {
        SetThreshold(stored);

        (await CreateService().GetSimilarityThresholdAsync()).Should().Be(expected);
    }

    [Fact]
    public async Task GetSimilarityThresholdAsync_NoSettingsService_UsesTheOldHardCodedFloor()
    {
        (await CreateService(withSettings: false).GetSimilarityThresholdAsync()).Should().Be(0.7);
    }

    [Fact]
    public async Task FindSimilarEventsAsync_NoThresholdGiven_UsesTheConfiguredFloor()
    {
        // Arrange - one source embedding, one candidate, and a floor of 0.9
        SetThreshold("0.9");
        await SeedEmbeddingsAsync();
        _embeddingService.Setup(e => e.EmbeddingDimension).Returns(3);
        _embeddingService
            .Setup(e => e.FindKNearestNeighbors(
                It.IsAny<float[]>(), It.IsAny<IEnumerable<(string id, float[] embedding)>>(), It.IsAny<int>(), It.IsAny<double>()))
            .Returns(new List<SimilarityResult>());

        // Act
        await CreateService().FindSimilarEventsAsync("source");

        // Assert
        _embeddingService.Verify(e => e.FindKNearestNeighbors(
            It.IsAny<float[]>(), It.IsAny<IEnumerable<(string id, float[] embedding)>>(), 10, 0.9), Times.Once);
    }

    [Fact]
    public async Task FindSimilarEventsAsync_ExplicitThreshold_WinsOverTheSetting()
    {
        SetThreshold("0.9");
        await SeedEmbeddingsAsync();
        _embeddingService.Setup(e => e.EmbeddingDimension).Returns(3);
        _embeddingService
            .Setup(e => e.FindKNearestNeighbors(
                It.IsAny<float[]>(), It.IsAny<IEnumerable<(string id, float[] embedding)>>(), It.IsAny<int>(), It.IsAny<double>()))
            .Returns(new List<SimilarityResult>());

        await CreateService().FindSimilarEventsAsync("source", topK: 5, threshold: 0.2);

        _embeddingService.Verify(e => e.FindKNearestNeighbors(
            It.IsAny<float[]>(), It.IsAny<IEnumerable<(string id, float[] embedding)>>(), 5, 0.2), Times.Once);
    }

    private async Task SeedEmbeddingsAsync()
    {
        await using var context = _contextFactory.CreateDbContext();
        context.Events.AddRange(
            new Event { EventId = "source", Title = "Source", StartDate = new DateTime(2024, 1, 1), Category = EventCategory.Other },
            new Event { EventId = "other", Title = "Other", StartDate = new DateTime(2024, 2, 1), Category = EventCategory.Other });
        context.EventEmbeddings.AddRange(
            new EventEmbedding { EmbeddingId = "e1", EventId = "source", EmbeddingVector = JsonSerializer.Serialize(new float[] { 1, 0, 0 }), EmbeddingProvider = "local", EmbeddingModel = "m", EmbeddingDimension = 3 },
            new EventEmbedding { EmbeddingId = "e2", EventId = "other", EmbeddingVector = JsonSerializer.Serialize(new float[] { 0, 1, 0 }), EmbeddingProvider = "local", EmbeddingModel = "m", EmbeddingDimension = 3 });
        await context.SaveChangesAsync();
    }
}
