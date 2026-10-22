using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// The relationship heuristics behind <see cref="RagService.DetectCrossReferencesAsync"/>:
/// IRagService defines six relationship types, and the heuristic layer can
/// honestly produce four — Temporal, Participatory (people in common),
/// Locational (places in common) and Thematic (category in common), in that
/// precedence. Driven through the explicit-candidate path so no embeddings
/// are needed; events are built in memory with the navigations
/// GetEventWithDetailsAsync loads.
/// </summary>
public class RagRelationshipHeuristicsTests
{
    private readonly Mock<IEventService> _eventService = new();
    private readonly Mock<ICrossReferenceRepository> _crossReferences = new();
    private readonly RagService _ragService;
    private readonly Dictionary<string, Event> _events = new();

    public RagRelationshipHeuristicsTests()
    {
        _crossReferences.Setup(r => r.GetReferencesForEventAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<CrossReference>());
        _crossReferences.Setup(r => r.GetReferenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CrossReference?)null);
        _crossReferences.Setup(r => r.AddAsync(It.IsAny<CrossReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CrossReference cr, CancellationToken _) => cr);
        _eventService.Setup(s => s.GetEventWithDetailsAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => _events.TryGetValue(id, out var evt) ? evt : null);

        _ragService = new RagService(
            new Mock<IEmbeddingService>().Object,
            _eventService.Object,
            TestDbContextFactory.CreateInMemory(),
            _crossReferences.Object,
            NullLogger<RagService>.Instance);
    }

    [Fact]
    public async Task SharedPerson_FarApartInTime_IsParticipatory()
    {
        // Arrange - two events a year apart, different categories, Dana at both
        Seed("source", new DateTime(2023, 3, 1), EventCategory.Travel, people: new[] { ("p-dana", "Dana") });
        Seed("target", new DateTime(2024, 3, 1), EventCategory.Work, people: new[] { ("p-dana", "Dana"), ("p-ruth", "Ruth") });

        // Act
        var results = await _ragService.DetectCrossReferencesAsync("source", new[] { "target" });

        // Assert
        var result = results.Should().ContainSingle().Subject;
        result.RelationshipType.Should().Be(CrossReferenceType.Participatory);
        result.SourceEventId.Should().Be("source");
        result.TargetEventId.Should().Be("target");
        result.Confidence.Should().BeInRange(0.5, 1.0);
        result.Reasoning.Should().StartWith("Heuristic:").And.Contain("Dana").And.NotContain("Ruth");

        // Persisted under the stored "person" type.
        _crossReferences.Verify(r => r.AddAsync(It.Is<CrossReference>(cr =>
            cr.EventId1 == "source" && cr.EventId2 == "target"
            && cr.RelationshipType == RelationshipType.Person.ToStringValue()), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SharedLocation_NoSharedPeople_IsLocational()
    {
        // Arrange
        Seed("source", new DateTime(2023, 3, 1), EventCategory.Travel, locations: new[] { ("l-paris", "Paris") });
        Seed("target", new DateTime(2020, 8, 1), EventCategory.Relationship, locations: new[] { ("l-paris", "Paris"), ("l-lyon", "Lyon") });

        // Act
        var results = await _ragService.DetectCrossReferencesAsync("source", new[] { "target" });

        // Assert
        var result = results.Should().ContainSingle().Subject;
        result.RelationshipType.Should().Be(CrossReferenceType.Locational);
        result.Confidence.Should().BeInRange(0.4, 1.0);
        result.Reasoning.Should().Contain("Paris").And.NotContain("Lyon");

        _crossReferences.Verify(r => r.AddAsync(It.Is<CrossReference>(cr =>
            cr.RelationshipType == RelationshipType.Location.ToStringValue()), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SharedPersonAndPlace_PeopleTakePrecedenceOverPlaces()
    {
        Seed("source", new DateTime(2023, 3, 1), EventCategory.Travel,
            people: new[] { ("p-dana", "Dana") }, locations: new[] { ("l-paris", "Paris") });
        Seed("target", new DateTime(2024, 3, 1), EventCategory.Travel,
            people: new[] { ("p-dana", "Dana") }, locations: new[] { ("l-paris", "Paris") });

        var results = await _ragService.DetectCrossReferencesAsync("source", new[] { "target" });

        results.Should().ContainSingle().Which.RelationshipType.Should().Be(CrossReferenceType.Participatory);
    }

    [Fact]
    public async Task WithinAWeek_TemporalStillWinsOverSharedPeople()
    {
        // The existing rule keeps its precedence: close in time is the
        // strongest signal the heuristics have.
        Seed("source", new DateTime(2024, 3, 1), EventCategory.Work, people: new[] { ("p-dana", "Dana") });
        Seed("target", new DateTime(2024, 3, 4), EventCategory.Travel, people: new[] { ("p-dana", "Dana") });

        var results = await _ragService.DetectCrossReferencesAsync("source", new[] { "target" });

        results.Should().ContainSingle().Which.RelationshipType.Should().Be(CrossReferenceType.Temporal);
    }

    [Fact]
    public async Task OnlySharedCategory_StaysThematic()
    {
        Seed("source", new DateTime(2023, 3, 1), EventCategory.Work, people: new[] { ("p-dana", "Dana") });
        Seed("target", new DateTime(2024, 3, 1), EventCategory.Work, people: new[] { ("p-ruth", "Ruth") });

        var results = await _ragService.DetectCrossReferencesAsync("source", new[] { "target" });

        results.Should().ContainSingle().Which.RelationshipType.Should().Be(CrossReferenceType.Thematic);
    }

    [Fact]
    public async Task NothingInCommon_ProducesNoRelationship()
    {
        Seed("source", new DateTime(2023, 3, 1), EventCategory.Work,
            people: new[] { ("p-dana", "Dana") }, locations: new[] { ("l-paris", "Paris") });
        Seed("target", new DateTime(2024, 3, 1), EventCategory.Travel,
            people: new[] { ("p-ruth", "Ruth") }, locations: new[] { ("l-lyon", "Lyon") });

        var results = await _ragService.DetectCrossReferencesAsync("source", new[] { "target" });

        results.Should().BeEmpty();
        _crossReferences.Verify(r => r.AddAsync(It.IsAny<CrossReference>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SharedPeople_MoreOverlap_MeansMoreConfidence()
    {
        // Arrange - one target shares one of two people, another shares both
        Seed("source", new DateTime(2023, 3, 1), EventCategory.Work,
            people: new[] { ("p-dana", "Dana"), ("p-ruth", "Ruth") });
        Seed("one-shared", new DateTime(2021, 3, 1), EventCategory.Travel,
            people: new[] { ("p-dana", "Dana"), ("p-sam", "Sam") });
        Seed("both-shared", new DateTime(2020, 3, 1), EventCategory.Travel,
            people: new[] { ("p-dana", "Dana"), ("p-ruth", "Ruth") });

        // Act
        var results = await _ragService.DetectCrossReferencesAsync("source", new[] { "one-shared", "both-shared" });

        // Assert
        results.Should().HaveCount(2);
        var one = results.Single(r => r.TargetEventId == "one-shared");
        var both = results.Single(r => r.TargetEventId == "both-shared");
        both.Confidence.Should().BeGreaterThan(one.Confidence);
        both.Reasoning.Should().Contain("Dana and Ruth");
    }

    private void Seed(
        string id,
        DateTime startDate,
        string category,
        (string Id, string Name)[]? people = null,
        (string Id, string Name)[]? locations = null)
    {
        var evt = new Event
        {
            EventId = id,
            Title = $"Event {id}",
            StartDate = startDate,
            Category = category,
        };

        foreach (var (personId, name) in people ?? Array.Empty<(string, string)>())
        {
            evt.EventPeople.Add(new EventPerson
            {
                EventId = id,
                PersonId = personId,
                Person = new Person { PersonId = personId, Name = name },
            });
        }

        foreach (var (locationId, name) in locations ?? Array.Empty<(string, string)>())
        {
            evt.EventLocations.Add(new EventLocation
            {
                EventId = id,
                LocationId = locationId,
                Location = new Location { LocationId = locationId, Name = name },
            });
        }

        _events[id] = evt;
    }
}
