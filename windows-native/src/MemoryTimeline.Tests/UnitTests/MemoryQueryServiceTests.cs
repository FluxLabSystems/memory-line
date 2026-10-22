using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Tests;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Tests for MemoryQueryService ("Ask your timeline").
///
/// Uses a SQLite temp-FILE database (real SQL semantics for the keyword search)
/// with the REAL AdvancedSearchService, repositories and SettingsService over
/// TestDbContextFactory, and Moq mocks for the LLM and embedding services.
/// </summary>
public class MemoryQueryServiceTests : IDisposable
{
    private readonly TestDbContextFactory _contextFactory;
    private readonly string _databasePath;
    private readonly Mock<ILlmService> _llmServiceMock;
    private readonly Mock<IEmbeddingService> _embeddingServiceMock;
    private readonly MemoryQueryService _service;

    public MemoryQueryServiceTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"MemoryQueryTests_{Guid.NewGuid()}.db");
        _contextFactory = TestDbContextFactory.CreateSqliteFile(_databasePath);

        using (var context = _contextFactory.CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        _llmServiceMock = new Mock<ILlmService>();

        _embeddingServiceMock = new Mock<IEmbeddingService>();
        _embeddingServiceMock.Setup(e => e.IsAvailableAsync()).ReturnsAsync(true);
        _embeddingServiceMock.Setup(e => e.EmbeddingDimension).Returns(3);
        _embeddingServiceMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>()))
            .ReturnsAsync(new float[] { 0.1f, 0.2f, 0.3f });
        _embeddingServiceMock
            .Setup(e => e.FindKNearestNeighbors(
                It.IsAny<float[]>(),
                It.IsAny<IEnumerable<(string id, float[] embedding)>>(),
                It.IsAny<int>(),
                It.IsAny<double>()))
            .Returns(new List<SimilarityResult>());

        var searchService = new AdvancedSearchService(
            _contextFactory, new Mock<ILogger<AdvancedSearchService>>().Object);
        var settingsService = new SettingsService(
            _contextFactory, new Mock<ILogger<SettingsService>>().Object);

        _service = new MemoryQueryService(
            _llmServiceMock.Object,
            _embeddingServiceMock.Object,
            searchService,
            _contextFactory,
            new PersonRepository(_contextFactory),
            new TagRepository(_contextFactory),
            new LocationRepository(_contextFactory),
            settingsService,
            new Mock<ILogger<MemoryQueryService>>().Object);
    }

    #region FuseRankings

    [Fact]
    public void FuseRankings_TwoRankedLists_ProducesReciprocalRankFusionOrder()
    {
        // Arrange: with k=60 the RRF scores are
        //   e1: 1/61 + 1/62 = 0.03252...
        //   e3: 1/63 + 1/61 = 0.03226...
        //   e2: 1/62        = 0.01612...
        var listA = new List<string> { "e1", "e2", "e3" };
        var listB = new List<string> { "e3", "e1" };

        // Act
        var fused = MemoryQueryService.FuseRankings(new IReadOnlyList<string>[] { listA, listB });

        // Assert
        fused.Should().Equal("e1", "e3", "e2");
    }

    [Fact]
    public void FuseRankings_SingleList_PreservesOrder()
    {
        // Arrange
        var list = new List<string> { "a", "b", "c" };

        // Act
        var fused = MemoryQueryService.FuseRankings(new IReadOnlyList<string>[] { list });

        // Assert
        fused.Should().Equal("a", "b", "c");
    }

    [Fact]
    public void FuseRankings_EmptyInput_ReturnsEmpty()
    {
        // Act
        var fromNoLists = MemoryQueryService.FuseRankings(Array.Empty<IReadOnlyList<string>>());
        var fromEmptyLists = MemoryQueryService.FuseRankings(
            new IReadOnlyList<string>[] { new List<string>(), new List<string>() });

        // Assert
        fromNoLists.Should().BeEmpty();
        fromEmptyLists.Should().BeEmpty();
    }

    #endregion

    #region AskAsync — refusal (the correctness requirement)

    [Fact]
    public async Task AskAsync_ArchiveLacksAnswer_ReturnsHonestRefusal()
    {
        // Arrange: the archive knows Sarah, but nothing about her wedding.
        await SeedEventAsync("evt-1", "Coffee with Sarah", new DateTime(2019, 3, 14), "Met Sarah for coffee downtown.");
        await SeedEventAsync("evt-2", "Lunch with Sarah", new DateTime(2020, 6, 1));

        SetupLlmSequence(
            planJson: "{\"semanticQuery\":\"Sarah\",\"from\":null,\"to\":null,\"people\":[],\"tags\":[],\"locations\":[],\"answerMode\":\"Factual\"}",
            answerJson: "{\"answer\":\"I don't have anything about that in your archive.\",\"answeredFromArchive\":false,\"confidence\":0.2}");

        // Act
        var result = await _service.AskAsync(
            "When did Sarah get married?", new MemoryQueryOptions { TopK = 5 });

        // Assert: honest refusal — no citations, no leftover markers.
        result.AnsweredFromArchive.Should().BeFalse();
        result.Citations.Should().BeEmpty();
        result.Answer.Should().NotContain("[event:");
        result.Answer.Should().Contain("don't have anything");
        result.RetrievedEventIds.Should().NotBeEmpty("the archive had Sarah events to consider");
    }

    [Fact]
    public async Task AskAsync_NoMatchingEvents_RefusesWithoutAnswerCall()
    {
        // Arrange: nothing in the archive matches the plan's query.
        await SeedEventAsync("evt-1", "Coffee with Sarah", new DateTime(2019, 3, 14));

        _llmServiceMock
            .Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"semanticQuery\":\"xyzzy\",\"from\":null,\"to\":null,\"people\":[],\"tags\":[],\"locations\":[],\"answerMode\":\"Factual\"}");

        // Act
        var result = await _service.AskAsync(
            "Did I ever visit xyzzy?", new MemoryQueryOptions { TopK = 5 });

        // Assert: short-circuit refusal — only the plan call was made.
        result.AnsweredFromArchive.Should().BeFalse();
        result.Citations.Should().BeEmpty();
        result.RetrievedEventIds.Should().BeEmpty();
        _llmServiceMock.Verify(
            l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region AskAsync — citation integrity

    [Fact]
    public async Task AskAsync_ModelCitesUnretrievedEvent_KeepsOnlyRetrievedCitations()
    {
        // Arrange
        await SeedEventAsync("evt-real", "Met Sarah at the fair", new DateTime(2019, 3, 14), "We met at the county fair.");

        SetupLlmSequence(
            planJson: "{\"semanticQuery\":\"Sarah\",\"from\":null,\"to\":null,\"people\":[],\"tags\":[],\"locations\":[],\"answerMode\":\"Factual\"}",
            answerJson: "{\"answer\":\"You met Sarah at the county fair [event:evt-real] and again later [event:evt-ghost].\",\"answeredFromArchive\":true,\"confidence\":0.9}");

        // Act
        var result = await _service.AskAsync(
            "When did I first meet Sarah?", new MemoryQueryOptions { TopK = 5 });

        // Assert: the fabricated id never becomes a citation.
        result.Citations.Should().ContainSingle();
        result.Citations[0].EventId.Should().Be("evt-real");
        result.Citations[0].Title.Should().Be("Met Sarah at the fair");
        result.Citations.Select(c => c.EventId)
            .Should().OnlyContain(id => result.RetrievedEventIds.Contains(id));

        // Markers are replaced with [n] references; ghost markers are stripped.
        result.Answer.Should().NotContain("[event:");
        result.Answer.Should().Contain("[1]");
        result.Answer.Should().NotContain("evt-ghost");
        result.AnsweredFromArchive.Should().BeTrue();
    }

    #endregion

    #region AskAsync — merged-person name resolution

    [Fact]
    public async Task AskAsync_PersonFilterUsesRetiredMergedName_ReturnsSurvivorsEvents()
    {
        // Arrange: "Bob" was merged into "Robert" — the tombstone keeps its
        // name (zero event links), the alias points at the survivor, and the
        // event link lives on the survivor. Filtering by the old name must
        // resolve to the survivor, not the tombstone.
        await SeedMergedPersonFixtureAsync(
            survivorId: "person-robert", survivorName: "Robert",
            tombstoneId: "person-bob", tombstoneName: "Bob",
            alias: "Bob",
            eventId: "evt-fishing", eventTitle: "Fishing trip", eventDate: new DateTime(2021, 5, 1));

        SetupLlmSequence(
            planJson: "{\"semanticQuery\":\"Bob\",\"from\":null,\"to\":null,\"people\":[\"Bob\"],\"tags\":[],\"locations\":[],\"answerMode\":\"Factual\"}",
            answerJson: "{\"answer\":\"You went on a fishing trip together [event:evt-fishing].\",\"answeredFromArchive\":true,\"confidence\":0.9}");

        // Act
        var result = await _service.AskAsync(
            "What did I do with Bob?", new MemoryQueryOptions { TopK = 5 });

        // Assert: the old name filtered as the survivor's id, so the
        // survivor's events were retrieved and cited.
        result.RetrievedEventIds.Should().Contain("evt-fishing");
        result.AnsweredFromArchive.Should().BeTrue();
        result.Citations.Should().ContainSingle(c => c.EventId == "evt-fishing");
    }

    #endregion

    #region AskAsync — degraded (keyword-only) retrieval

    [Fact]
    public async Task AskAsync_EmbeddingServiceUnavailable_DegradesToKeywordRetrieval()
    {
        // Arrange: the embedding leg throws (no embedding API key configured).
        _embeddingServiceMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>()))
            .ThrowsAsync(new ConfigurationException("Embedding API key not configured — add it in Settings"));

        await SeedEventAsync("evt-1", "Coffee with Sarah", new DateTime(2019, 3, 14), "Met Sarah for coffee downtown.");

        SetupLlmSequence(
            planJson: "{\"semanticQuery\":\"Sarah\",\"from\":null,\"to\":null,\"people\":[],\"tags\":[],\"locations\":[],\"answerMode\":\"Factual\"}",
            answerJson: "{\"answer\":\"You had coffee with Sarah on 14 March 2019 [event:evt-1].\",\"answeredFromArchive\":true,\"confidence\":0.85}");

        // Act
        var result = await _service.AskAsync(
            "When did I last see Sarah?", new MemoryQueryOptions { TopK = 5 });

        // Assert: no throw, keyword retrieval still answered the question.
        result.UsedSemanticRetrieval.Should().BeFalse();
        result.RetrievedEventIds.Should().Contain("evt-1");
        result.AnsweredFromArchive.Should().BeTrue();
        result.Citations.Should().ContainSingle(c => c.EventId == "evt-1");
    }

    [Fact]
    public async Task AskAsync_StoredEmbeddingsFromOtherProvider_DegradesToKeywordAndReportsNoSemantics()
    {
        // Arrange: the archive was embedded by the OLD provider (1536-dim
        // rows) but the current provider returns 384-dim query vectors —
        // a provider switch without re-embedding. The semantic leg must throw
        // the typed mismatch internally; AskAsync must NOT surface it, must
        // answer keyword-only, and must report UsedSemanticRetrieval=false
        // instead of "semantic with zero contribution".
        await SeedEventAsync("evt-1", "Coffee with Sarah", new DateTime(2019, 3, 14), "Met Sarah for coffee downtown.");
        await SeedEmbeddingAsync("evt-1", new float[1536]);

        _embeddingServiceMock
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>()))
            .ReturnsAsync(new float[384]);

        SetupLlmSequence(
            planJson: "{\"semanticQuery\":\"Sarah\",\"from\":null,\"to\":null,\"people\":[],\"tags\":[],\"locations\":[],\"answerMode\":\"Factual\"}",
            answerJson: "{\"answer\":\"You had coffee with Sarah on 14 March 2019 [event:evt-1].\",\"answeredFromArchive\":true,\"confidence\":0.85}");

        // Act — must not throw
        var result = await _service.AskAsync(
            "When did I last see Sarah?", new MemoryQueryOptions { TopK = 5 });

        // Assert: keyword-only result with honest semantic metadata.
        result.UsedSemanticRetrieval.Should().BeFalse();
        result.RetrievedEventIds.Should().Contain("evt-1");
        result.AnsweredFromArchive.Should().BeTrue();
        result.Citations.Should().ContainSingle(c => c.EventId == "evt-1");
        _embeddingServiceMock.Verify(
            e => e.FindKNearestNeighbors(
                It.IsAny<float[]>(),
                It.IsAny<IEnumerable<(string id, float[] embedding)>>(),
                It.IsAny<int>(),
                It.IsAny<double>()),
            Times.Never,
            "mixed-dimension candidates must never reach the kNN math");
    }

    #endregion

    #region AskAsync — defensive parsing

    [Fact]
    public async Task AskAsync_MalformedPlanJson_FallsBackToRawQuestionRetrieval()
    {
        // Arrange: the plan call returns garbage; the raw question ("Sarah")
        // becomes both the keyword and semantic query.
        await SeedEventAsync("evt-1", "Coffee with Sarah", new DateTime(2019, 3, 14));

        SetupLlmSequence(
            planJson: "this is definitely not JSON",
            answerJson: "{\"answer\":\"You had coffee with Sarah [event:evt-1].\",\"answeredFromArchive\":true,\"confidence\":0.8}");

        // Act
        var result = await _service.AskAsync("Sarah", new MemoryQueryOptions { TopK = 5 });

        // Assert
        result.RetrievedEventIds.Should().Contain("evt-1");
        result.AnsweredFromArchive.Should().BeTrue();
        result.Citations.Should().ContainSingle(c => c.EventId == "evt-1");
    }

    [Fact]
    public async Task AskAsync_MalformedAnswerJson_TreatsTextAsUngroundedAnswer()
    {
        // Arrange
        await SeedEventAsync("evt-1", "Coffee with Sarah", new DateTime(2019, 3, 14));

        SetupLlmSequence(
            planJson: "{\"semanticQuery\":\"Sarah\",\"from\":null,\"to\":null,\"people\":[],\"tags\":[],\"locations\":[],\"answerMode\":\"Factual\"}",
            answerJson: "Just some plain text that is not JSON.");

        // Act
        var result = await _service.AskAsync(
            "When did I see Sarah?", new MemoryQueryOptions { TopK = 5 });

        // Assert: the text is surfaced but never claimed as grounded.
        result.Answer.Should().Be("Just some plain text that is not JSON.");
        result.AnsweredFromArchive.Should().BeFalse();
        result.Confidence.Should().Be(0);
        result.Citations.Should().BeEmpty();
    }

    #endregion

    #region Streaming (progress overload + AskStreamingAsync)

    private sealed class CollectingProgress : IProgress<string>
    {
        public List<string> Chunks { get; } = new();

        public void Report(string value) => Chunks.Add(value);
    }

    private static async IAsyncEnumerable<string> Pieces(params string[] pieces)
    {
        foreach (var piece in pieces)
        {
            await Task.Yield();
            yield return piece;
        }
    }

    /// <summary>Plan via CompleteAsync; the grounded answer via the token stream, in the given pieces.</summary>
    private void SetupStreamingLlm(string planJson, params string[] answerPieces)
    {
        _llmServiceMock
            .Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(planJson);
        _llmServiceMock
            .Setup(l => l.StreamCompletionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Pieces(answerPieces));
    }

    private const string SarahPlanJson =
        "{\"semanticQuery\":\"Sarah\",\"from\":null,\"to\":null,\"people\":[],\"tags\":[],\"locations\":[],\"answerMode\":\"Factual\"}";

    [Fact]
    public async Task AskAsync_WithProgress_StreamsTheDisplayAnswerAsItArrivesAndMatchesTheFinalAnswer()
    {
        // Arrange: the answer JSON arrives in pieces that split the key, a
        // valid marker, a fabricated marker, and a stray space before the
        // period - everything the display rewriting has to cope with.
        await SeedEventAsync("evt-real", "Met Sarah at the fair", new DateTime(2019, 3, 14), "We met at the county fair.");
        SetupStreamingLlm(
            SarahPlanJson,
            "{\"ans", "wer\":\"You met Sarah [ev", "ent:evt-real] at the fair [event:evt-ghost]",
            " .\",\"answeredFromArchive\":true,\"confidence\":0.9}");
        var progress = new CollectingProgress();

        // Act
        var result = await _service.AskAsync(
            "When did I first meet Sarah?", new MemoryQueryOptions { TopK = 5 }, progress);

        // Assert: the streamed text is the final display text, chunk by chunk;
        // the answer stage never used the non-streaming call; citations intact.
        result.Answer.Should().Be("You met Sarah [1] at the fair.");
        string.Concat(progress.Chunks).Should().Be(result.Answer);
        progress.Chunks.Count.Should().BeGreaterThan(1, "the answer was shown while it arrived, not whole at the end");
        result.Citations.Should().ContainSingle().Which.EventId.Should().Be("evt-real");
        result.AnsweredFromArchive.Should().BeTrue();
        _llmServiceMock.Verify(
            l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once, "only the retrieval plan goes through the non-streaming call");
        _llmServiceMock.Verify(
            l => l.StreamCompletionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AskStreamingAsync_YieldsTheSameDisplayChunks()
    {
        // Arrange
        await SeedEventAsync("evt-real", "Met Sarah at the fair", new DateTime(2019, 3, 14), "We met at the county fair.");
        SetupStreamingLlm(
            SarahPlanJson,
            "{\"answer\":\"You met Sarah ", "[event:evt-real].\",\"answeredFromArchive\":true,\"confidence\":0.9}");

        // Act
        var chunks = new List<string>();
        await foreach (var chunk in _service.AskStreamingAsync("When did I first meet Sarah?", new MemoryQueryOptions { TopK = 5 }))
        {
            chunks.Add(chunk);
        }

        // Assert
        string.Concat(chunks).Should().Be("You met Sarah [1].");
        chunks.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task AskAsync_WithProgress_NoMatchingEvents_ReportsTheRefusalOnceWithoutStreaming()
    {
        // Arrange: an empty archive; the plan call still runs, the answer never does.
        SetupStreamingLlm(SarahPlanJson, "never used");
        var progress = new CollectingProgress();

        // Act
        var result = await _service.AskAsync("Did I ever go to Mars?", new MemoryQueryOptions { TopK = 5 }, progress);

        // Assert
        result.AnsweredFromArchive.Should().BeFalse();
        progress.Chunks.Should().ContainSingle().Which.Should().Be(result.Answer);
        _llmServiceMock.Verify(
            l => l.StreamCompletionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AskAsync_WithProgress_ModelReturnsPlainText_ReportsTheWholeTextOnceAndNeverClaimsGrounding()
    {
        // Arrange: no JSON shape at all, so nothing can be shown until the
        // end; then the whole text is shown once, matching the batch fallback.
        await SeedEventAsync("evt-real", "Met Sarah at the fair", new DateTime(2019, 3, 14), "We met at the county fair.");
        SetupStreamingLlm(SarahPlanJson, "Your archive ", "doesn't mention that.");
        var progress = new CollectingProgress();

        // Act
        var result = await _service.AskAsync("When did I first meet Sarah?", new MemoryQueryOptions { TopK = 5 }, progress);

        // Assert
        result.Answer.Should().Be("Your archive doesn't mention that.");
        progress.Chunks.Should().ContainSingle().Which.Should().Be(result.Answer);
        result.AnsweredFromArchive.Should().BeFalse();
        result.Confidence.Should().Be(0);
    }

    [Fact]
    public async Task AskAsync_WithProgress_StreamingFailure_PropagatesToTheCaller()
    {
        // Arrange: no provider configured surfaces from the stream exactly as
        // it would from the non-streaming call.
        await SeedEventAsync("evt-real", "Met Sarah at the fair", new DateTime(2019, 3, 14), "We met at the county fair.");
        _llmServiceMock
            .Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SarahPlanJson);
        _llmServiceMock
            .Setup(l => l.StreamCompletionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Failing());

        // Act
        var act = () => _service.AskAsync("When did I first meet Sarah?", new MemoryQueryOptions { TopK = 5 }, new CollectingProgress());
        var streamingAct = async () =>
        {
            await foreach (var _ in _service.AskStreamingAsync("When did I first meet Sarah?", new MemoryQueryOptions { TopK = 5 }))
            {
            }
        };

        // Assert
        await act.Should().ThrowAsync<ConfigurationException>();
        await streamingAct.Should().ThrowAsync<ConfigurationException>();

        static async IAsyncEnumerable<string> Failing()
        {
            await Task.Yield();
            throw new ConfigurationException("LLM base URL not configured");
#pragma warning disable CS0162 // unreachable: the iterator needs a yield to be an iterator
            yield break;
#pragma warning restore CS0162
        }
    }

    #endregion

    #region SplitIntoWordChunks

    [Fact]
    public void SplitIntoWordChunks_Roundtrip_ReconstructsText()
    {
        // Arrange
        const string text = "You met Sarah [1] at the fair.";

        // Act
        var chunks = MemoryQueryService.SplitIntoWordChunks(text).ToList();

        // Assert
        chunks.Should().NotBeEmpty();
        string.Concat(chunks).Should().Be(text);
    }

    [Fact]
    public void SplitIntoWordChunks_EmptyText_YieldsNothing()
    {
        MemoryQueryService.SplitIntoWordChunks(null).Should().BeEmpty();
        MemoryQueryService.SplitIntoWordChunks(string.Empty).Should().BeEmpty();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// First CompleteAsync call returns the retrieval plan, second returns the
    /// grounded answer — matching the two-call pipeline shape.
    /// </summary>
    private void SetupLlmSequence(string planJson, string answerJson)
    {
        _llmServiceMock
            .SetupSequence(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(planJson)
            .ReturnsAsync(answerJson);
    }

    /// <summary>
    /// Seeds a post-merge people fixture: a living survivor linked to one
    /// event, a tombstone (MergedIntoId set) that kept the retired name, and
    /// the alias row MergeAsync records so old mentions resolve.
    /// </summary>
    private async Task SeedMergedPersonFixtureAsync(
        string survivorId, string survivorName,
        string tombstoneId, string tombstoneName,
        string alias,
        string eventId, string eventTitle, DateTime eventDate)
    {
        await SeedEventAsync(eventId, eventTitle, eventDate);

        await using var context = _contextFactory.CreateDbContext();
        context.People.Add(new Person
        {
            PersonId = survivorId,
            Name = survivorName,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        context.People.Add(new Person
        {
            PersonId = tombstoneId,
            Name = tombstoneName,
            MergedIntoId = survivorId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        context.PersonAliases.Add(new PersonAlias
        {
            PersonId = survivorId,
            Alias = alias
        });
        context.EventPeople.Add(new EventPerson
        {
            EventId = eventId,
            PersonId = survivorId,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds a stored event embedding row whose vector (and dimension column)
    /// come from <paramref name="vector"/> — e.g. a 1536-dim row left behind
    /// by a previous embedding provider.
    /// </summary>
    private async Task SeedEmbeddingAsync(string eventId, float[] vector)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.EventEmbeddings.Add(new EventEmbedding
        {
            EmbeddingId = Guid.NewGuid().ToString(),
            EventId = eventId,
            EmbeddingVector = System.Text.Json.JsonSerializer.Serialize(vector),
            EmbeddingProvider = "openai",
            EmbeddingModel = "text-embedding-3-small",
            EmbeddingDimension = vector.Length,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private async Task SeedEventAsync(string eventId, string title, DateTime startDate, string? description = null)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.Events.Add(new Event
        {
            EventId = eventId,
            Title = title,
            StartDate = startDate,
            Description = description,
            Category = "other",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    #endregion

    public void Dispose()
    {
        using (var context = _contextFactory.CreateDbContext())
        {
            context.Database.EnsureDeleted();
        }

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
