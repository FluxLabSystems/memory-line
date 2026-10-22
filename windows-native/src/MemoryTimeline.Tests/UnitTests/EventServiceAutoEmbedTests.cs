using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// <c>auto_generate_embeddings</c> was seeded (true) and read by nothing. It
/// now gates the background embedding EventService starts for a created
/// event (and, through <see cref="IEventService.IsAutoEmbeddingEnabledAsync"/>,
/// the one the approval path starts); explicit re-embedding is unaffected.
/// </summary>
public class EventServiceAutoEmbedTests
{
    private readonly TestDbContextFactory _contextFactory = TestDbContextFactory.CreateInMemory();
    private readonly Mock<IEmbeddingService> _embeddingService = new();
    private readonly Mock<ISettingsService> _settings = new();

    public EventServiceAutoEmbedTests()
    {
        _embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>()))
            .ReturnsAsync(new float[] { 0.1f, 0.2f, 0.3f });
        _embeddingService.Setup(e => e.ProviderKey).Returns("local");
        _embeddingService.Setup(e => e.ModelName).Returns("test-model");
    }

    private EventService CreateService(bool withSettings = true) => new(
        new EventRepository(_contextFactory),
        _contextFactory,
        NullLogger<EventService>.Instance,
        embeddingService: _embeddingService.Object,
        settingsService: withSettings ? _settings.Object : null);

    [Fact]
    public async Task IsAutoEmbeddingEnabledAsync_ReadsTheSetting_AndDefaultsToTrue()
    {
        _settings.Setup(s => s.GetSettingAsync(SettingKeys.AutoGenerateEmbeddings, true)).ReturnsAsync(false);
        (await CreateService().IsAutoEmbeddingEnabledAsync()).Should().BeFalse();

        _settings.Setup(s => s.GetSettingAsync(SettingKeys.AutoGenerateEmbeddings, true)).ReturnsAsync(true);
        (await CreateService().IsAutoEmbeddingEnabledAsync()).Should().BeTrue();

        (await CreateService(withSettings: false).IsAutoEmbeddingEnabledAsync()).Should().BeTrue(
            "without a settings service the seeded default applies");
    }

    [Fact]
    public async Task CreateEventAsync_AutoEmbeddingOff_DoesNotEmbed()
    {
        // Arrange
        _settings.Setup(s => s.GetSettingAsync(SettingKeys.AutoGenerateEmbeddings, true)).ReturnsAsync(false);
        var service = CreateService();

        // Act
        await service.CreateEventAsync(NewEvent());
        await Task.Delay(250); // give a (wrongly started) background task time to run

        // Assert
        _embeddingService.Verify(e => e.GenerateEmbeddingAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CreateEventAsync_AutoEmbeddingOn_EmbedsInTheBackground()
    {
        // Arrange
        _settings.Setup(s => s.GetSettingAsync(SettingKeys.AutoGenerateEmbeddings, true)).ReturnsAsync(true);
        var service = CreateService();

        // Act
        await service.CreateEventAsync(NewEvent());

        // Assert - fire-and-forget, so poll briefly
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && _embeddingService.Invocations.All(i => i.Method.Name != nameof(IEmbeddingService.GenerateEmbeddingAsync)))
        {
            await Task.Delay(25);
        }

        _embeddingService.Verify(e => e.GenerateEmbeddingAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_ExplicitRequest_IgnoresTheSetting()
    {
        // Arrange - the user turned automatic embedding off, then asked for a backfill
        _settings.Setup(s => s.GetSettingAsync(SettingKeys.AutoGenerateEmbeddings, true)).ReturnsAsync(false);
        var service = CreateService();
        var created = await service.CreateEventAsync(NewEvent());

        // Act
        await service.GenerateEmbeddingAsync(created.EventId);

        // Assert
        _embeddingService.Verify(e => e.GenerateEmbeddingAsync(It.IsAny<string>()), Times.Once);
    }

    private static Event NewEvent() => new()
    {
        Title = "Moved house",
        StartDate = new DateTime(2024, 3, 1),
        Category = EventCategory.Other,
    };
}
