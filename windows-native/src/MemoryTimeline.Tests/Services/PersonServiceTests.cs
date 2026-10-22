using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Tests;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.Services;

/// <summary>
/// Tests for <see cref="PersonService"/>. A file-based SQLite database is used
/// (rather than EF InMemory) because MergePersonsAsync opens a relational
/// transaction and the tests should exercise the real people/event_people
/// schema, unique name index, and cascade behavior.
/// </summary>
public class PersonServiceTests : IDisposable
{
    private readonly TestDbContextFactory _contextFactory;
    private readonly IPersonService _personService;
    private readonly Mock<ILogger<PersonService>> _loggerMock;
    private readonly string _databasePath;

    public PersonServiceTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"PersonServiceTests_{Guid.NewGuid()}.db");
        _contextFactory = TestDbContextFactory.CreateSqliteFile(_databasePath);

        using (var context = _contextFactory.CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        _loggerMock = new Mock<ILogger<PersonService>>();
        _personService = new PersonService(_contextFactory, _loggerMock.Object);
    }

    // ---- CRUD round trip ----

    [Fact]
    public async Task CreatePersonAsync_ThenGetPersonAsync_RoundTripsAllContactFields()
    {
        // Arrange
        var dto = new PersonDto
        {
            Name = "Alice Smith",
            Nickname = "Ali",
            Relationship = "sister",
            Email = "alice@example.com",
            Phone = "+1 555 0100",
            Birthday = new DateTime(1990, 5, 4),
            Company = "Contoso",
            Notes = "Met at the lake house",
            PhotoPath = @"C:\photos\alice.jpg",
            AvatarColor = "#4A8FB5",
            IsFavorite = true,
            FirstMetDate = new DateTime(2005, 8, 20)
        };

        // Act
        var created = await _personService.CreatePersonAsync(dto);
        var fetched = await _personService.GetPersonAsync(created.PersonId);

        // Assert
        created.PersonId.Should().NotBeNullOrEmpty();
        fetched.Should().NotBeNull();
        fetched!.Name.Should().Be("Alice Smith");
        fetched.Nickname.Should().Be("Ali");
        fetched.Relationship.Should().Be("sister");
        fetched.Email.Should().Be("alice@example.com");
        fetched.Phone.Should().Be("+1 555 0100");
        fetched.Birthday.Should().Be(new DateTime(1990, 5, 4));
        fetched.Company.Should().Be("Contoso");
        fetched.Notes.Should().Be("Met at the lake house");
        fetched.PhotoPath.Should().Be(@"C:\photos\alice.jpg");
        fetched.AvatarColor.Should().Be("#4A8FB5");
        fetched.IsFavorite.Should().BeTrue();
        fetched.FirstMetDate.Should().Be(new DateTime(2005, 8, 20));
        fetched.EventCount.Should().Be(0);
    }

    [Fact]
    public async Task UpdatePersonAsync_ChangesEditableFields_PersistsChanges()
    {
        // Arrange
        var created = await _personService.CreatePersonAsync(new PersonDto
        {
            Name = "Bruce Banner",
            Email = "old@example.com"
        });

        // Act
        created.Name = "Robert Banner";
        created.Email = "new@example.com";
        created.Company = "Gamma Labs";
        created.Birthday = new DateTime(1969, 12, 18);
        var updated = await _personService.UpdatePersonAsync(created);
        var fetched = await _personService.GetPersonAsync(created.PersonId);

        // Assert
        updated.Name.Should().Be("Robert Banner");
        fetched.Should().NotBeNull();
        fetched!.Name.Should().Be("Robert Banner");
        fetched.Email.Should().Be("new@example.com");
        fetched.Company.Should().Be("Gamma Labs");
        fetched.Birthday.Should().Be(new DateTime(1969, 12, 18));
    }

    [Fact]
    public async Task DeletePersonAsync_RemovesPersonAndCascadesJunctionRows()
    {
        // Arrange
        var person = await _personService.CreatePersonAsync(new PersonDto { Name = "Charlie Day" });
        var evt = await SeedEventAsync("Party", new DateTime(2024, 3, 1));
        await SeedLinkAsync(evt.EventId, person.PersonId);

        // Act
        await _personService.DeletePersonAsync(person.PersonId);

        // Assert
        (await _personService.GetPersonAsync(person.PersonId)).Should().BeNull();
        await using var verifyContext = _contextFactory.CreateDbContext();
        (await verifyContext.EventPeople.AsNoTracking().ToListAsync()).Should().BeEmpty();
        (await verifyContext.Events.AsNoTracking().ToListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task CreatePersonAsync_DuplicateNameCaseInsensitive_ThrowsInvalidOperationException()
    {
        // Arrange
        await _personService.CreatePersonAsync(new PersonDto { Name = "Bob Jones" });

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _personService.CreatePersonAsync(new PersonDto { Name = "bob jones" }));
    }

    // ---- Search ----

    [Fact]
    public async Task SearchPersonsAsync_MatchesNameNicknameEmailCompanyAndRelationship()
    {
        // Arrange
        await _personService.CreatePersonAsync(new PersonDto
        {
            Name = "Carol Danvers",
            Nickname = "Cap",
            Email = "carol@starforce.example",
            Company = "Starforce",
            Relationship = "friend"
        });
        await _personService.CreatePersonAsync(new PersonDto { Name = "Wilhelm Zed" });

        // Act & Assert - each searched field matches, case-insensitively
        (await _personService.SearchPersonsAsync("carol")).Should().ContainSingle(p => p.Name == "Carol Danvers");
        (await _personService.SearchPersonsAsync("CAP")).Should().ContainSingle(p => p.Name == "Carol Danvers");
        (await _personService.SearchPersonsAsync("@starforce.example")).Should().ContainSingle(p => p.Name == "Carol Danvers");
        (await _personService.SearchPersonsAsync("starforce")).Should().ContainSingle(p => p.Name == "Carol Danvers");
        (await _personService.SearchPersonsAsync("friend")).Should().ContainSingle(p => p.Name == "Carol Danvers");

        // Non-matching term finds nothing
        (await _personService.SearchPersonsAsync("nomatchterm")).Should().BeEmpty();
    }

    [Fact]
    public async Task SearchPersonsAsync_DoesNotSearchNotes()
    {
        // Arrange
        await _personService.CreatePersonAsync(new PersonDto
        {
            Name = "Pietro Frost",
            Notes = "quicksilver runner"
        });

        // Act
        var results = await _personService.SearchPersonsAsync("quicksilver");

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchPersonsAsync_WhitespaceTerm_ReturnsAllPersons()
    {
        // Arrange
        await _personService.CreatePersonAsync(new PersonDto { Name = "Anna One" });
        await _personService.CreatePersonAsync(new PersonDto { Name = "Ben Two" });

        // Act
        var results = await _personService.SearchPersonsAsync("   ");

        // Assert
        results.Should().HaveCount(2);
    }

    // ---- GetAllPersonsAsync sorting ----

    [Fact]
    public async Task GetAllPersonsAsync_NameSort_FavoritesFirstThenAlphabetical()
    {
        // Arrange
        await SeedSortFixtureAsync();

        // Act
        var favoritesFirst = await _personService.GetAllPersonsAsync(PersonSortOption.Name, favoritesFirst: true);
        var plainAlphabetical = await _personService.GetAllPersonsAsync(PersonSortOption.Name, favoritesFirst: false);

        // Assert - Ben is the only favorite
        favoritesFirst.Select(p => p.Name).Should().Equal("Ben", "Anna", "Cara");
        plainAlphabetical.Select(p => p.Name).Should().Equal("Anna", "Ben", "Cara");
    }

    [Fact]
    public async Task GetAllPersonsAsync_RecentlyAddedSort_OrdersByCreatedAtDescending()
    {
        // Arrange
        await SeedSortFixtureAsync();

        // Act
        var results = await _personService.GetAllPersonsAsync(PersonSortOption.RecentlyAdded, favoritesFirst: false);

        // Assert - CreatedAt: Ben 2024-03, Cara 2024-02, Anna 2024-01
        results.Select(p => p.Name).Should().Equal("Ben", "Cara", "Anna");
    }

    [Fact]
    public async Task GetAllPersonsAsync_RecentlyUpdatedSort_OrdersByUpdatedAtDescending()
    {
        // Arrange
        await SeedSortFixtureAsync();

        // Act
        var results = await _personService.GetAllPersonsAsync(PersonSortOption.RecentlyUpdated, favoritesFirst: false);

        // Assert - UpdatedAt: Anna 2024-06, Cara 2024-05, Ben 2024-04
        results.Select(p => p.Name).Should().Equal("Anna", "Cara", "Ben");
    }

    [Fact]
    public async Task GetAllPersonsAsync_MostEventsSort_OrdersByEventCountAndPopulatesCounts()
    {
        // Arrange
        await SeedSortFixtureAsync();
        var people = await _personService.GetAllPersonsAsync(PersonSortOption.Name, favoritesFirst: false);
        var anna = people.Single(p => p.Name == "Anna");
        var cara = people.Single(p => p.Name == "Cara");

        var e1 = await SeedEventAsync("Event 1", new DateTime(2023, 1, 1));
        var e2 = await SeedEventAsync("Event 2", new DateTime(2023, 2, 1));
        await SeedLinkAsync(e1.EventId, cara.PersonId);
        await SeedLinkAsync(e2.EventId, cara.PersonId);
        await SeedLinkAsync(e1.EventId, anna.PersonId);

        // Act
        var results = await _personService.GetAllPersonsAsync(PersonSortOption.MostEvents, favoritesFirst: false);

        // Assert - Cara 2 events, Anna 1, Ben 0; counts are populated on the DTOs
        results.Select(p => p.Name).Should().Equal("Cara", "Anna", "Ben");
        results.Select(p => p.EventCount).Should().Equal(2, 1, 0);
    }

    // ---- Favorites ----

    [Fact]
    public async Task ToggleFavoriteAsync_FlipsStateAndReturnsNewValue()
    {
        // Arrange
        var person = await _personService.CreatePersonAsync(new PersonDto { Name = "Diana Prince" });
        person.IsFavorite.Should().BeFalse();

        // Act
        var afterFirstToggle = await _personService.ToggleFavoriteAsync(person.PersonId);
        var fetchedOn = await _personService.GetPersonAsync(person.PersonId);
        var afterSecondToggle = await _personService.ToggleFavoriteAsync(person.PersonId);
        var fetchedOff = await _personService.GetPersonAsync(person.PersonId);

        // Assert
        afterFirstToggle.Should().BeTrue();
        fetchedOn!.IsFavorite.Should().BeTrue();
        afterSecondToggle.Should().BeFalse();
        fetchedOff!.IsFavorite.Should().BeFalse();
    }

    // ---- Events for person ----

    [Fact]
    public async Task GetEventsForPersonAsync_ReturnsSummariesNewestStartDateFirst()
    {
        // Arrange
        var person = await _personService.CreatePersonAsync(new PersonDto { Name = "Ed Norton" });
        var oldest = await SeedEventAsync("Oldest", new DateTime(2022, 6, 15), EventCategory.Travel);
        var middle = await SeedEventAsync("Middle", new DateTime(2023, 1, 1), EventCategory.Work,
            endDate: new DateTime(2023, 1, 5));
        var newest = await SeedEventAsync("Newest", new DateTime(2024, 1, 1), EventCategory.Milestone);
        await SeedLinkAsync(oldest.EventId, person.PersonId);
        await SeedLinkAsync(middle.EventId, person.PersonId);
        await SeedLinkAsync(newest.EventId, person.PersonId);

        // Act
        var summaries = await _personService.GetEventsForPersonAsync(person.PersonId);

        // Assert
        summaries.Select(s => s.Title).Should().Equal("Newest", "Middle", "Oldest");
        summaries[0].EventId.Should().Be(newest.EventId);
        summaries[0].StartDate.Should().Be(new DateTime(2024, 1, 1));
        summaries[0].Category.Should().Be(EventCategory.Milestone);
        summaries[1].EndDate.Should().Be(new DateTime(2023, 1, 5));
    }

    [Fact]
    public async Task GetPersonAsync_PopulatesEventCountAndFirstLastEventDates()
    {
        // Arrange
        var person = await _personService.CreatePersonAsync(new PersonDto { Name = "Fiona Glenanne" });
        var first = await SeedEventAsync("First", new DateTime(2020, 2, 2));
        var last = await SeedEventAsync("Last", new DateTime(2024, 9, 9));
        await SeedLinkAsync(first.EventId, person.PersonId);
        await SeedLinkAsync(last.EventId, person.PersonId);

        // Act
        var fetched = await _personService.GetPersonAsync(person.PersonId);

        // Assert
        fetched.Should().NotBeNull();
        fetched!.EventCount.Should().Be(2);
        fetched.FirstEventDate.Should().Be(new DateTime(2020, 2, 2));
        fetched.LastEventDate.Should().Be(new DateTime(2024, 9, 9));
    }

    // ---- Merge ----

    [Fact]
    public async Task MergeAsync_RepointsJunctionsTombstonesSourceAndCreatesAliases()
    {
        // Arrange
        var source = await _personService.CreatePersonAsync(new PersonDto
        {
            Name = "Mike Old",
            Nickname = "Mikey",
            Email = "mike@example.com",
            Company = "Acme",
            Notes = "Source notes",
            IsFavorite = true
        });
        var target = await _personService.CreatePersonAsync(new PersonDto
        {
            Name = "Michael New",
            Phone = "555-0100",
            Company = "TargetCo"
        });

        var e1 = await SeedEventAsync("Only source", new DateTime(2023, 1, 1));
        var e2 = await SeedEventAsync("Shared", new DateTime(2023, 2, 1));
        var e3 = await SeedEventAsync("Only target", new DateTime(2023, 3, 1));
        await SeedLinkAsync(e1.EventId, source.PersonId);
        await SeedLinkAsync(e2.EventId, source.PersonId);
        await SeedLinkAsync(e2.EventId, target.PersonId);
        await SeedLinkAsync(e3.EventId, target.PersonId);

        // Act
        await _personService.MergeAsync(source.PersonId, target.PersonId);

        // Assert - the source row is TOMBSTONED (kept, pointing at the target),
        // not deleted; its id resolves through the chain to the target.
        await using var verifyContext = _contextFactory.CreateDbContext();
        var sourceRow = await verifyContext.People.AsNoTracking()
            .SingleAsync(p => p.PersonId == source.PersonId);
        sourceRow.MergedIntoId.Should().Be(target.PersonId);
        var resolved = await _personService.GetPersonAsync(source.PersonId);
        resolved.Should().NotBeNull();
        resolved!.PersonId.Should().Be(target.PersonId);

        // Tombstones never appear in lists
        var all = await _personService.GetAllPersonsAsync();
        all.Should().ContainSingle().Which.PersonId.Should().Be(target.PersonId);

        // Junctions repointed with ZERO orphans; the shared link was not duplicated
        var links = await verifyContext.EventPeople.AsNoTracking().ToListAsync();
        links.Should().OnlyContain(ep => ep.PersonId == target.PersonId);
        links.Select(ep => ep.EventId).Should().BeEquivalentTo(
            new[] { e1.EventId, e2.EventId, e3.EventId });

        // The source's name AND nickname became aliases of the target
        var aliases = await _personService.GetAliasesAsync(target.PersonId);
        aliases.Should().BeEquivalentTo(new[] { "Mike Old", "Mikey" });

        // Missing contact fields backfilled from source; existing target values kept
        var merged = await _personService.GetPersonAsync(target.PersonId);
        merged.Should().NotBeNull();
        merged!.Email.Should().Be("mike@example.com");   // backfilled
        merged.Notes.Should().Be("Source notes");        // backfilled
        merged.Phone.Should().Be("555-0100");            // target's own value kept
        merged.Company.Should().Be("TargetCo");          // NOT overwritten by source
        merged.IsFavorite.Should().BeTrue();             // favorite flag carried over
        merged.EventCount.Should().Be(3);
    }

    [Fact]
    public async Task MergeAsync_AlreadyTombstonedSource_IsIdempotentNoOp()
    {
        // Arrange - A merged into B; A is now a tombstone
        var a = await _personService.CreatePersonAsync(new PersonDto { Name = "Person A" });
        var b = await _personService.CreatePersonAsync(new PersonDto { Name = "Person B" });
        var c = await _personService.CreatePersonAsync(new PersonDto { Name = "Person C" });
        await _personService.MergeAsync(a.PersonId, b.PersonId);

        // Act - re-merging the tombstoned A (even into a different target) is a no-op
        await _personService.MergeAsync(a.PersonId, c.PersonId);

        // Assert - A still points at B; C gained nothing
        await using var verifyContext = _contextFactory.CreateDbContext();
        var aRow = await verifyContext.People.AsNoTracking()
            .SingleAsync(p => p.PersonId == a.PersonId);
        aRow.MergedIntoId.Should().Be(b.PersonId);
        (await _personService.GetAliasesAsync(c.PersonId)).Should().BeEmpty();
    }

    [Fact]
    public async Task MergeAsync_TombstonedTarget_ResolvesThroughChainToLivingPerson()
    {
        // Arrange - A was merged into B, so A is a tombstone
        var a = await _personService.CreatePersonAsync(new PersonDto { Name = "Person A" });
        var b = await _personService.CreatePersonAsync(new PersonDto { Name = "Person B" });
        var c = await _personService.CreatePersonAsync(new PersonDto { Name = "Person C" });
        var evt = await SeedEventAsync("C's event", new DateTime(2023, 6, 1));
        await SeedLinkAsync(evt.EventId, c.PersonId);
        await _personService.MergeAsync(a.PersonId, b.PersonId);

        // Act - merging C INTO the tombstone A must land on the living B
        await _personService.MergeAsync(c.PersonId, a.PersonId);

        // Assert - C's tombstone points at B (not A) and B holds C's links
        await using var verifyContext = _contextFactory.CreateDbContext();
        var cRow = await verifyContext.People.AsNoTracking()
            .SingleAsync(p => p.PersonId == c.PersonId);
        cRow.MergedIntoId.Should().Be(b.PersonId);
        var links = await verifyContext.EventPeople.AsNoTracking().ToListAsync();
        links.Should().ContainSingle().Which.PersonId.Should().Be(b.PersonId);

        // A double-hop chain resolves end to end: C -> B directly, A -> B
        (await _personService.GetPersonAsync(c.PersonId))!.PersonId.Should().Be(b.PersonId);
        (await _personService.GetPersonAsync(a.PersonId))!.PersonId.Should().Be(b.PersonId);
    }

    [Fact]
    public async Task MergeAsync_SelfMergeDirectlyOrViaChain_Throws()
    {
        // Arrange
        var a = await _personService.CreatePersonAsync(new PersonDto { Name = "Person A" });
        var b = await _personService.CreatePersonAsync(new PersonDto { Name = "Person B" });
        await _personService.MergeAsync(a.PersonId, b.PersonId); // A -> B

        // Act & Assert - direct self-merge
        var direct = async () => await _personService.MergeAsync(b.PersonId, b.PersonId);
        await direct.Should().ThrowAsync<InvalidOperationException>();

        // Act & Assert - the chain cannot produce a cycle: merging living B
        // into tombstone A resolves A -> B == source and must throw, leaving
        // B living.
        var viaChain = async () => await _personService.MergeAsync(b.PersonId, a.PersonId);
        await viaChain.Should().ThrowAsync<InvalidOperationException>();

        await using var verifyContext = _contextFactory.CreateDbContext();
        var bRow = await verifyContext.People.AsNoTracking()
            .SingleAsync(p => p.PersonId == b.PersonId);
        bRow.MergedIntoId.Should().BeNull();
    }

    // ---- Sync projections (design §19 Phase 3) ----

    [Fact]
    public async Task CreatePersonAsync_PublishesPersonUpsert()
    {
        // Arrange
        var upserts = new List<string>();
        var tombstones = new List<(TimelineProjectionEntity Entity, string EntityId)>();
        var service = CreatePersonServiceWith(CreateRecordingPublisher(upserts, tombstones).Object);

        // Act
        var created = await service.CreatePersonAsync(new PersonDto { Name = "Nadia Sync" });

        // Assert - a companion learns about the contact as soon as it exists
        upserts.Should().Equal(created.PersonId);
        tombstones.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdatePersonAsync_PublishesPersonUpsert()
    {
        // Arrange
        var upserts = new List<string>();
        var tombstones = new List<(TimelineProjectionEntity Entity, string EntityId)>();
        var service = CreatePersonServiceWith(CreateRecordingPublisher(upserts, tombstones).Object);
        var created = await service.CreatePersonAsync(new PersonDto { Name = "Owen Before" });
        upserts.Clear(); // the create published too; this test is about the edit

        // Act
        created.Name = "Owen After";
        created.Relationship = "colleague";
        await service.UpdatePersonAsync(created);

        // Assert
        upserts.Should().Equal(created.PersonId);
        tombstones.Should().BeEmpty();
    }

    [Fact]
    public async Task ToggleFavoriteAsync_PublishesPersonUpsert()
    {
        // Arrange
        var upserts = new List<string>();
        var tombstones = new List<(TimelineProjectionEntity Entity, string EntityId)>();
        var service = CreatePersonServiceWith(CreateRecordingPublisher(upserts, tombstones).Object);
        var person = await service.CreatePersonAsync(new PersonDto { Name = "Pia Starred" });
        upserts.Clear();

        // Act
        await service.ToggleFavoriteAsync(person.PersonId);

        // Assert - IsFavorite is carried by the projection, so the flip travels
        upserts.Should().Equal(person.PersonId);
        tombstones.Should().BeEmpty();
    }

    [Fact]
    public async Task DeletePersonAsync_PublishesPersonTombstone()
    {
        // Arrange
        var upserts = new List<string>();
        var tombstones = new List<(TimelineProjectionEntity Entity, string EntityId)>();
        var service = CreatePersonServiceWith(CreateRecordingPublisher(upserts, tombstones).Object);
        var person = await service.CreatePersonAsync(new PersonDto { Name = "Quentin Gone" });
        upserts.Clear();

        // Act
        await service.DeletePersonAsync(person.PersonId);

        // Assert - a real deletion tombstones, so the phone drops its copy
        tombstones.Should().Equal((TimelineProjectionEntity.Person, person.PersonId));
        upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task DeletePersonAsync_AlsoTombstonesTheMergedAwayShellsItRemoves()
    {
        // Arrange - A was merged into B, so A survives as a shell pointing at B
        var upserts = new List<string>();
        var tombstones = new List<(TimelineProjectionEntity Entity, string EntityId)>();
        var service = CreatePersonServiceWith(CreateRecordingPublisher(upserts, tombstones).Object);
        var a = await service.CreatePersonAsync(new PersonDto { Name = "Shell A" });
        var b = await service.CreatePersonAsync(new PersonDto { Name = "Keeper B" });
        await service.MergeAsync(a.PersonId, b.PersonId);
        upserts.Clear();

        // Act - deleting B also removes the now-unresolvable shell A
        await service.DeletePersonAsync(b.PersonId);

        // Assert - BOTH removed rows are tombstoned. A companion was told about
        // the shell when it was merged; leaving it un-tombstoned would strand
        // that companion on a pointer to a person that no longer exists.
        tombstones.Should().BeEquivalentTo(new[]
        {
            (TimelineProjectionEntity.Person, b.PersonId),
            (TimelineProjectionEntity.Person, a.PersonId)
        });
    }

    [Fact]
    public async Task MergeAsync_PublishesUpsertsForBothPeopleAndNeverTombstonesTheMergedAwaySource()
    {
        // Arrange
        var upserts = new List<string>();
        var tombstones = new List<(TimelineProjectionEntity Entity, string EntityId)>();
        var service = CreatePersonServiceWith(CreateRecordingPublisher(upserts, tombstones).Object);
        var source = await service.CreatePersonAsync(new PersonDto { Name = "Mike Old" });
        var target = await service.CreatePersonAsync(new PersonDto { Name = "Michael New" });
        var evt = await SeedEventAsync("Their event", new DateTime(2023, 4, 1));
        await SeedLinkAsync(evt.EventId, source.PersonId);
        upserts.Clear();

        // Act
        await service.MergeAsync(source.PersonId, target.PersonId);

        // Assert - the merged-away source is published as an UPSERT, because its
        // row survives carrying MergedIntoId and the already-projected event
        // still lists the old person id. A tombstone here would leave that event
        // rendering a dangling person on the phone instead of resolving through
        // the pointer to the survivor.
        tombstones.Should().BeEmpty(
            "a merged-away person is a pointer to follow, not a row to drop");

        // Both people changed: the source gained the pointer, the target gained
        // the source's event link (and therefore a different EventCount).
        upserts.Should().BeEquivalentTo(new[] { source.PersonId, target.PersonId });

        // ...and the archive agrees the source is a tombstone, not a deletion
        await using var verifyContext = _contextFactory.CreateDbContext();
        var sourceRow = await verifyContext.People.AsNoTracking()
            .SingleAsync(p => p.PersonId == source.PersonId);
        sourceRow.MergedIntoId.Should().Be(target.PersonId);
    }

    [Fact]
    public async Task MergeAsync_AlreadyTombstonedSource_PublishesNothing()
    {
        // Arrange - A -> B already happened
        var upserts = new List<string>();
        var tombstones = new List<(TimelineProjectionEntity Entity, string EntityId)>();
        var service = CreatePersonServiceWith(CreateRecordingPublisher(upserts, tombstones).Object);
        var a = await service.CreatePersonAsync(new PersonDto { Name = "Person A" });
        var b = await service.CreatePersonAsync(new PersonDto { Name = "Person B" });
        var c = await service.CreatePersonAsync(new PersonDto { Name = "Person C" });
        await service.MergeAsync(a.PersonId, b.PersonId);
        upserts.Clear();

        // Act - the re-merge is a no-op that writes nothing
        await service.MergeAsync(a.PersonId, c.PersonId);

        // Assert - nothing was written, so nothing is projected
        upserts.Should().BeEmpty();
        tombstones.Should().BeEmpty();
    }

    [Fact]
    public async Task CreatePersonAsync_ProjectionPublisherThrows_StillCreatesThePerson()
    {
        // Arrange - the sync outbox is unavailable
        var publisherMock = new Mock<ITimelineProjectionPublisher>();
        publisherMock
            .Setup(p => p.PublishPersonAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sync outbox unavailable"));
        var service = CreatePersonServiceWith(publisherMock.Object);

        // Act
        var created = await service.CreatePersonAsync(new PersonDto { Name = "Rita Committed" });

        // Assert - the archive write already committed; a failure to tell the
        // phone about it must not be reported to the user as a failed save
        created.PersonId.Should().NotBeNullOrEmpty();
        (await service.GetPersonAsync(created.PersonId)).Should().NotBeNull();
    }

    [Fact]
    public async Task MergeAsync_ProjectionPublisherThrows_StillCommitsTheMerge()
    {
        // Arrange
        var publisherMock = new Mock<ITimelineProjectionPublisher>();
        publisherMock
            .Setup(p => p.PublishPersonAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sync outbox unavailable"));
        var service = CreatePersonServiceWith(publisherMock.Object);
        var source = await service.CreatePersonAsync(new PersonDto { Name = "Source Row" });
        var target = await service.CreatePersonAsync(new PersonDto { Name = "Target Row" });

        // Act
        await service.MergeAsync(source.PersonId, target.PersonId);

        // Assert - publishing happens after the transaction commits, so a
        // throwing publisher can neither fail nor roll back the merge
        await using var verifyContext = _contextFactory.CreateDbContext();
        var sourceRow = await verifyContext.People.AsNoTracking()
            .SingleAsync(p => p.PersonId == source.PersonId);
        sourceRow.MergedIntoId.Should().Be(target.PersonId);
    }

    [Fact]
    public async Task WithoutProjectionPublisher_EveryMutationStillSucceeds()
    {
        // Arrange - _personService uses the two-argument constructor, i.e. the
        // default null publisher any non-sync host (and every older test) gets.
        var keeper = await _personService.CreatePersonAsync(new PersonDto { Name = "Unsynced Sam" });
        var merged = await _personService.CreatePersonAsync(new PersonDto { Name = "Unsynced Sue" });

        // Act
        await _personService.ToggleFavoriteAsync(keeper.PersonId);
        await _personService.UpdatePersonAsync(keeper);
        await _personService.AddAliasAsync(keeper.PersonId, "Sammy");
        await _personService.MergeAsync(merged.PersonId, keeper.PersonId);
        await _personService.DeletePersonAsync(keeper.PersonId);

        // Assert - no publisher, no NullReferenceException, no behavior change
        (await _personService.GetPersonAsync(keeper.PersonId)).Should().BeNull();
    }

    // ---- Aliases ----

    [Fact]
    public async Task AddAliasAsync_RoundTripsAndIsIdempotentPerPerson()
    {
        // Arrange
        var person = await _personService.CreatePersonAsync(new PersonDto { Name = "Robert Paulson" });

        // Act
        await _personService.AddAliasAsync(person.PersonId, "Bob");
        await _personService.AddAliasAsync(person.PersonId, "bob"); // ci re-add: no-op

        // Assert
        (await _personService.GetAliasesAsync(person.PersonId)).Should().Equal("Bob");

        // Act - remove (case-insensitive) and verify
        await _personService.RemoveAliasAsync(person.PersonId, "BOB");
        (await _personService.GetAliasesAsync(person.PersonId)).Should().BeEmpty();
    }

    [Fact]
    public async Task AddAliasAsync_AliasEqualsLivingPersonName_ThrowsWithClearMessage()
    {
        // Arrange
        var robert = await _personService.CreatePersonAsync(new PersonDto { Name = "Robert Paulson" });
        await _personService.CreatePersonAsync(new PersonDto { Name = "Bob" });

        // Act
        var addAlias = async () => await _personService.AddAliasAsync(robert.PersonId, "bob");

        // Assert - rejected because a living person is already named Bob
        (await addAlias.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Bob");
    }

    [Fact]
    public async Task AddAliasAsync_AliasOwnedByAnotherPerson_Throws()
    {
        // Arrange
        var robert = await _personService.CreatePersonAsync(new PersonDto { Name = "Robert Paulson" });
        var bobby = await _personService.CreatePersonAsync(new PersonDto { Name = "Roberta Paulson" });
        await _personService.AddAliasAsync(robert.PersonId, "Bob");

        // Act
        var stealAlias = async () => await _personService.AddAliasAsync(bobby.PersonId, "BOB");

        // Assert
        await stealAlias.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- Profile ----

    [Fact]
    public async Task GetProfileAsync_AggregatesEventsDatesCoOccurrenceLocationsAndAliases()
    {
        // Arrange - P shares events with Q (twice) and R (once), across two locations
        var p = await _personService.CreatePersonAsync(new PersonDto { Name = "Paula Main" });
        var q = await _personService.CreatePersonAsync(new PersonDto { Name = "Quinn Often" });
        var r = await _personService.CreatePersonAsync(new PersonDto { Name = "Rare Guest" });
        await _personService.AddAliasAsync(p.PersonId, "Pat");

        var e1 = await SeedEventAsync("Earliest", new DateTime(2020, 1, 10));
        var e2 = await SeedEventAsync("Middle", new DateTime(2022, 6, 15));
        var e3 = await SeedEventAsync("Latest", new DateTime(2024, 3, 20));
        await SeedLinkAsync(e1.EventId, p.PersonId);
        await SeedLinkAsync(e1.EventId, q.PersonId);
        await SeedLinkAsync(e2.EventId, p.PersonId);
        await SeedLinkAsync(e2.EventId, q.PersonId);
        await SeedLinkAsync(e2.EventId, r.PersonId);
        await SeedLinkAsync(e3.EventId, p.PersonId);

        var lakeHouse = await SeedLocationAsync("Lake House");
        var office = await SeedLocationAsync("Office");
        await SeedEventLocationAsync(e1.EventId, lakeHouse.LocationId);
        await SeedEventLocationAsync(e2.EventId, lakeHouse.LocationId);
        await SeedEventLocationAsync(e3.EventId, office.LocationId);

        // Act
        var profile = await _personService.GetProfileAsync(p.PersonId);

        // Assert
        profile.Should().NotBeNull();
        profile!.Person.PersonId.Should().Be(p.PersonId);
        profile.Person.EventCount.Should().Be(3);

        profile.Events.Select(e => e.Title).Should().Equal("Latest", "Middle", "Earliest");
        profile.FirstSeen.Should().Be(new DateTime(2020, 1, 10));
        profile.LastSeen.Should().Be(new DateTime(2024, 3, 20));

        profile.TopCoOccurring.Should().HaveCount(2);
        profile.TopCoOccurring[0].Should().Be(new PersonCoOccurrence(q.PersonId, "Quinn Often", 2));
        profile.TopCoOccurring[1].Should().Be(new PersonCoOccurrence(r.PersonId, "Rare Guest", 1));
        profile.TopCoOccurring[0].SharedEventsDisplay.Should().Be("2 shared memories");
        profile.TopCoOccurring[1].SharedEventsDisplay.Should().Be("1 shared memory");

        profile.TopLocations.Should().Equal(new PersonLocationCount("Lake House", 2), new PersonLocationCount("Office", 1));
        profile.TopLocations[1].CountDisplay.Should().Be("1 memory");
        profile.Aliases.Should().Equal("Pat");
    }

    [Fact]
    public async Task GetProfileAsync_TopCoOccurring_CapsAtFiveLivingPeopleOrderedByCountThenName()
    {
        // Arrange - a hub person on a party with six guests (one event each),
        // one guest who also shares a second event, and a tombstoned person
        // linked directly (a stray row a merge would normally repoint).
        var hub = await _personService.CreatePersonAsync(new PersonDto { Name = "Hub Person" });
        var party = await SeedEventAsync("Big party", new DateTime(2023, 7, 1));
        await SeedLinkAsync(party.EventId, hub.PersonId);
        foreach (var name in new[] { "Fay", "Eve", "Dan", "Cal", "Bea", "Ann" })
        {
            var guest = await _personService.CreatePersonAsync(new PersonDto { Name = name });
            await SeedLinkAsync(party.EventId, guest.PersonId);
        }

        var twice = await _personService.CreatePersonAsync(new PersonDto { Name = "Zed Twice" });
        await SeedLinkAsync(party.EventId, twice.PersonId);
        var dinner = await SeedEventAsync("Dinner", new DateTime(2023, 8, 1));
        await SeedLinkAsync(dinner.EventId, hub.PersonId);
        await SeedLinkAsync(dinner.EventId, twice.PersonId);

        var ghost = await _personService.CreatePersonAsync(new PersonDto { Name = "Ghost Row" });
        var keeper = await _personService.CreatePersonAsync(new PersonDto { Name = "Keeper" });
        await _personService.MergeAsync(ghost.PersonId, keeper.PersonId);
        await SeedLinkAsync(party.EventId, ghost.PersonId);

        // Act
        var profile = await _personService.GetProfileAsync(hub.PersonId);

        // Assert - most shared first, then by name; five at most; living only.
        profile!.TopCoOccurring.Select(c => c.Name).Should().Equal("Zed Twice", "Ann", "Bea", "Cal", "Dan");
        profile.TopCoOccurring[0].SharedEvents.Should().Be(2);
        profile.TopCoOccurring.Should().OnlyContain(c => c.SharedEvents >= 1);
        profile.TopCoOccurring.Select(c => c.Name).Should().NotContain("Ghost Row").And.NotContain("Keeper");
    }

    [Fact]
    public async Task GetProfileAsync_TombstonedId_ResolvesToSurvivorsProfile()
    {
        // Arrange
        var source = await _personService.CreatePersonAsync(new PersonDto { Name = "Old Row" });
        var target = await _personService.CreatePersonAsync(new PersonDto { Name = "Survivor" });
        var evt = await SeedEventAsync("Their event", new DateTime(2021, 5, 5));
        await SeedLinkAsync(evt.EventId, source.PersonId);
        await _personService.MergeAsync(source.PersonId, target.PersonId);

        // Act - asking for the tombstoned id yields the survivor's profile
        var profile = await _personService.GetProfileAsync(source.PersonId);

        // Assert
        profile.Should().NotBeNull();
        profile!.Person.PersonId.Should().Be(target.PersonId);
        profile.Events.Should().ContainSingle().Which.EventId.Should().Be(evt.EventId);
        profile.Aliases.Should().Contain("Old Row");
    }

    // ---- Matching ----

    [Fact]
    public async Task FindBestMatchAsync_ExactNameCaseInsensitive_ReturnsExactMatch()
    {
        // Arrange
        var person = await _personService.CreatePersonAsync(new PersonDto { Name = "Henry Adams" });

        // Act
        var match = await _personService.FindBestMatchAsync("henry adams");

        // Assert
        match.Should().NotBeNull();
        match!.Kind.Should().Be(PersonMatchKind.Exact);
        match.Person.PersonId.Should().Be(person.PersonId);
    }

    [Fact]
    public async Task FindBestMatchAsync_ExactNickname_ReturnsNicknameMatch()
    {
        // Arrange
        var person = await _personService.CreatePersonAsync(new PersonDto
        {
            Name = "Katherine Bell",
            Nickname = "Kate"
        });

        // Act
        var match = await _personService.FindBestMatchAsync("kate");

        // Assert
        match.Should().NotBeNull();
        match!.Kind.Should().Be(PersonMatchKind.Nickname);
        match.Person.PersonId.Should().Be(person.PersonId);
    }

    [Fact]
    public async Task FindBestMatchAsync_AliasExact_BeatsFuzzyAndReportsAliasKind()
    {
        // Arrange - "Bob" is an alias of Robert; "Bobb Smith" would otherwise
        // win as a fuzzy (containment) match.
        var robert = await _personService.CreatePersonAsync(new PersonDto { Name = "Robert Paulson" });
        await _personService.CreatePersonAsync(new PersonDto { Name = "Bobb Smith" });
        await _personService.AddAliasAsync(robert.PersonId, "Bob");

        // Act
        var match = await _personService.FindBestMatchAsync("bob");

        // Assert - the alias tier sits above fuzzy
        match.Should().NotBeNull();
        match!.Kind.Should().Be(PersonMatchKind.Alias);
        match.Person.PersonId.Should().Be(robert.PersonId);
    }

    [Fact]
    public async Task FindBestMatchAsync_NicknameBeatsAlias()
    {
        // Arrange - one person is NICKNAMED Bob, another merely has the alias
        var nicknamed = await _personService.CreatePersonAsync(new PersonDto
        {
            Name = "William Tell",
            Nickname = "Bob"
        });
        var aliased = await _personService.CreatePersonAsync(new PersonDto { Name = "Robert Paulson" });
        await _personService.AddAliasAsync(aliased.PersonId, "Bob");

        // Act
        var match = await _personService.FindBestMatchAsync("Bob");

        // Assert - the nickname tier sits above the alias tier
        match.Should().NotBeNull();
        match!.Kind.Should().Be(PersonMatchKind.Nickname);
        match.Person.PersonId.Should().Be(nicknamed.PersonId);
    }

    [Fact]
    public async Task FindBestMatchAsync_SmallEditDistance_ReturnsFuzzyMatch()
    {
        // Arrange
        var person = await _personService.CreatePersonAsync(new PersonDto { Name = "Henry Adams" });

        // Act - one substitution away from "Henry Adams"
        var match = await _personService.FindBestMatchAsync("Henri Adams");

        // Assert
        match.Should().NotBeNull();
        match!.Kind.Should().Be(PersonMatchKind.Fuzzy);
        match.Person.PersonId.Should().Be(person.PersonId);
    }

    [Fact]
    public async Task FindBestMatchAsync_NothingPlausible_ReturnsNull()
    {
        // Arrange
        await _personService.CreatePersonAsync(new PersonDto { Name = "Henry Adams" });

        // Act
        var noMatch = await _personService.FindBestMatchAsync("Xqzwv Plmnr");
        var emptyName = await _personService.FindBestMatchAsync("   ");

        // Assert
        noMatch.Should().BeNull();
        emptyName.Should().BeNull();
    }

    // ---- Duplicates ----

    [Fact]
    public async Task FindPotentialDuplicatesAsync_FindsNearIdenticalNames()
    {
        // Arrange
        await _personService.CreatePersonAsync(new PersonDto { Name = "John Smith" });
        await _personService.CreatePersonAsync(new PersonDto { Name = "Jon Smith" });
        await _personService.CreatePersonAsync(new PersonDto { Name = "Completely Unrelated" });

        // Act
        var pairs = await _personService.FindPotentialDuplicatesAsync();

        // Assert
        pairs.Should().ContainSingle();
        var pair = pairs[0];
        new[] { pair.First.Name, pair.Second.Name }
            .Should().BeEquivalentTo(new[] { "John Smith", "Jon Smith" });
        pair.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SuggestMergesAsync_AliasCollision_ReturnsReasonedCandidate()
    {
        // Arrange - Robert carries the alias "Bob"; later a separate living
        // person named "Bob" is created (names alone are not near-duplicates,
        // so only the alias collision can surface this pair).
        var robert = await _personService.CreatePersonAsync(new PersonDto { Name = "Robert Paulson" });
        await _personService.AddAliasAsync(robert.PersonId, "Bob");
        var bob = await _personService.CreatePersonAsync(new PersonDto { Name = "Bob" });

        // Act
        var candidates = await _personService.SuggestMergesAsync();

        // Assert - the alias owner is the suggested keeper (First), the
        // namesake the suggested source (Second), with an explanatory reason
        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.First.PersonId.Should().Be(robert.PersonId);
        candidate.Second.PersonId.Should().Be(bob.PersonId);
        candidate.Reason.Should().Contain("Bob").And.Contain("alias");
    }

    // ---- Seed helpers ----

    /// <summary>
    /// Builds a service wired to a projection publisher. Mirrors the default
    /// fixture in every other respect, so a projection test differs from the
    /// plain one by exactly the dependency under test.
    /// </summary>
    private PersonService CreatePersonServiceWith(ITimelineProjectionPublisher projectionPublisher) =>
        new(_contextFactory, _loggerMock.Object, projectionPublisher);

    /// <summary>
    /// A publisher mock that records WHICH people were projected and whether a
    /// tombstone was ever published. The payload itself is the Sync layer's
    /// business (and is covered there); what PersonService owes the feed is the
    /// right call, for the right id, at the right moment.
    /// </summary>
    private static Mock<ITimelineProjectionPublisher> CreateRecordingPublisher(
        List<string> personUpserts,
        List<(TimelineProjectionEntity Entity, string EntityId)> tombstones)
    {
        var mock = new Mock<ITimelineProjectionPublisher>();

        mock.Setup(p => p.PublishPersonAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((personId, _) => personUpserts.Add(personId))
            .Returns(Task.CompletedTask);

        mock.Setup(p => p.PublishDeletedAsync(
                It.IsAny<TimelineProjectionEntity>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<TimelineProjectionEntity, string, CancellationToken>(
                (entity, entityId, _) => tombstones.Add((entity, entityId)))
            .Returns(Task.CompletedTask);

        return mock;
    }

    /// <summary>
    /// Seeds Anna/Ben/Cara directly through a context with explicit created/updated
    /// timestamps so sort assertions are deterministic (no wall-clock dependence).
    /// </summary>
    private async Task SeedSortFixtureAsync()
    {
        await using var context = _contextFactory.CreateDbContext();
        context.People.AddRange(
            new Person
            {
                Name = "Anna",
                CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new Person
            {
                Name = "Ben",
                IsFavorite = true,
                CreatedAt = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            new Person
            {
                Name = "Cara",
                CreatedAt = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        await context.SaveChangesAsync();
    }

    private async Task<Event> SeedEventAsync(
        string title,
        DateTime startDate,
        string? category = null,
        DateTime? endDate = null)
    {
        var evt = new Event
        {
            EventId = Guid.NewGuid().ToString(),
            Title = title,
            StartDate = startDate,
            EndDate = endDate,
            Category = category ?? EventCategory.Other,
            CreatedAt = DateTime.UtcNow
        };

        await using var context = _contextFactory.CreateDbContext();
        context.Events.Add(evt);
        await context.SaveChangesAsync();
        return evt;
    }

    private async Task SeedLinkAsync(string eventId, string personId)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.EventPeople.Add(new EventPerson
        {
            EventId = eventId,
            PersonId = personId,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private async Task<Location> SeedLocationAsync(string name)
    {
        var location = new Location
        {
            LocationId = Guid.NewGuid().ToString(),
            Name = name,
            CreatedAt = DateTime.UtcNow
        };

        await using var context = _contextFactory.CreateDbContext();
        context.Locations.Add(location);
        await context.SaveChangesAsync();
        return location;
    }

    private async Task SeedEventLocationAsync(string eventId, string locationId)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.EventLocations.Add(new EventLocation
        {
            EventId = eventId,
            LocationId = locationId,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
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
