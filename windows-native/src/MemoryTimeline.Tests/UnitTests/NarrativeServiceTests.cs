using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Tests;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Tests for NarrativeService (F5 narrative generation).
///
/// Uses the EF InMemory provider (scope resolution is plain LINQ, no SQL
/// semantics needed) with a mocked ILlmService. Section calls and the
/// connective pass are told apart by the word "bridge", which appears only
/// in the connective system prompt.
/// </summary>
public class NarrativeServiceTests : IDisposable
{
    private readonly TestDbContextFactory _contextFactory;
    private readonly Mock<ILlmService> _llmMock;
    private readonly NarrativeService _service;

    private readonly List<(string System, string User)> _sectionPrompts = new();
    private readonly List<(string System, string User)> _connectivePrompts = new();

    public NarrativeServiceTests()
    {
        _contextFactory = TestDbContextFactory.CreateInMemory();
        _llmMock = new Mock<ILlmService>();
        _service = new NarrativeService(
            _llmMock.Object,
            _contextFactory,
            new Mock<ILogger<NarrativeService>>().Object);
    }

    #region Scope resolution

    [Fact]
    public async Task GenerateAsync_EraScope_GathersEraIdMatchesAndDateRangeOverlaps()
    {
        // Arrange: era window is 2019; one event linked by id (outside the
        // window), one inside the window, one entirely unrelated.
        await SeedEraAsync("era-1", "College Years", new DateTime(2019, 1, 1), new DateTime(2019, 12, 31));
        await SeedEventAsync("evt-linked", "Linked by era id", new DateTime(2025, 5, 1), eraId: "era-1");
        await SeedEventAsync("evt-inrange", "Inside era window", new DateTime(2019, 6, 15));
        await SeedEventAsync("evt-outside", "Unrelated", new DateTime(2022, 3, 1));
        SetupSectionProse();

        // Act
        var narrative = await _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.Era,
            ScopeId = "era-1"
        });

        // Assert: era-id match plus date-range overlap, chronological order.
        narrative.Scope.Should().Be(NarrativeScope.Era);
        narrative.ScopeLabel.Should().Be("College Years");
        narrative.SourceEventIds.Should().Equal("evt-inrange", "evt-linked");
        narrative.Sections.Should().ContainSingle()
            .Which.SourceEventIds.Should().Equal("evt-inrange", "evt-linked");
    }

    [Fact]
    public async Task GenerateAsync_PersonScope_GathersOnlyLinkedEvents()
    {
        // Arrange
        await SeedPersonAsync("p1", "Sarah");
        await SeedEventAsync("evt-with", "Coffee with Sarah", new DateTime(2020, 1, 5));
        await SeedEventAsync("evt-without", "Alone at home", new DateTime(2020, 2, 1));
        await LinkPersonAsync("evt-with", "p1");
        SetupSectionProse();

        // Act
        var narrative = await _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.Person,
            ScopeId = "p1"
        });

        // Assert: only the event_people-joined event; one section, no connective.
        narrative.ScopeLabel.Should().Be("Sarah");
        narrative.SourceEventIds.Should().Equal("evt-with");
        _llmMock.Verify(l => l.CompleteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_TagScope_GathersOnlyTaggedEvents()
    {
        // Arrange
        await SeedTagAsync("t1", "travel");
        await SeedEventAsync("evt-tagged", "Trip to Rome", new DateTime(2021, 4, 10));
        await SeedEventAsync("evt-untagged", "Regular day", new DateTime(2021, 5, 1));
        await LinkTagAsync("evt-tagged", "t1");
        SetupSectionProse();

        // Act
        var narrative = await _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.Tag,
            ScopeId = "t1"
        });

        // Assert
        narrative.ScopeLabel.Should().Be("travel");
        narrative.SourceEventIds.Should().Equal("evt-tagged");
    }

    [Fact]
    public async Task GenerateAsync_DateRangeScope_UsesOverlapSemantics()
    {
        // Arrange: a duration event spanning into the range counts; events
        // fully before/after do not.
        await SeedEventAsync("evt-before", "Before", new DateTime(2019, 1, 1));
        await SeedEventAsync("evt-spanning", "Spanning NYE", new DateTime(2019, 12, 20), endDate: new DateTime(2020, 1, 10));
        await SeedEventAsync("evt-inside", "Inside", new DateTime(2020, 6, 1));
        await SeedEventAsync("evt-after", "After", new DateTime(2021, 2, 1));
        SetupSectionProse();

        // Act
        var narrative = await _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.DateRange,
            From = new DateTime(2020, 1, 1),
            To = new DateTime(2020, 12, 31)
        });

        // Assert
        narrative.ScopeLabel.Should().Be("1 Jan 2020 – 31 Dec 2020");
        narrative.SourceEventIds.Should().Equal("evt-spanning", "evt-inside");
    }

    [Fact]
    public async Task GenerateAsync_SelectionScope_ResolvesIdsChronologically()
    {
        // Arrange
        await SeedEventAsync("e-2020", "Later", new DateTime(2020, 3, 1));
        await SeedEventAsync("e-2018", "Earlier", new DateTime(2018, 7, 1));
        await SeedEventAsync("e-2019", "Not selected", new DateTime(2019, 1, 1));
        SetupSectionProse();

        // Act
        var narrative = await _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.Selection,
            SelectedEventIds = new List<string> { "e-2020", "e-2018" }
        });

        // Assert: chronological regardless of the selection order.
        narrative.SourceEventIds.Should().Equal("e-2018", "e-2020");
        narrative.ScopeLabel.Should().Be("2 selected memories");
    }

    [Fact]
    public async Task GenerateAsync_EraNotFound_ThrowsInvalidOperation()
    {
        // Act
        Func<Task> act = () => _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.Era,
            ScopeId = "missing-era"
        });

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Era not found: missing-era");
    }

    [Fact]
    public async Task GenerateAsync_DateRangeWithoutBounds_ThrowsArgumentException()
    {
        // Act
        Func<Task> act = () => _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.DateRange
        });

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    #endregion

    #region Zero-event scope

    [Fact]
    public async Task GenerateAsync_ZeroEventScope_ReturnsEmptyNarrativeWithoutLlmCall()
    {
        // Arrange: empty database, nothing in the range.

        // Act
        var narrative = await _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.DateRange,
            From = new DateTime(2020, 1, 1),
            To = new DateTime(2020, 12, 31)
        });

        // Assert: empty narrative with a clear scope label, and NO LLM call.
        narrative.Sections.Should().BeEmpty();
        narrative.SourceEventIds.Should().BeEmpty();
        narrative.ScopeLabel.Should().Be("1 Jan 2020 – 31 Dec 2020");
        narrative.Title.Should().NotBeNullOrWhiteSpace();
        _llmMock.Verify(l => l.CompleteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region Chunking + connective pass

    [Fact]
    public async Task GenerateAsync_400EventRange_PartitionsByYearWithinCapAndOneConnectivePass()
    {
        // Arrange: 100 events per year 2018-2021 (400 total). Each year
        // exceeds the ~40 cap, so it splits into 3 near-equal runs =>
        // 12 sections, each <= 40 events and each inside a single year.
        var yearById = new Dictionary<string, int>(StringComparer.Ordinal);
        var events = new List<Event>();
        for (var year = 2018; year <= 2021; year++)
        {
            for (var i = 0; i < 100; i++)
            {
                var id = $"evt-{year}-{i:D3}";
                yearById[id] = year;
                events.Add(new Event
                {
                    EventId = id,
                    Title = $"Event {year}-{i:D3}",
                    StartDate = new DateTime(year, (i % 12) + 1, (i % 28) + 1),
                    Category = "other"
                });
            }
        }
        await SeedEventsBulkAsync(events);

        SetupSectionProse("Things happened this year.");
        SetupConnective("{\"title\":\"Four Years of Everything\",\"bridges\":[\"B1.\",\"B2.\"]}");

        // Act
        var narrative = await _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.DateRange,
            From = new DateTime(2018, 1, 1),
            To = new DateTime(2021, 12, 31),
            TargetWords = 2400
        });

        // Assert: partitioning.
        narrative.Sections.Should().HaveCount(12);
        narrative.Sections.Should().OnlyContain(s => s.SourceEventIds.Count <= 40);
        narrative.SourceEventIds.Should().HaveCount(400);
        narrative.Sections.Select(s => s.Heading).Should().OnlyHaveUniqueItems();

        foreach (var section in narrative.Sections)
        {
            section.SourceEventIds
                .Select(id => yearById[id])
                .Distinct()
                .Should().ContainSingle("each section must stay within one calendar year");
        }

        narrative.Sections
            .Select(s => yearById[s.SourceEventIds[0]])
            .Should().BeInAscendingOrder();

        // Assert: call counts — one LLM call per section plus ONE connective pass.
        _sectionPrompts.Should().HaveCount(12);
        _connectivePrompts.Should().HaveCount(1);

        // Assert: connective output applied — title plus bridges prepended
        // to the sections after the first.
        narrative.Title.Should().Be("Four Years of Everything");
        narrative.Sections[1].Prose.Should().StartWith("B1.");
        narrative.Sections[2].Prose.Should().StartWith("B2.");
    }

    [Fact]
    public async Task GenerateAsync_MalformedConnectiveJson_KeepsHeadingsAndDeterministicTitle()
    {
        // Arrange: 60 events over two years => 2 sections + connective pass.
        var events = new List<Event>();
        for (var i = 0; i < 30; i++)
        {
            events.Add(new Event { EventId = $"a-{i:D2}", Title = $"A{i}", StartDate = new DateTime(2020, (i % 12) + 1, 1), Category = "other" });
            events.Add(new Event { EventId = $"b-{i:D2}", Title = $"B{i}", StartDate = new DateTime(2021, (i % 12) + 1, 1), Category = "other" });
        }
        await SeedEventsBulkAsync(events);

        SetupSectionProse("Yearly prose.");
        SetupConnective("definitely not JSON");

        // Act
        var narrative = await _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.DateRange,
            From = new DateTime(2020, 1, 1),
            To = new DateTime(2021, 12, 31)
        });

        // Assert: deterministic scope title, headings intact, no bridges.
        narrative.Title.Should().Be("The story of 1 Jan 2020 – 31 Dec 2021");
        narrative.Sections.Should().HaveCount(2);
        narrative.Sections[0].Heading.Should().Be("2020");
        narrative.Sections[1].Heading.Should().Be("2021");
        narrative.Sections[1].Prose.Should().Be("Yearly prose.");
    }

    [Fact]
    public async Task GenerateAsync_CoarsePrecisionOnlyChunks_UseHonestHeadingsNotRawAnchorYears()
    {
        // Arrange: three year-groups of 30 events each (90 total, so the
        // partitioner splits and no adjacent pair merges under the 40 cap):
        //   - 1990 anchors, ALL Decade precision  -> heading must be "1990s"
        //   - 1995 anchors, ALL Unknown precision -> heading must be the scope
        //     label (an Unknown anchor is a placeholder, not a headline year)
        //   - 2020 anchors, Day precision         -> keeps the year heading
        var events = new List<Event>();
        for (var i = 0; i < 30; i++)
        {
            events.Add(new Event
            {
                EventId = $"dec-{i:D2}",
                Title = $"Decade memory {i}",
                StartDate = new DateTime(1990, (i % 12) + 1, 1),
                DatePrecision = DatePrecision.Decade,
                Category = "other"
            });
            events.Add(new Event
            {
                EventId = $"unk-{i:D2}",
                Title = $"Undated memory {i}",
                StartDate = new DateTime(1995, (i % 12) + 1, 1),
                DatePrecision = DatePrecision.Unknown,
                Category = "other"
            });
            events.Add(new Event
            {
                EventId = $"day-{i:D2}",
                Title = $"Dated memory {i}",
                StartDate = new DateTime(2020, (i % 12) + 1, 1),
                DatePrecision = DatePrecision.Day,
                Category = "other"
            });
        }
        await SeedEventsBulkAsync(events);

        SetupSectionProse("Coarse prose.");
        SetupConnective("not json"); // keep headings/title deterministic

        // Act
        var narrative = await _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.DateRange,
            From = new DateTime(1990, 1, 1),
            To = new DateTime(2020, 12, 31)
        });

        // Assert: the Decade-only chunk reads as the decade, the Unknown-only
        // chunk falls back to the scope label, and the Day chunk keeps its year.
        narrative.Sections.Should().HaveCount(3);
        narrative.Sections[0].Heading.Should().Be("1990s");
        narrative.Sections[1].Heading.Should().Be("1 Jan 1990 – 31 Dec 2020");
        narrative.Sections[2].Heading.Should().Be("2020");

        // The coarse chunks' raw anchor years must not leak into the prompts
        // (the heading is fed to the model as "# Section: {heading}").
        _sectionPrompts.Should().HaveCount(3);
        _sectionPrompts[0].User.Should().Contain("# Section: 1990s");
        _sectionPrompts[1].User.Should().NotContain("1995");
    }

    #endregion

    #region Prompt assembly (grounding + date precision)

    [Fact]
    public async Task GenerateAsync_YearPrecisionEvent_PromptUsesHonestDateTextNotFabricatedDate()
    {
        // Arrange: the anchor date is 1998-07-01 but the precision is Year —
        // the prompt must say "1998", never July or the exact day.
        await SeedEventAsync("evt-year", "Ski trip", new DateTime(1998, 7, 1), precision: DatePrecision.Year);
        SetupSectionProse();

        // Act
        await _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.DateRange,
            From = new DateTime(1998, 1, 1),
            To = new DateTime(1998, 12, 31),
            Voice = NarrativeVoice.Warm,
            TargetWords = 1200
        });

        // Assert
        _sectionPrompts.Should().ContainSingle();
        var (system, user) = _sectionPrompts[0];

        // Grounding rules are in the system prompt, verbatim.
        system.Should().Contain("no weather, no dialogue, no unstated emotions");
        system.Should().Contain("Never state a date more precisely than its date text");

        // Voice instruction and clamped per-section word target.
        system.Should().Contain("warm");
        system.Should().Contain("approximately 800 words");

        // The event line carries the DateDisplay.FormatPrecise text ("1998"),
        // not the raw anchor date.
        user.Should().Contain("[event:evt-year] 1998 — Ski trip");
        user.Should().NotContain("1998-07-01");
        user.Should().NotContain("Jul 1998");
        user.Should().NotContain("July 1998");
        user.Should().NotContain("1 Jul");
    }

    #endregion

    #region Citation integrity

    [Fact]
    public async Task GenerateAsync_ModelCitesGhostId_StripsGhostKeepsReal()
    {
        // Arrange
        await SeedEventAsync("evt-real", "Real memory", new DateTime(2020, 5, 5));
        SetupSectionProse("We did it. [event:evt-real] More happened. [event:evt-ghost]");

        // Act
        var narrative = await _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.DateRange,
            From = new DateTime(2020, 1, 1),
            To = new DateTime(2020, 12, 31)
        });

        // Assert: only markers whose id is in the section's source set survive.
        var prose = narrative.Sections.Should().ContainSingle().Which.Prose;
        prose.Should().Contain("[event:evt-real]");
        prose.Should().NotContain("evt-ghost");
        prose.Should().Be("We did it. [event:evt-real] More happened.");
    }

    [Fact]
    public async Task GenerateAsync_IncludeCitationsFalse_StripsAllMarkers()
    {
        // Arrange
        await SeedEventAsync("evt-real", "Real memory", new DateTime(2020, 5, 5));
        SetupSectionProse("It started well [event:evt-real], and ended better.");

        // Act
        var narrative = await _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.DateRange,
            From = new DateTime(2020, 1, 1),
            To = new DateTime(2020, 12, 31),
            IncludeCitations = false
        });

        // Assert: no markers at all, punctuation tidied.
        var prose = narrative.Sections.Should().ContainSingle().Which.Prose;
        prose.Should().NotContain("[event:");
        prose.Should().Be("It started well, and ended better.");
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task GenerateAsync_CopiesIncludeMediaOntoTheNarrative()
    {
        // Arrange: the export reads the choice from the Narrative, so the
        // request flag must land there — on empty narratives too.
        await SeedEventAsync("evt-1", "A day", new DateTime(2020, 1, 5));
        SetupSectionProse();

        // Act
        var withMedia = await _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.Selection,
            SelectedEventIds = new List<string> { "evt-1" }
        });
        var withoutMedia = await _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.Selection,
            SelectedEventIds = new List<string> { "evt-1" },
            IncludeMedia = false
        });
        var empty = await _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.Selection,
            SelectedEventIds = new List<string> { "no-such-event" }
        });

        // Assert
        withMedia.IncludeMedia.Should().BeTrue("the request default includes media");
        withoutMedia.IncludeMedia.Should().BeFalse();
        empty.IncludeMedia.Should().BeTrue("the zero-event narrative carries the flag as well");
    }

    [Fact]
    public async Task GenerateAsync_PreCancelledToken_ThrowsWithoutLlmCall()
    {
        // Arrange
        await SeedEventAsync("evt-1", "Something", new DateTime(2020, 1, 1));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        Func<Task> act = () => _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.DateRange,
            From = new DateTime(2020, 1, 1),
            To = new DateTime(2020, 12, 31)
        }, null, cts.Token);

        // Assert: nothing is returned and no tokens are spent — and since
        // export only ever runs after a completed generation, no partial
        // file can exist either.
        await act.Should().ThrowAsync<OperationCanceledException>();
        _llmMock.Verify(l => l.CompleteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateAsync_CancelledDuringFirstSection_StopsBeforeSecondSection()
    {
        // Arrange: two sections' worth of events; the first LLM call cancels
        // the token, and the between-section check must stop the run.
        var events = new List<Event>();
        for (var i = 0; i < 30; i++)
        {
            events.Add(new Event { EventId = $"a-{i:D2}", Title = $"A{i}", StartDate = new DateTime(2020, (i % 12) + 1, 1), Category = "other" });
            events.Add(new Event { EventId = $"b-{i:D2}", Title = $"B{i}", StartDate = new DateTime(2021, (i % 12) + 1, 1), Category = "other" });
        }
        await SeedEventsBulkAsync(events);

        using var cts = new CancellationTokenSource();
        _llmMock
            .Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel())
            .ReturnsAsync("First section prose.");

        // Act
        Func<Task> act = () => _service.GenerateAsync(new NarrativeRequest
        {
            Scope = NarrativeScope.DateRange,
            From = new DateTime(2020, 1, 1),
            To = new DateTime(2021, 12, 31)
        }, null, cts.Token);

        // Assert: throws, and only ONE call ever went out (no second section,
        // no connective pass).
        await act.Should().ThrowAsync<OperationCanceledException>();
        _llmMock.Verify(l => l.CompleteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Section calls: any CompleteAsync whose system prompt does NOT mention
    /// "bridge" (only the connective pass does). Captures prompts.
    /// </summary>
    private void SetupSectionProse(string prose = "The story unfolds.")
    {
        _llmMock
            .Setup(l => l.CompleteAsync(
                It.Is<string>(s => !s.Contains("bridge")),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, int, CancellationToken>((sys, user, _, _) => _sectionPrompts.Add((sys, user)))
            .ReturnsAsync(prose);
    }

    /// <summary>Connective pass: system prompt asks for bridges. Captures prompts.</summary>
    private void SetupConnective(string response)
    {
        _llmMock
            .Setup(l => l.CompleteAsync(
                It.Is<string>(s => s.Contains("bridge")),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, int, CancellationToken>((sys, user, _, _) => _connectivePrompts.Add((sys, user)))
            .ReturnsAsync(response);
    }

    private async Task SeedEventAsync(
        string eventId,
        string title,
        DateTime startDate,
        DateTime? endDate = null,
        string? eraId = null,
        DatePrecision precision = DatePrecision.Day,
        string? description = null)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.Events.Add(new Event
        {
            EventId = eventId,
            Title = title,
            StartDate = startDate,
            EndDate = endDate,
            EraId = eraId,
            DatePrecision = precision,
            Description = description,
            Category = "other"
        });
        await context.SaveChangesAsync();
    }

    private async Task SeedEventsBulkAsync(IEnumerable<Event> events)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.Events.AddRange(events);
        await context.SaveChangesAsync();
    }

    private async Task SeedEraAsync(string eraId, string name, DateTime start, DateTime? end)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.Eras.Add(new Era
        {
            EraId = eraId,
            Name = name,
            StartDate = start,
            EndDate = end
        });
        await context.SaveChangesAsync();
    }

    private async Task SeedPersonAsync(string personId, string name)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.People.Add(new Person { PersonId = personId, Name = name });
        await context.SaveChangesAsync();
    }

    private async Task LinkPersonAsync(string eventId, string personId)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.EventPeople.Add(new EventPerson { EventId = eventId, PersonId = personId });
        await context.SaveChangesAsync();
    }

    private async Task SeedTagAsync(string tagId, string tagName)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.Tags.Add(new Tag { TagId = tagId, TagName = tagName });
        await context.SaveChangesAsync();
    }

    private async Task LinkTagAsync(string eventId, string tagId)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.EventTags.Add(new EventTag { EventId = eventId, TagId = tagId });
        await context.SaveChangesAsync();
    }

    #endregion

    public void Dispose()
    {
        using var context = _contextFactory.CreateDbContext();
        context.Database.EnsureDeleted();
    }
}
