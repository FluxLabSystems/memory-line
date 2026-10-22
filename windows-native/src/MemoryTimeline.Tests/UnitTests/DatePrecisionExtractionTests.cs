using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Tests;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Tests that the extraction pipeline carries date precision end to end:
/// LLM datePrecision strings (mocked <see cref="ILlmService"/>) become
/// <see cref="DatePrecision"/> enum values on pending events, approval copies
/// them to the real event, and pending-event updates round-trip them.
/// Uses a file-based SQLite database because approval runs a real transaction.
/// </summary>
public class DatePrecisionExtractionTests : IDisposable
{
    private readonly TestDbContextFactory _contextFactory;
    private readonly string _databasePath;
    private readonly Mock<ILlmService> _llmServiceMock;
    private readonly EventExtractionService _extractionService;

    public DatePrecisionExtractionTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"DatePrecisionExtractionTests_{Guid.NewGuid()}.db");
        _contextFactory = TestDbContextFactory.CreateSqliteFile(_databasePath);
        using (var context = _contextFactory.CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        _llmServiceMock = new Mock<ILlmService>();

        var eventServiceMock = new Mock<IEventService>();
        eventServiceMock
            .Setup(s => s.GetRecentEventsAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<Event>());

        var personService = new PersonService(
            _contextFactory, new Mock<ILogger<PersonService>>().Object);

        _extractionService = new EventExtractionService(
            _llmServiceMock.Object,
            new Mock<ISpeechToTextService>().Object,
            eventServiceMock.Object,
            new Mock<ISettingsService>().Object,
            new Mock<IRecordingQueueRepository>().Object,
            personService,
            _contextFactory,
            new Mock<ILogger<EventExtractionService>>().Object);
    }

    [Fact]
    public async Task ExtractAndCreatePendingEventsAsync_ParsesDatePrecisionStringsDefensively()
    {
        // Arrange - the queue row the pending events reference (FK enforced on SQLite)
        await using (var seedContext = _contextFactory.CreateDbContext())
        {
            seedContext.RecordingQueues.Add(new RecordingQueue
            {
                QueueId = "queue-1",
                AudioFilePath = @"C:\audio\rec1.wav",
                Status = QueueStatus.Pending
            });
            await seedContext.SaveChangesAsync();
        }

        // Four extracted events covering valid, mixed-case, missing,
        // and garbage precision strings
        _llmServiceMock
            .Setup(s => s.ExtractEventsAsync(It.IsAny<string>(), It.IsAny<ExtractionContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EventExtractionResult
            {
                Success = true,
                OverallConfidence = 0.8,
                Events = new List<ExtractedEvent>
                {
                    new()
                    {
                        Title = "Moved to Denver",
                        StartDate = new DateTime(2011, 7, 1),
                        DatePrecision = "year",
                        Category = "milestone",
                        Confidence = 0.7
                    },
                    new()
                    {
                        Title = "Beach trip",
                        StartDate = new DateTime(1998, 7, 15),
                        DatePrecision = "SEASON",
                        Category = "travel",
                        Confidence = 0.6
                    },
                    new()
                    {
                        Title = "Legacy payload",
                        StartDate = new DateTime(2020, 3, 14),
                        DatePrecision = null,
                        Category = "other",
                        Confidence = 0.9
                    },
                    new()
                    {
                        Title = "Garbage precision",
                        StartDate = new DateTime(2021, 5, 2),
                        DatePrecision = "fortnight",
                        Category = "other",
                        Confidence = 0.5
                    }
                }
            });

        // Act - "I moved to Denver sometime in 2011" must land as Year, not a day
        var created = await _extractionService.ExtractAndCreatePendingEventsAsync(
            "queue-1", "I moved to Denver sometime in 2011...");

        // Assert
        created.Should().HaveCount(4);

        await using var context = _contextFactory.CreateDbContext();
        var stored = await context.PendingEvents.AsNoTracking().ToListAsync();

        stored.Single(p => p.Title == "Moved to Denver").DatePrecision
            .Should().Be(DatePrecision.Year);
        stored.Single(p => p.Title == "Beach trip").DatePrecision
            .Should().Be(DatePrecision.Season, "precision strings parse case-insensitively");
        stored.Single(p => p.Title == "Legacy payload").DatePrecision
            .Should().Be(DatePrecision.Day, "missing precision falls back to Day");
        stored.Single(p => p.Title == "Garbage precision").DatePrecision
            .Should().Be(DatePrecision.Day, "unrecognized precision falls back to Day");

        stored.Should().OnlyContain(p => p.EarliestPossible == null && p.LatestPossible == null);
    }

    [Fact]
    public async Task ApprovePendingEventAsync_CopiesPrecisionAndBoundsToRealEvent()
    {
        // Arrange - a reviewed pending event with coarse precision and explicit bounds
        var pendingId = Guid.NewGuid().ToString();
        await using (var seedContext = _contextFactory.CreateDbContext())
        {
            seedContext.PendingEvents.Add(new PendingEvent
            {
                PendingId = pendingId,
                Title = "Moved to Denver",
                StartDate = new DateTime(2011, 7, 1),
                DatePrecision = DatePrecision.Year,
                EarliestPossible = new DateTime(2011, 1, 1),
                LatestPossible = new DateTime(2011, 12, 31),
                Category = "milestone",
                ConfidenceScore = 0.7,
                ExtractedData = """{"title":"Moved to Denver"}""",
                Status = PendingStatus.PendingReview.ToStringValue(),
                IsApproved = false
            });
            await seedContext.SaveChangesAsync();
        }

        // Act
        var approved = await _extractionService.ApprovePendingEventAsync(pendingId);

        // Assert - the returned and stored event both carry the precision fields
        approved.DatePrecision.Should().Be(DatePrecision.Year);
        approved.EarliestPossible.Should().Be(new DateTime(2011, 1, 1));
        approved.LatestPossible.Should().Be(new DateTime(2011, 12, 31));

        await using var verifyContext = _contextFactory.CreateDbContext();
        var storedEvent = await verifyContext.Events.AsNoTracking().SingleAsync();
        storedEvent.DatePrecision.Should().Be(DatePrecision.Year);
        storedEvent.EarliestPossible.Should().Be(new DateTime(2011, 1, 1));
        storedEvent.LatestPossible.Should().Be(new DateTime(2011, 12, 31));

        var storedPending = await verifyContext.PendingEvents.AsNoTracking().SingleAsync();
        storedPending.IsApproved.Should().BeTrue();
    }

    [Fact]
    public async Task UpdatePendingEventAsync_RoundTripsPrecisionAndBounds()
    {
        // Arrange - a pending event stored with the default Day precision
        var pendingId = Guid.NewGuid().ToString();
        await using (var seedContext = _contextFactory.CreateDbContext())
        {
            seedContext.PendingEvents.Add(new PendingEvent
            {
                PendingId = pendingId,
                Title = "Beach trip",
                StartDate = new DateTime(1998, 7, 15),
                Category = "travel",
                ExtractedData = """{"title":"Beach trip"}""",
                Status = PendingStatus.PendingReview.ToStringValue()
            });
            await seedContext.SaveChangesAsync();
        }

        // Act - the review edit corrects the precision to Season
        var edited = new PendingEvent
        {
            PendingId = pendingId,
            Title = "Beach trip",
            StartDate = new DateTime(1998, 7, 15),
            DatePrecision = DatePrecision.Season,
            EarliestPossible = new DateTime(1998, 6, 1),
            LatestPossible = new DateTime(1998, 8, 31),
            Category = "travel",
            ConfidenceScore = 0.6,
            ExtractedData = """{"title":"Beach trip"}"""
        };
        await _extractionService.UpdatePendingEventAsync(edited);

        // Assert
        await using var verifyContext = _contextFactory.CreateDbContext();
        var stored = await verifyContext.PendingEvents.AsNoTracking().SingleAsync();
        stored.DatePrecision.Should().Be(DatePrecision.Season);
        stored.EarliestPossible.Should().Be(new DateTime(1998, 6, 1));
        stored.LatestPossible.Should().Be(new DateTime(1998, 8, 31));
    }

    public void Dispose()
    {
        using (var context = _contextFactory.CreateDbContext())
        {
            context.Database.EnsureDeleted();
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
