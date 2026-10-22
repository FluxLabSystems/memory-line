using System.Text.Json;
using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Sync;
using MemoryTimeline.SyncContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// The Windows side of a Phase 4 assistant turn: <see cref="AssistantTurnResponder"/>
/// answers a pending <c>assistant_turn</c> through the Ask service and queues
/// the answered turn in the shape the service accepts; <see cref="RemoteChangeApplier"/>
/// routes the change to it; and a whole sync cycle carries the answer from a
/// fake pull to a fake push. Real outbox repository over EF InMemory, fake
/// <see cref="IMemoryQueryService"/> and <see cref="ISyncClient"/>.
/// </summary>
public class AssistantTurnResponderTests
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private readonly TestDbContextFactory _contextFactory = TestDbContextFactory.CreateInMemory();
    private readonly Mock<IMemoryQueryService> _queryService = new();
    private readonly SyncOutboxRepository _outbox;
    private readonly AssistantTurnResponder _responder;

    public AssistantTurnResponderTests()
    {
        _outbox = new SyncOutboxRepository(_contextFactory);
        _responder = new AssistantTurnResponder(_queryService.Object, _outbox, NullLogger<AssistantTurnResponder>.Instance);
    }

    #region Answering

    [Fact]
    public async Task ApplyAsync_PendingWindowsTurn_AnswersAndQueuesTheCompletedTurn()
    {
        // Arrange - the phone asked; the archive has two relevant memories
        var citedEventId = Guid.NewGuid().ToString();
        _queryService
            .Setup(q => q.AskAsync("When did we go to the island?", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryAnswer
            {
                Question = "When did we go to the island?",
                Answer = "You took the ferry to the island in the summer of 2003 [1].",
                AnsweredFromArchive = true,
                Citations = new List<CitedEvent>
                {
                    new(citedEventId, "Ferry to the island", new DateTime(2003, 7, 14), "The early crossing was rough", DatePrecision.Season),
                    new("not-a-uuid", "Legacy row", new DateTime(2001, 1, 1), "dropped", DatePrecision.Day),
                },
            });
        var change = CreateTurnChange(out var incoming, question: "When did we go to the island?");

        // Act
        var result = await _responder.ApplyAsync(change);

        // Assert
        result.Should().Be(ChangeApplicationResult.Applied);

        var row = await SingleRowAsync();
        row.EntityType.Should().Be(SyncChangeEntityType.AssistantTurn);
        row.EntityId.Should().Be(incoming.TurnId);
        row.Operation.Should().Be(SyncOutboxOperation.Upsert);
        row.DeliveredAt.Should().BeNull();

        // camelCase on the wire, like every other sync body
        using (var document = JsonDocument.Parse(row.PayloadJson!))
        {
            document.RootElement.TryGetProperty("turnId", out _).Should().BeTrue();
            document.RootElement.TryGetProperty("actualResponder", out _).Should().BeTrue();
        }

        var answered = Deserialize(row);
        answered.TurnId.Should().Be(incoming.TurnId);
        answered.SessionId.Should().Be(incoming.SessionId);
        answered.Sequence.Should().Be(incoming.Sequence);
        answered.Question.Should().Be(incoming.Question);
        answered.OriginDeviceId.Should().Be(incoming.OriginDeviceId);
        answered.Status.Should().Be(AssistantTurnStatus.Completed);
        answered.RequestedResponder.Should().Be(AssistantResponder.Windows);
        answered.ActualResponder.Should().Be(AssistantResponder.Windows);
        answered.FailureReason.Should().BeNull();
        answered.FailureRetryable.Should().BeNull();
        answered.UpdatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        answered.Result.Should().NotBeNull();
        answered.Result!.Answer.Should().Be("You took the ferry to the island in the summer of 2003 [1].");
        answered.Result.Grounding.Should().Be(AssistantGrounding.Archive);
        answered.Result.ElapsedMs.Should().NotBeNull();

        // Citations: UUID ids only, the precision-honest date already formatted
        var citation = answered.Result.Citations.Should().ContainSingle().Subject;
        citation.EventId.Should().Be(citedEventId);
        citation.Title.Should().Be("Ferry to the island");
        citation.DisplayDate.Should().Be(Core.Models.DateDisplay.FormatPrecise(new DateTime(2003, 7, 14), DatePrecision.Season));
        citation.Excerpt.Should().Be("The early crossing was rough");
    }

    [Fact]
    public async Task ApplyAsync_HonestRefusal_CompletesWithNoCitations()
    {
        // Arrange - the archive has nothing on the subject
        _queryService
            .Setup(q => q.AskAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryAnswer
            {
                Answer = "I couldn't find anything in your timeline about that.",
                AnsweredFromArchive = false,
            });
        var change = CreateTurnChange(out _);

        // Act
        var result = await _responder.ApplyAsync(change);

        // Assert
        result.Should().Be(ChangeApplicationResult.Applied);
        var answered = Deserialize(await SingleRowAsync());
        answered.Status.Should().Be(AssistantTurnStatus.Completed);
        answered.Result!.Citations.Should().BeEmpty();
        answered.Result.Grounding.Should().Be(AssistantGrounding.Archive);
    }

    [Fact]
    public async Task ApplyAsync_Replay_DoesNotAskTwice()
    {
        // Arrange
        _queryService
            .Setup(q => q.AskAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryAnswer { Answer = "Once.", AnsweredFromArchive = true });
        var change = CreateTurnChange(out _);

        // Act - the pull page is replayed after a later retryable failure
        var first = await _responder.ApplyAsync(change);
        var second = await _responder.ApplyAsync(change);

        // Assert
        first.Should().Be(ChangeApplicationResult.Applied);
        second.Should().Be(ChangeApplicationResult.Applied);
        _queryService.Verify(q => q.AskAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()), Times.Once);
        (await AllRowsAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task ApplyAsync_OverlongAnswerAndTooManyCitations_AreCutToTheServicesBounds()
    {
        // Arrange
        var citations = Enumerable.Range(0, 60)
            .Select(i => new CitedEvent(Guid.NewGuid().ToString(), $"Event {i}", new DateTime(2020, 1, 1), new string('x', 2_000)))
            .ToList();
        _queryService
            .Setup(q => q.AskAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryAnswer { Answer = new string('a', 25_000), AnsweredFromArchive = true, Citations = citations });
        var change = CreateTurnChange(out _);

        // Act
        await _responder.ApplyAsync(change);

        // Assert - the responder decides what to leave out; the service rejects rather than trims
        var answered = Deserialize(await SingleRowAsync());
        answered.Result!.Answer.Length.Should().Be(AssistantTurnResponder.AnswerMaxChars);
        answered.Result.Citations.Should().HaveCount(AssistantTurnResponder.CitationMaxCount);
        answered.Result.Citations.Should().OnlyContain(c => c.Excerpt!.Length == AssistantTurnResponder.CitationExcerptMaxChars);
    }

    #endregion

    #region Failures

    [Fact]
    public async Task ApplyAsync_NoProviderConfigured_FailsTheTurnWithADisplaySafeNonRetryableReason()
    {
        // Arrange
        _queryService
            .Setup(q => q.AskAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConfigurationException("Anthropic API key not configured — add it in Settings"));
        var change = CreateTurnChange(out _);

        // Act
        var result = await _responder.ApplyAsync(change);

        // Assert
        result.Should().Be(ChangeApplicationResult.Applied);
        var answered = Deserialize(await SingleRowAsync());
        answered.Status.Should().Be(AssistantTurnStatus.Failed);
        answered.Result.Should().BeNull();
        answered.FailureRetryable.Should().BeFalse();
        answered.FailureReason.Should().Be(AssistantTurnResponder.NotConfiguredReason);
    }

    [Fact]
    public async Task ApplyAsync_AnsweringThrows_FailsTheTurnWithoutLeakingTheException()
    {
        // Arrange - an exception message that would carry an endpoint
        _queryService
            .Setup(q => q.AskAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused: http://192.168.1.20:11434/v1/chat/completions"));
        var change = CreateTurnChange(out _);

        // Act
        var result = await _responder.ApplyAsync(change);

        // Assert
        result.Should().Be(ChangeApplicationResult.Applied);
        var answered = Deserialize(await SingleRowAsync());
        answered.Status.Should().Be(AssistantTurnStatus.Failed);
        answered.FailureRetryable.Should().BeTrue();
        answered.FailureReason.Should().Be(AssistantTurnResponder.AnswerFailedReason);
        answered.FailureReason.Should().NotContain("192.168");
    }

    [Fact]
    public async Task ApplyAsync_EmptyAnswer_FailsRetryablyRatherThanCompletingSilently()
    {
        _queryService
            .Setup(q => q.AskAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryAnswer { Answer = "   " });
        var change = CreateTurnChange(out _);

        await _responder.ApplyAsync(change);

        var answered = Deserialize(await SingleRowAsync());
        answered.Status.Should().Be(AssistantTurnStatus.Failed);
        answered.FailureRetryable.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyAsync_Cancelled_PropagatesWithoutPublishing()
    {
        _queryService
            .Setup(q => q.AskAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var change = CreateTurnChange(out _);

        var act = () => _responder.ApplyAsync(change);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await AllRowsAsync()).Should().BeEmpty();
    }

    #endregion

    #region Skips and rejections

    [Theory]
    [InlineData(AssistantResponder.Provider)]
    [InlineData(AssistantResponder.OnDevice)]
    public async Task ApplyAsync_NotForWindows_IsSkipped(string responder)
    {
        var change = CreateTurnChange(out _, requestedResponder: responder);

        var result = await _responder.ApplyAsync(change);

        result.Should().Be(ChangeApplicationResult.Skipped);
        _queryService.VerifyNoOtherCalls();
        (await AllRowsAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(AssistantTurnStatus.Cancelled)]
    [InlineData(AssistantTurnStatus.Completed)]
    [InlineData(AssistantTurnStatus.Dispatched)]
    public async Task ApplyAsync_TurnNotPending_IsSkipped(string status)
    {
        var change = CreateTurnChange(out _, status: status);

        var result = await _responder.ApplyAsync(change);

        result.Should().Be(ChangeApplicationResult.Skipped);
        _queryService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ApplyAsync_UnsetResponder_DefaultsToWindows()
    {
        _queryService
            .Setup(q => q.AskAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryAnswer { Answer = "Yes.", AnsweredFromArchive = true });
        var change = CreateTurnChange(out _, requestedResponder: "");

        var result = await _responder.ApplyAsync(change);

        result.Should().Be(ChangeApplicationResult.Applied);
        Deserialize(await SingleRowAsync()).RequestedResponder.Should().Be(AssistantResponder.Windows);
    }

    [Fact]
    public async Task ApplyAsync_DeleteOperation_IsSkipped()
    {
        var change = CreateTurnChange(out _);
        change.Operation = SyncOperation.Delete;

        (await _responder.ApplyAsync(change)).Should().Be(ChangeApplicationResult.Skipped);
    }

    [Fact]
    public async Task ApplyAsync_MalformedOrMismatchedPayload_IsPermanentlySkipped()
    {
        var garbage = CreateTurnChange(out _);
        garbage.PayloadJson = "{not json";
        (await _responder.ApplyAsync(garbage)).Should().Be(ChangeApplicationResult.FailedPermanent);

        var empty = CreateTurnChange(out _);
        empty.PayloadJson = null;
        (await _responder.ApplyAsync(empty)).Should().Be(ChangeApplicationResult.FailedPermanent);

        var mismatched = CreateTurnChange(out _);
        mismatched.EntityId = Guid.NewGuid().ToString();
        (await _responder.ApplyAsync(mismatched)).Should().Be(ChangeApplicationResult.FailedPermanent);

        _queryService.VerifyNoOtherCalls();
    }

    #endregion

    #region Routing and the whole cycle

    [Fact]
    public async Task RemoteChangeApplier_AssistantTurn_RoutesToTheResponder_AndChunksAreSkipped()
    {
        // Arrange
        var responder = new Mock<IAssistantTurnResponder>();
        responder.Setup(r => r.ApplyAsync(It.IsAny<SyncChangeDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChangeApplicationResult.Applied);
        var applier = CreateApplier(responder.Object);
        var turn = CreateTurnChange(out _);
        var chunk = new SyncChangeDto
        {
            ChangeId = 99,
            EntityType = SyncChangeEntityType.AssistantTurnChunk,
            EntityId = turn.EntityId,
            Operation = SyncOperation.Upsert,
            PayloadJson = "{}",
        };

        // Act
        var turnResult = await applier.ApplyAsync(turn);
        var chunkResult = await applier.ApplyAsync(chunk);

        // Assert
        turnResult.Should().Be(ChangeApplicationResult.Applied);
        responder.Verify(r => r.ApplyAsync(turn, It.IsAny<CancellationToken>()), Times.Once);
        chunkResult.Should().Be(ChangeApplicationResult.Skipped);
    }

    [Fact]
    public async Task RemoteChangeApplier_NoResponderRegistered_SkipsTheTurn()
    {
        var applier = CreateApplier(responder: null);

        (await applier.ApplyAsync(CreateTurnChange(out _))).Should().Be(ChangeApplicationResult.Skipped);
    }

    [Fact]
    public async Task SyncCycle_PullsAQuestion_AndPushesTheAnswerThroughTheSyncClient()
    {
        // Arrange - a fake API: one pending turn on the feed, and a push that
        // records what it receives
        _queryService
            .Setup(q => q.AskAsync("What did we do in Paris?", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryAnswer { Answer = "You climbed the tower.", AnsweredFromArchive = true });
        var change = CreateTurnChange(out var incoming, question: "What did we do in Paris?");

        var syncClient = new Mock<ISyncClient>();
        syncClient.Setup(c => c.PullChangesAsync(0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncPullResponse { Changes = new List<SyncChangeDto> { change }, NextCursor = change.ChangeId, HasMore = false });
        SyncPushRequest? pushed = null;
        syncClient.Setup(c => c.PushChangesAsync(It.IsAny<SyncPushRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SyncPushRequest, CancellationToken>((request, _) => pushed = request)
            .ReturnsAsync((SyncPushRequest request, CancellationToken _) => new SyncPushResponse
            {
                Results = request.Entries.Select(e => new SyncPushEntryResult { ClientSequence = e.ClientSequence, Accepted = true, ServerChangeId = 500 }).ToList(),
            });

        var settingsStore = new FakeSyncSettingsStore();
        var worker = new SyncBackgroundWorker(
            syncClient.Object,
            CreateApplier(_responder),
            new LocalOutboxPublisher(_outbox, syncClient.Object, settingsStore, NullLogger<LocalOutboxPublisher>.Instance),
            new FakeCursorStore(),
            settingsStore,
            NullLogger<SyncBackgroundWorker>.Instance);

        // Act - one cycle: pull → answer → drain
        var cycle = await worker.SyncNowAsync();

        // Assert
        cycle.Ran.Should().BeTrue();
        cycle.Error.Should().BeNull();
        cycle.ChangesApplied.Should().Be(1);
        cycle.OutboxPublished.Should().Be(1);

        pushed.Should().NotBeNull();
        var entry = pushed!.Entries.Should().ContainSingle().Subject;
        entry.EntityType.Should().Be(SyncChangeEntityType.AssistantTurn);
        entry.EntityId.Should().Be(incoming.TurnId);
        entry.Operation.Should().Be(SyncOperation.Upsert);
        var answered = JsonSerializer.Deserialize<AssistantTurnChangePayload>(entry.PayloadJson!, WireJson)!;
        answered.Status.Should().Be(AssistantTurnStatus.Completed);
        answered.ActualResponder.Should().Be(AssistantResponder.Windows);
        answered.Result!.Answer.Should().Be("You climbed the tower.");

        (await SingleRowAsync()).DeliveredAt.Should().NotBeNull("the accepted push marks the row delivered");
    }

    #endregion

    #region Helpers

    private RemoteChangeApplier CreateApplier(IAssistantTurnResponder? responder) => new(
        new Mock<IArtifactTransferClient>().Object,
        new Mock<ICaptureIngestionService>().Object,
        new Mock<IQueueService>().Object,
        new FakeSyncSettingsStore(),
        NullLogger<RemoteChangeApplier>.Instance,
        decisionApplier: null,
        assistantResponder: responder);

    private static SyncChangeDto CreateTurnChange(
        out AssistantTurnChangePayload payload,
        string question = "When did we go to the island?",
        string status = AssistantTurnStatus.Pending,
        string? requestedResponder = AssistantResponder.Windows)
    {
        payload = new AssistantTurnChangePayload
        {
            TurnId = Guid.NewGuid().ToString(),
            SessionId = Guid.NewGuid().ToString(),
            Sequence = 3,
            Question = question,
            Status = status,
            RequestedResponder = requestedResponder ?? string.Empty,
            OriginDeviceId = "iphone-1",
            UpdatedAtUtc = DateTime.UtcNow.AddSeconds(-5),
        };

        return new SyncChangeDto
        {
            ChangeId = 42,
            EntityType = SyncChangeEntityType.AssistantTurn,
            EntityId = payload.TurnId,
            Operation = SyncOperation.Upsert,
            Revision = 1,
            ChangedAtUtc = payload.UpdatedAtUtc,
            SourceDeviceId = "iphone-1",
            PayloadJson = JsonSerializer.Serialize(payload, WireJson),
        };
    }

    private static AssistantTurnChangePayload Deserialize(SyncOutbox row) =>
        JsonSerializer.Deserialize<AssistantTurnChangePayload>(row.PayloadJson!, WireJson)!;

    private async Task<SyncOutbox> SingleRowAsync()
    {
        await using var context = _contextFactory.CreateDbContext();
        return await context.SyncOutboxEntries.AsNoTracking().SingleAsync();
    }

    private async Task<List<SyncOutbox>> AllRowsAsync()
    {
        await using var context = _contextFactory.CreateDbContext();
        return await context.SyncOutboxEntries.AsNoTracking().OrderBy(o => o.OutboxId).ToListAsync();
    }

    private sealed class FakeCursorStore : ISyncCursorStore
    {
        private long _cursor;

        public Task<long> GetCursorAsync() => Task.FromResult(_cursor);

        public Task SetCursorAsync(long cursor)
        {
            _cursor = cursor;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSyncSettingsStore : ISyncSettingsStore
    {
        public Task<bool> IsSyncEnabledAsync() => Task.FromResult(true);

        public Task<string?> GetServerUrlAsync() => Task.FromResult<string?>("https://sync.example.test");

        public Task<string?> GetDeviceIdAsync() => Task.FromResult<string?>("windows-device-1");

        public Task<string?> GetAccessTokenAsync() => Task.FromResult<string?>("access-token");

        public Task<string?> GetRefreshTokenAsync() => Task.FromResult<string?>("refresh-token");

        public Task<bool> IsAutoProcessEnabledAsync() => Task.FromResult(true);

        public Task SaveRegistrationAsync(string serverUrl, DeviceRegisterResponse registration) => Task.CompletedTask;

        public Task SaveTokensAsync(string accessToken, string refreshToken) => Task.CompletedTask;

        public Task ClearRegistrationAsync() => Task.CompletedTask;
    }

    #endregion
}
