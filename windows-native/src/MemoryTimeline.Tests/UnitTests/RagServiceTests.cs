using FluentAssertions;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Tests;
using Moq;
using System.Text.Json;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

public class RagServiceTests : IDisposable
{
    private readonly TestDbContextFactory _contextFactory;
    private readonly AppDbContext _context; // seeding/lookup context over the same in-memory store
    private readonly Mock<IEmbeddingService> _embeddingServiceMock;
    private readonly Mock<IEventService> _eventServiceMock;
    private readonly Mock<ICrossReferenceRepository> _crossReferenceRepositoryMock;
    private readonly Mock<ILogger<RagService>> _loggerMock;
    private readonly RagService _ragService;

    public RagServiceTests()
    {
        // Factory over a uniquely named in-memory database; RagService creates its own
        // short-lived contexts from it.
        _contextFactory = TestDbContextFactory.CreateInMemory();
        _context = _contextFactory.CreateDbContext();

        _embeddingServiceMock = new Mock<IEmbeddingService>();
        _eventServiceMock = new Mock<IEventService>();
        _crossReferenceRepositoryMock = new Mock<ICrossReferenceRepository>();
        _loggerMock = new Mock<ILogger<RagService>>();

        // RagService no longer takes ILlmService or AppDbContext: cross-reference
        // detection is heuristic (no LLM) and persistence goes through
        // ICrossReferenceRepository over an IDbContextFactory.
        _ragService = new RagService(
            _embeddingServiceMock.Object,
            _eventServiceMock.Object,
            _contextFactory,
            _crossReferenceRepositoryMock.Object,
            _loggerMock.Object);

        // Setup embedding service defaults
        _embeddingServiceMock.Setup(e => e.EmbeddingDimension).Returns(1536);
        _embeddingServiceMock.Setup(e => e.ModelName).Returns("text-embedding-3-small");

        // Cross-reference repository defaults: nothing stored yet
        _crossReferenceRepositoryMock
            .Setup(r => r.GetReferencesForEventAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<CrossReference>());
        _crossReferenceRepositoryMock
            .Setup(r => r.GetReferenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CrossReference?)null);
        _crossReferenceRepositoryMock
            .Setup(r => r.AddAsync(It.IsAny<CrossReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CrossReference cr, CancellationToken _) => cr);

        SeedTestData();
    }

    private void SeedTestData()
    {
        var events = new List<Event>
        {
            new()
            {
                EventId = "event1",
                Title = "Started new job at Tech Company",
                Description = "Joined as a software engineer",
                StartDate = new DateTime(2024, 1, 15),
                Category = EventCategory.Work,
                CreatedAt = DateTime.UtcNow
            },
            new()
            {
                EventId = "event2",
                Title = "Completed coding bootcamp",
                Description = "Finished intensive programming course",
                StartDate = new DateTime(2023, 12, 20),
                Category = EventCategory.Education,
                CreatedAt = DateTime.UtcNow
            },
            new()
            {
                EventId = "event3",
                Title = "Graduated from University",
                Description = "Earned Computer Science degree",
                StartDate = new DateTime(2023, 5, 15),
                Category = EventCategory.Education,
                CreatedAt = DateTime.UtcNow
            }
        };

        // Create embeddings for events (mock vectors) using the mapped column properties
        var embeddings = new List<EventEmbedding>
        {
            CreateEmbeddingRow("event1", 1),
            CreateEmbeddingRow("event2", 2),
            CreateEmbeddingRow("event3", 3)
        };

        _context.Events.AddRange(events);
        _context.EventEmbeddings.AddRange(embeddings);
        _context.SaveChanges();
    }

    private EventEmbedding CreateEmbeddingRow(string eventId, int seed) => new()
    {
        EmbeddingId = Guid.NewGuid().ToString(),
        EventId = eventId,
        EmbeddingVector = JsonSerializer.Serialize(GenerateMockEmbedding(seed)),
        EmbeddingProvider = "openai",
        EmbeddingModel = "text-embedding-3-small",
        EmbeddingDimension = 1536,
        CreatedAt = DateTime.UtcNow
    };

    private float[] GenerateMockEmbedding(int seed)
    {
        // Generate a simple mock embedding vector
        var random = new Random(seed);
        var embedding = new float[1536];
        for (int i = 0; i < embedding.Length; i++)
        {
            embedding[i] = (float)random.NextDouble();
        }
        return embedding;
    }

    [Fact]
    public async Task FindSimilarEventsAsync_ValidEventId_ReturnsSimilarEvents()
    {
        // Arrange
        var sourceEventId = "event1";

        // Mock the event service to return event details
        _eventServiceMock.Setup(s => s.GetEventByIdAsync("event2"))
            .ReturnsAsync(await _context.Events.FindAsync("event2"));
        _eventServiceMock.Setup(s => s.GetEventByIdAsync("event3"))
            .ReturnsAsync(await _context.Events.FindAsync("event3"));

        // Mock similarity calculation to return high similarity for event2
        _embeddingServiceMock.Setup(e => e.CalculateCosineSimilarity(
                It.IsAny<float[]>(), It.IsAny<float[]>()))
            .Returns(0.85);

        _embeddingServiceMock.Setup(e => e.FindKNearestNeighbors(
                It.IsAny<float[]>(),
                It.IsAny<IEnumerable<(string id, float[] embedding)>>(),
                It.IsAny<int>(),
                It.IsAny<double>()))
            .Returns(new List<SimilarityResult>
            {
                new() { Id = "event2", Similarity = 0.85 },
                new() { Id = "event3", Similarity = 0.75 }
            });

        // Act
        var results = await _ragService.FindSimilarEventsAsync(sourceEventId, topK: 10, threshold: 0.7);

        // Assert
        results.Should().NotBeNull();
        results.Should().HaveCount(2);
        results.First().EventId.Should().Be("event2");
        results.First().Similarity.Should().BeGreaterThan(0.7);
    }

    [Fact]
    public async Task FindSimilarEventsAsync_WithHighThreshold_FiltersResults()
    {
        // Arrange
        var sourceEventId = "event1";

        _embeddingServiceMock.Setup(e => e.FindKNearestNeighbors(
                It.IsAny<float[]>(),
                It.IsAny<IEnumerable<(string id, float[] embedding)>>(),
                It.IsAny<int>(),
                It.IsAny<double>()))
            .Returns(new List<SimilarityResult>
            {
                new() { Id = "event2", Similarity = 0.95 }
                // event3 filtered out by threshold
            });

        _eventServiceMock.Setup(s => s.GetEventByIdAsync("event2"))
            .ReturnsAsync(await _context.Events.FindAsync("event2"));

        // Act
        var results = await _ragService.FindSimilarEventsAsync(sourceEventId, topK: 10, threshold: 0.9);

        // Assert
        results.Should().HaveCount(1);
        results.First().Similarity.Should().BeGreaterThanOrEqualTo(0.9);
    }

    [Fact]
    public async Task SuggestTagsAsync_BasedOnSimilarEvents_ReturnsSuggestions()
    {
        // Arrange
        var targetEventId = "event1";

        // Create tags for similar events
        var tag1 = new Tag { TagId = "tag1", Name = "career", CreatedAt = DateTime.UtcNow };
        var tag2 = new Tag { TagId = "tag2", Name = "technology", CreatedAt = DateTime.UtcNow };
        _context.Tags.AddRange(tag1, tag2);

        var event2 = await _context.Events.FindAsync("event2");
        event2!.EventTags.Add(new EventTag { EventId = event2.EventId, TagId = tag1.TagId, Tag = tag1 });
        event2!.EventTags.Add(new EventTag { EventId = event2.EventId, TagId = tag2.TagId, Tag = tag2 });

        var event3 = await _context.Events.FindAsync("event3");
        event3!.EventTags.Add(new EventTag { EventId = event3.EventId, TagId = tag1.TagId, Tag = tag1 });

        await _context.SaveChangesAsync();

        // Mock finding similar events
        _embeddingServiceMock.Setup(e => e.FindKNearestNeighbors(
                It.IsAny<float[]>(),
                It.IsAny<IEnumerable<(string id, float[] embedding)>>(),
                It.IsAny<int>(),
                It.IsAny<double>()))
            .Returns(new List<SimilarityResult>
            {
                new() { Id = "event2", Similarity = 0.9 },
                new() { Id = "event3", Similarity = 0.8 }
            });

        _eventServiceMock.Setup(s => s.GetEventByIdAsync("event2")).ReturnsAsync(event2);
        _eventServiceMock.Setup(s => s.GetEventByIdAsync("event3")).ReturnsAsync(event3);
        _eventServiceMock.Setup(s => s.GetEventTagsAsync("event2")).ReturnsAsync(new List<Tag> { tag1, tag2 });
        _eventServiceMock.Setup(s => s.GetEventTagsAsync("event3")).ReturnsAsync(new List<Tag> { tag1 });

        // Act
        var suggestions = await _ragService.SuggestTagsAsync(targetEventId, maxSuggestions: 5);

        // Assert
        suggestions.Should().NotBeEmpty();
        suggestions.Should().Contain(s => s.TagName == "career");
        suggestions.First().Confidence.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DetectPatternsAsync_WithDateRange_DetectsPatterns()
    {
        // Arrange
        var startDate = new DateTime(2023, 1, 1);
        var endDate = new DateTime(2024, 12, 31);

        // Pattern detection now retrieves events through IEventService
        var seededEvents = _context.Events.ToList();
        _eventServiceMock.Setup(s => s.GetEventsByDateRangeAsync(startDate, endDate))
            .ReturnsAsync(seededEvents);

        // Act
        var result = await _ragService.DetectPatternsAsync(startDate, endDate);

        // Assert
        result.Should().NotBeNull();
        result.CategoryPatterns.Should().NotBeEmpty();
        result.CategoryPatterns.Should().Contain(p => p.Category == EventCategory.Education);
        result.CategoryPatterns.Should().Contain(p => p.Category == EventCategory.Work);
    }

    [Fact]
    public async Task DetectPatternsAsync_NoDateRange_AnalyzesAllEvents()
    {
        // Arrange - without a date range, all events are retrieved through IEventService
        var seededEvents = _context.Events.ToList();
        _eventServiceMock.Setup(s => s.GetAllEventsAsync())
            .ReturnsAsync(seededEvents);

        // Act
        var result = await _ragService.DetectPatternsAsync();

        // Assert
        result.Should().NotBeNull();
        result.CategoryPatterns.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public async Task FindSimilarEventsAsync_NonExistentEventId_ReturnsEmpty()
    {
        // Act
        var results = await _ragService.FindSimilarEventsAsync("non-existent-id");

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectCrossReferencesAsync_WithValidEventId_DetectsAndPersistsReferences()
    {
        // Arrange - detection is now heuristic (temporal proximity / shared category)
        // and new detections are persisted via ICrossReferenceRepository.
        // Add a candidate event within 7 days of event1 so the temporal heuristic fires.
        var eventId = "event1";
        var event4 = new Event
        {
            EventId = "event4",
            Title = "First week at Tech Company",
            Description = "Onboarding and setup",
            StartDate = new DateTime(2024, 1, 18), // 3 days after event1
            Category = EventCategory.Work,
            CreatedAt = DateTime.UtcNow
        };
        _context.Events.Add(event4);
        _context.EventEmbeddings.Add(CreateEmbeddingRow("event4", 4));
        await _context.SaveChangesAsync();

        _embeddingServiceMock.Setup(e => e.FindKNearestNeighbors(
                It.IsAny<float[]>(),
                It.IsAny<IEnumerable<(string id, float[] embedding)>>(),
                It.IsAny<int>(),
                It.IsAny<double>()))
            .Returns(new List<SimilarityResult>
            {
                new() { Id = "event4", Similarity = 0.85 }
            });

        _eventServiceMock.Setup(s => s.GetEventByIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => _context.Events.Find(id));
        _eventServiceMock.Setup(s => s.GetEventWithDetailsAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => _context.Events.Find(id));

        // Act
        var results = await _ragService.DetectCrossReferencesAsync(eventId);

        // Assert - events 3 days apart produce a temporal cross-reference with a
        // similarity-derived (not hard-coded) confidence, persisted via the repository.
        results.Should().ContainSingle();
        results[0].SourceEventId.Should().Be("event1");
        results[0].TargetEventId.Should().Be("event4");
        results[0].RelationshipType.Should().Be(CrossReferenceType.Temporal);
        results[0].Confidence.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(1);

        _crossReferenceRepositoryMock.Verify(r => r.AddAsync(It.Is<CrossReference>(cr =>
            cr.EventId1 == "event1" && cr.EventId2 == "event4"), It.IsAny<CancellationToken>()), Times.Once);

        _embeddingServiceMock.Verify(e => e.FindKNearestNeighbors(
            It.IsAny<float[]>(),
            It.IsAny<IEnumerable<(string, float[])>>(),
            It.IsAny<int>(),
            It.IsAny<double>()), Times.AtLeastOnce);
    }

    [Fact]
    public void EmbeddingServiceMock_CalculateCosineSimilarity_ReturnsValue()
    {
        // Arrange
        var embedding1 = GenerateMockEmbedding(1);
        var embedding2 = GenerateMockEmbedding(2);

        _embeddingServiceMock.Setup(e => e.CalculateCosineSimilarity(embedding1, embedding2))
            .Returns(0.75);

        // Act
        var similarity = _embeddingServiceMock.Object.CalculateCosineSimilarity(embedding1, embedding2);

        // Assert
        similarity.Should().Be(0.75);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }
}
