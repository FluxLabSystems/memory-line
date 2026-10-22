using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MemoryTimeline.SyncApi.Application;
using MemoryTimeline.SyncContracts;
using Xunit;

namespace MemoryTimeline.SyncApi.Tests;

/// <summary>
/// The Phase 4 assistant round trip against the real host (design §19 Phase 4):
/// a phone opens a session and submits a question, the service persists it and
/// publishes an <c>assistant_turn</c> change, Windows answers by pushing that
/// change back, and the phone reads the answer either from the turn it polls or
/// from its own pull feed. The service never answers anything itself — every
/// answer in these tests is one a test client pushed.
///
/// The boundaries worth breaking are all here too: a second device must not be
/// able to read another device's conversation, a resubmitted turn must not ask
/// twice, oversized context must be refused rather than trimmed, and a
/// cancelled turn must stay cancelled when the answer finally lands. Tests
/// share one host per class, so each takes a per-device baseline cursor before
/// acting.
/// </summary>
public class AssistantFlowTests : IClassFixture<SyncApiFixture>
{
    /// <summary>Fixed responder timestamp, so payload round trips can be asserted exactly.</summary>
    private static readonly DateTime AnsweredAt = new(2026, 8, 7, 15, 45, 0, DateTimeKind.Utc);

    private readonly SyncApiFixture _fixture;

    public AssistantFlowTests(SyncApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Assistant_SubmittedTurn_IsPublishedTowardWindowsAndNotEchoedBack()
    {
        var (phone, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var session = await CreateSessionAsync(phoneClient);
        var phoneBaseline = await SyncApiFixture.GetCursorAsync(phoneClient);
        var windowsBaseline = await SyncApiFixture.GetCursorAsync(windowsClient);

        var turnId = Guid.NewGuid().ToString("D");
        var submitted = await SubmitTurnAsync(phoneClient, session.SessionId, new AssistantTurnCreateRequest
        {
            TurnId = turnId,
            Question = "Where did we stop for lunch on the Utah trip?",
            ClientContext =
            [
                new AssistantContextItem
                {
                    EventId = Guid.NewGuid().ToString("D"),
                    Title = "Drive through Moab",
                    EventDateUtc = new DateTime(2024, 6, 2, 0, 0, 0, DateTimeKind.Utc),
                    Excerpt = "We pulled off near the river.",
                    Score = 0.71,
                },
            ],
        });

        // The turn is accepted and durable immediately; nothing has answered it.
        submitted.TurnId.Should().Be(turnId);
        submitted.SessionId.Should().Be(session.SessionId);
        submitted.Sequence.Should().Be(1);
        submitted.Status.Should().Be(AssistantTurnStatus.Pending);
        submitted.RequestedResponder.Should().Be(AssistantResponder.Windows);
        submitted.ActualResponder.Should().BeNull("nothing has claimed the turn yet");
        submitted.Result.Should().BeNull();

        // Windows picks it up on its normal pull — no second transport.
        var windowsPull = await SyncApiFixture.PullAsync(windowsClient, windowsBaseline);
        var change = windowsPull.Changes.Should()
            .ContainSingle(c => c.EntityType == SyncChangeEntityType.AssistantTurn).Which;
        change.EntityId.Should().Be(turnId);
        change.Operation.Should().Be(SyncOperation.Upsert);
        change.Revision.Should().Be(1);
        change.SourceDeviceId.Should().Be(phone.DeviceId);

        var payload = TurnPayload(change);
        payload.TurnId.Should().Be(turnId);
        payload.SessionId.Should().Be(session.SessionId);
        payload.Sequence.Should().Be(1);
        payload.Question.Should().Be("Where did we stop for lunch on the Utah trip?");
        payload.Status.Should().Be(AssistantTurnStatus.Pending);
        payload.RequestedResponder.Should().Be(AssistantResponder.Windows);
        payload.OriginDeviceId.Should().Be(phone.DeviceId, "Windows has to know which device to scope retrieval to");
        payload.Result.Should().BeNull();

        // Client context rides along whole — a responder sees all of it or none.
        var context = payload.ClientContext.Should().ContainSingle().Which;
        context.Title.Should().Be("Drive through Moab");
        context.Excerpt.Should().Be("We pulled off near the river.");
        context.Score.Should().Be(0.71);

        // The asking phone does not pull back its own question.
        var phonePull = await SyncApiFixture.PullAsync(phoneClient, phoneBaseline);
        phonePull.Changes.Should().NotContain(c => c.EntityType == SyncChangeEntityType.AssistantTurn);

        // The session's bookkeeping moved with the turn.
        var refreshed = await phoneClient.GetFromJsonAsync<AssistantSessionResponse>(
            $"/api/v1/assistant/sessions/{session.SessionId}", SyncApiFixture.Json);
        refreshed!.TurnCount.Should().Be(1);
    }

    [Fact]
    public async Task Assistant_AnswerPushedFromWindows_ReachesThePollingClient()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (windows, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var session = await CreateSessionAsync(phoneClient);
        var phoneBaseline = await SyncApiFixture.GetCursorAsync(phoneClient);
        var turn = await SubmitTurnAsync(phoneClient, session.SessionId, NewTurnRequest());

        var citedEventId = Guid.NewGuid().ToString("D");
        var push = await SyncApiFixture.PushAsync(
            windowsClient,
            AnswerEntry(1, turn, AssistantTurnStatus.Completed, payload =>
            {
                payload.ActualResponder = AssistantResponder.Windows;
                payload.Result = new AssistantTurnResult
                {
                    Answer = "You stopped at a diner outside Green River.",
                    Grounding = AssistantGrounding.Archive,
                    Model = "local-archive-1",
                    ElapsedMs = 1420,
                    Citations =
                    [
                        new AssistantCitation
                        {
                            EventId = citedEventId,
                            Title = "Lunch outside Green River",
                            DisplayDate = "Summer 2024",
                            Excerpt = "Diner with the neon sign.",
                        },
                    ],
                };
            }));
        var result = push.Results.Should().ContainSingle().Which;
        result.Accepted.Should().BeTrue();
        result.Duplicate.Should().BeFalse();
        result.ServerChangeId.Should().NotBeNull();

        // The phone polls the turn and has the answer, without having had to be
        // awake for the change that carried it.
        var answered = await GetTurnAsync(phoneClient, turn.TurnId);
        answered.Status.Should().Be(AssistantTurnStatus.Completed);
        answered.RequestedResponder.Should().Be(AssistantResponder.Windows);
        answered.ActualResponder.Should().Be(AssistantResponder.Windows);
        answered.CompletedAtUtc.Should().NotBeNull();
        answered.Result!.Answer.Should().Be("You stopped at a diner outside Green River.");
        answered.Result.Grounding.Should().Be(AssistantGrounding.Archive);
        answered.Result.Model.Should().Be("local-archive-1");
        answered.Result.ElapsedMs.Should().Be(1420);
        var citation = answered.Result.Citations.Should().ContainSingle().Which;
        citation.EventId.Should().Be(citedEventId);
        citation.DisplayDate.Should().Be("Summer 2024");
        citation.Excerpt.Should().Be("Diner with the neon sign.");

        // ...and the same answer is on the feed for a client that was awake.
        var phonePull = await SyncApiFixture.PullAsync(phoneClient, phoneBaseline);
        var change = TurnChanges(phonePull, turn.TurnId).Should().ContainSingle().Which;
        change.Revision.Should().Be(2, "the submission was revision 1");
        change.SourceDeviceId.Should().Be(windows.DeviceId);

        // The published payload is the stored turn re-serialized, so it carries
        // the original question even though the publisher's entry omitted it.
        var payload = TurnPayload(change);
        payload.Question.Should().Be(NewTurnRequest().Question);
        payload.Status.Should().Be(AssistantTurnStatus.Completed);
        payload.ActualResponder.Should().Be(AssistantResponder.Windows);
        payload.UpdatedAtUtc.Should().Be(AnsweredAt);
        payload.Result!.Citations.Should().ContainSingle().Which.EventId.Should().Be(citedEventId);
    }

    [Fact]
    public async Task Assistant_TurnWithNoResponder_FallsBackToTheSessionPreference()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var session = await CreateSessionAsync(phoneClient, AssistantResponder.OnDevice);
        session.PreferredResponder.Should().Be(AssistantResponder.OnDevice);

        // No responder on the turn: it inherits the session's preference...
        var inherited = await SubmitTurnAsync(phoneClient, session.SessionId, NewTurnRequest());
        inherited.RequestedResponder.Should().Be(AssistantResponder.OnDevice);
        // ...which nothing is deployed to answer, so it fails at once rather
        // than waiting a day for the reaper (see the dedicated test below).
        inherited.Status.Should().Be(AssistantTurnStatus.Failed);
        inherited.FailureRetryable.Should().BeFalse();

        // ...and a turn may still override it mid-conversation, which is the
        // whole point of choosing per turn rather than per session.
        var overridden = await SubmitTurnAsync(phoneClient, session.SessionId, NewTurnRequest(r =>
            r.Responder = AssistantResponder.Windows));
        overridden.RequestedResponder.Should().Be(AssistantResponder.Windows);
        overridden.Status.Should().Be(AssistantTurnStatus.Pending);
        overridden.Sequence.Should().Be(2);

        // Both responders are recorded when the answer names a different one
        // from the one asked for — a fallback the client needs to see, because
        // it means the answer is less grounded than requested.
        await SyncApiFixture.PushAsync(
            windowsClient,
            AnswerEntry(1, overridden, AssistantTurnStatus.Completed, payload =>
            {
                payload.ActualResponder = AssistantResponder.Provider;
                payload.Result = new AssistantTurnResult
                {
                    Answer = "I could not reach your archive, so this is general knowledge.",
                    Grounding = AssistantGrounding.None,
                };
            }));

        var answered = await GetTurnAsync(phoneClient, overridden.TurnId);
        answered.RequestedResponder.Should().Be(AssistantResponder.Windows);
        answered.ActualResponder.Should().Be(AssistantResponder.Provider);
        answered.Result!.Grounding.Should().Be(AssistantGrounding.None);
    }

    [Fact]
    public async Task Assistant_ResubmittedTurnId_ReturnsTheStoredTurnAndAsksOnce()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var session = await CreateSessionAsync(phoneClient);
        var windowsBaseline = await SyncApiFixture.GetCursorAsync(windowsClient);

        var request = NewTurnRequest();
        var first = await phoneClient.PostAsJsonAsync(
            $"/api/v1/assistant/sessions/{session.SessionId}/turns", request, SyncApiFixture.Json);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        // The retry after a lost response returns 200 with the stored turn, not
        // 201 and not a conflict: asking an assistant twice is not merely
        // wasteful, it answers a question the user asked once.
        var second = await phoneClient.PostAsJsonAsync(
            $"/api/v1/assistant/sessions/{session.SessionId}/turns", request, SyncApiFixture.Json);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var replayed = await second.Content.ReadFromJsonAsync<AssistantTurnResponse>(SyncApiFixture.Json);
        replayed!.TurnId.Should().Be(request.TurnId);
        replayed.Sequence.Should().Be(1, "the replay must not consume a second position in the conversation");

        // Only one question reached Windows.
        var pull = await SyncApiFixture.PullAsync(windowsClient, windowsBaseline);
        TurnChanges(pull, request.TurnId).Should().ContainSingle();

        var refreshed = await phoneClient.GetFromJsonAsync<AssistantSessionResponse>(
            $"/api/v1/assistant/sessions/{session.SessionId}", SyncApiFixture.Json);
        refreshed!.TurnCount.Should().Be(1);
    }

    [Fact]
    public async Task Assistant_ResubmittedAfterAnAnswer_ReturnsTheAnswerRatherThanReAsking()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var session = await CreateSessionAsync(phoneClient);

        var request = NewTurnRequest();
        var turn = await SubmitTurnAsync(phoneClient, session.SessionId, request);
        await SyncApiFixture.PushAsync(
            windowsClient,
            AnswerEntry(1, turn, AssistantTurnStatus.Completed, payload =>
            {
                payload.ActualResponder = AssistantResponder.Windows;
                payload.Result = new AssistantTurnResult
                {
                    Answer = "Green River.",
                    Grounding = AssistantGrounding.Archive,
                };
            }));

        var response = await phoneClient.PostAsJsonAsync(
            $"/api/v1/assistant/sessions/{session.SessionId}/turns", request, SyncApiFixture.Json);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // The replay hands back the answer the turn already has rather than
        // resetting it to pending and asking Windows again.
        var replayed = await response.Content.ReadFromJsonAsync<AssistantTurnResponse>(SyncApiFixture.Json);
        replayed!.Status.Should().Be(AssistantTurnStatus.Completed);
        replayed.Result!.Answer.Should().Be("Green River.");
    }

    [Fact]
    public async Task Assistant_SessionOfAnotherDevice_IsInvisibleToIt()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, otherPhoneClient) = await _fixture.RegisterClientAsync("ios", "iPad");
        var session = await CreateSessionAsync(phoneClient);
        var turn = await SubmitTurnAsync(phoneClient, session.SessionId, NewTurnRequest());

        // Same owner, different device. Reporting "not found" rather than
        // "forbidden" keeps the response from confirming that the identifier is
        // real, and above all keeps the question text out of it.
        var readSession = await otherPhoneClient.GetAsync($"/api/v1/assistant/sessions/{session.SessionId}");
        readSession.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SyncApiFixture.ReadApiErrorAsync(readSession)).Code
            .Should().Be("assistant_session_not_found");

        var readTurn = await otherPhoneClient.GetAsync($"/api/v1/assistant/turns/{turn.TurnId}");
        readTurn.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SyncApiFixture.ReadApiErrorAsync(readTurn)).Code.Should().Be("assistant_turn_not_found");
        (await readTurn.Content.ReadAsStringAsync()).Should().NotContain(
            "Green River", "a denied read must not leak the question it refused to return");

        // Nor may it add to the conversation or end it.
        var submit = await otherPhoneClient.PostAsJsonAsync(
            $"/api/v1/assistant/sessions/{session.SessionId}/turns", NewTurnRequest(), SyncApiFixture.Json);
        submit.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var cancel = await otherPhoneClient.PostAsJsonAsync(
            $"/api/v1/assistant/turns/{turn.TurnId}/cancel", new { }, SyncApiFixture.Json);
        cancel.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The owning device is unaffected by any of it.
        var owned = await GetTurnAsync(phoneClient, turn.TurnId);
        owned.Status.Should().Be(AssistantTurnStatus.Pending);
    }

    [Fact]
    public async Task Assistant_TurnForAnUndeployedResponder_FailsAtOnceAndIsNeverPublished()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var session = await CreateSessionAsync(phoneClient);
        var windowsBaseline = await SyncApiFixture.GetCursorAsync(windowsClient);

        // The vocabulary admits provider and on_device, but nothing on this
        // service answers for either: no LLM provider is configured and no
        // device pre-processes locally. Accepting the turn as pending would
        // leave the asker staring at a spinner until the reaper cancels it a
        // day later; failing it now tells them immediately, and tells them
        // which mode would work.
        foreach (var mode in new[] { AssistantResponder.Provider, AssistantResponder.OnDevice })
        {
            var turn = await SubmitTurnAsync(phoneClient, session.SessionId, NewTurnRequest(r => r.Responder = mode));
            turn.RequestedResponder.Should().Be(mode);
            turn.Status.Should().Be(AssistantTurnStatus.Failed);
            turn.FailureRetryable.Should().BeFalse("retrying the same mode would fail the same way");
            turn.FailureReason.Should().Be(
                $"No responder for mode {mode} is deployed on this sync service; ask again with the windows responder.");
            turn.ActualResponder.Should().BeNull();
            turn.Result.Should().BeNull();
            turn.CompletedAtUtc.Should().NotBeNull("failed is terminal");

            // Polling agrees, and the terminal state holds: cancelling is a no-op.
            (await GetTurnAsync(phoneClient, turn.TurnId)).Status.Should().Be(AssistantTurnStatus.Failed);
            (await CancelTurnAsync(phoneClient, turn.TurnId)).Status.Should().Be(AssistantTurnStatus.Failed);

            // A resubmission replays the stored failure rather than asking again.
            var replay = await phoneClient.PostAsJsonAsync(
                $"/api/v1/assistant/sessions/{session.SessionId}/turns",
                new AssistantTurnCreateRequest { TurnId = turn.TurnId, Question = "again?", Responder = mode },
                SyncApiFixture.Json);
            replay.StatusCode.Should().Be(HttpStatusCode.OK);
            (await replay.Content.ReadFromJsonAsync<AssistantTurnResponse>(SyncApiFixture.Json))!.Status
                .Should().Be(AssistantTurnStatus.Failed);
        }

        // Nothing rode the feed toward Windows: there is nothing to answer.
        var windowsPull = await SyncApiFixture.PullAsync(windowsClient, windowsBaseline);
        windowsPull.Changes.Should().NotContain(c => c.EntityType == SyncChangeEntityType.AssistantTurn);

        // The sequence was consumed all the same, so a later Windows turn is
        // ordered after the failed ones and proceeds as before.
        var refreshed = await phoneClient.GetFromJsonAsync<AssistantSessionResponse>(
            $"/api/v1/assistant/sessions/{session.SessionId}", SyncApiFixture.Json);
        refreshed!.TurnCount.Should().Be(2);
        var windowsTurn = await SubmitTurnAsync(phoneClient, session.SessionId, NewTurnRequest());
        windowsTurn.Sequence.Should().Be(3);
        windowsTurn.Status.Should().Be(AssistantTurnStatus.Pending);
        (await SyncApiFixture.PullAsync(windowsClient, windowsBaseline)).Changes
            .Should().ContainSingle(c => c.EntityType == SyncChangeEntityType.AssistantTurn)
            .Which.EntityId.Should().Be(windowsTurn.TurnId);
    }

    [Fact]
    public async Task Assistant_UnknownResponder_IsRejectedLikeAnUnknownPlatform()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");

        // The shape a client already handles for an unknown device platform.
        using var unauthenticated = _fixture.CreateClient();
        var badPlatform = await unauthenticated.PostAsJsonAsync(
            "/api/v1/devices/register",
            new DeviceRegisterRequest
            {
                PairingCode = SyncApiFixture.PairingCode,
                Platform = "toaster",
                DisplayName = "Toaster",
            },
            SyncApiFixture.Json);
        badPlatform.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var platformError = await SyncApiFixture.ReadApiErrorAsync(badPlatform);

        var badSession = await phoneClient.PostAsJsonAsync(
            "/api/v1/assistant/sessions",
            new AssistantSessionCreateRequest { PreferredResponder = "cloud_brain" },
            SyncApiFixture.Json);
        badSession.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var sessionError = await SyncApiFixture.ReadApiErrorAsync(badSession);
        sessionError.Code.Should().Be(platformError.Code);
        sessionError.Retryable.Should().Be(platformError.Retryable);
        sessionError.Message.Should().Contain("windows, provider, on_device");

        // Same on a turn: an unknown responder is refused rather than quietly
        // becoming the default, so a client built against a newer vocabulary
        // learns this service cannot route its turns.
        var session = await CreateSessionAsync(phoneClient);
        var badTurn = await phoneClient.PostAsJsonAsync(
            $"/api/v1/assistant/sessions/{session.SessionId}/turns",
            NewTurnRequest(r => r.Responder = "cloud_brain"),
            SyncApiFixture.Json);
        badTurn.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SyncApiFixture.ReadApiErrorAsync(badTurn)).Code.Should().Be(platformError.Code);

        var refreshed = await phoneClient.GetFromJsonAsync<AssistantSessionResponse>(
            $"/api/v1/assistant/sessions/{session.SessionId}", SyncApiFixture.Json);
        refreshed!.TurnCount.Should().Be(0, "a rejected turn must not consume a sequence number");
    }

    [Fact]
    public async Task Assistant_OversizedClientContext_IsRejectedNotTruncated()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var session = await CreateSessionAsync(phoneClient);
        var windowsBaseline = await SyncApiFixture.GetCursorAsync(windowsClient);

        // One excerpt over the per-item bound.
        var overLongExcerpt = NewTurnRequest(r => r.ClientContext =
        [
            ContextItem(new string('c', AssistantLimits.ClientContextExcerptMaxChars + 1)),
        ]);
        var excerptResponse = await phoneClient.PostAsJsonAsync(
            $"/api/v1/assistant/sessions/{session.SessionId}/turns", overLongExcerpt, SyncApiFixture.Json);
        excerptResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SyncApiFixture.ReadApiErrorAsync(excerptResponse)).Code.Should().Be("validation_error");

        // Too many items, each individually legal.
        var tooManyItems = NewTurnRequest(r => r.ClientContext = Enumerable
            .Range(0, AssistantLimits.ClientContextMaxItems + 1)
            .Select(_ => ContextItem("short"))
            .ToList());
        var itemsResponse = await phoneClient.PostAsJsonAsync(
            $"/api/v1/assistant/sessions/{session.SessionId}/turns", tooManyItems, SyncApiFixture.Json);
        itemsResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Within both per-item bounds, over the aggregate one — the case a
        // per-item-only check would let through.
        var perItemChars = AssistantLimits.ClientContextMaxTotalChars / AssistantLimits.ClientContextMaxItems;
        var overTotal = NewTurnRequest(r => r.ClientContext = Enumerable
            .Range(0, AssistantLimits.ClientContextMaxItems)
            .Select(_ => ContextItem(new string('c', perItemChars)))
            .ToList());
        var totalResponse = await phoneClient.PostAsJsonAsync(
            $"/api/v1/assistant/sessions/{session.SessionId}/turns", overTotal, SyncApiFixture.Json);
        totalResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SyncApiFixture.ReadApiErrorAsync(totalResponse)).Message.Should().Contain("in total");

        // A question over its own bound is refused the same way.
        var overLongQuestion = await phoneClient.PostAsJsonAsync(
            $"/api/v1/assistant/sessions/{session.SessionId}/turns",
            NewTurnRequest(r => r.Question = new string('q', AssistantLimits.QuestionMaxChars + 1)),
            SyncApiFixture.Json);
        overLongQuestion.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Nothing was truncated and dispatched behind the client's back: no
        // turn exists and Windows was never asked.
        var pull = await SyncApiFixture.PullAsync(windowsClient, windowsBaseline);
        pull.Changes.Should().NotContain(c => c.EntityType == SyncChangeEntityType.AssistantTurn);

        var missing = await phoneClient.GetAsync($"/api/v1/assistant/turns/{overLongExcerpt.TurnId}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Assistant_ContextAtExactlyTheBound_IsAccepted()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var session = await CreateSessionAsync(phoneClient);

        // The bound is inclusive on both the item excerpt and the question, so
        // a client that measured correctly is not refused for being exact.
        var turn = await SubmitTurnAsync(phoneClient, session.SessionId, NewTurnRequest(r =>
        {
            r.Question = new string('q', AssistantLimits.QuestionMaxChars);
            r.ClientContext = [ContextItem(new string('c', AssistantLimits.ClientContextExcerptMaxChars))];
        }));
        turn.Status.Should().Be(AssistantTurnStatus.Pending);
        turn.Question.Should().HaveLength(AssistantLimits.QuestionMaxChars);
    }

    [Fact]
    public async Task Assistant_CancelledTurn_StaysCancelledAgainstALateAnswer()
    {
        var (phone, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var session = await CreateSessionAsync(phoneClient);
        var phoneBaseline = await SyncApiFixture.GetCursorAsync(phoneClient);
        var windowsBaseline = await SyncApiFixture.GetCursorAsync(windowsClient);
        var turn = await SubmitTurnAsync(phoneClient, session.SessionId, NewTurnRequest());

        var cancelled = await CancelTurnAsync(phoneClient, turn.TurnId);
        cancelled.Status.Should().Be(AssistantTurnStatus.Cancelled);
        cancelled.CompletedAtUtc.Should().NotBeNull();

        // Windows is told to stop, on the same feed it was asked on.
        var windowsPull = await SyncApiFixture.PullAsync(windowsClient, windowsBaseline);
        var cancelChange = TurnChanges(windowsPull, turn.TurnId).Should().HaveCount(2).And.Subject.Last();
        cancelChange.Revision.Should().Be(2);
        cancelChange.SourceDeviceId.Should().Be(phone.DeviceId);
        TurnPayload(cancelChange).Status.Should().Be(AssistantTurnStatus.Cancelled);

        // ...but Windows had already finished, and pushes the answer anyway.
        // The entry is consumed so the outbox stops retrying it, and discarded
        // so the cancel button means stop rather than "stop if you're quick".
        var late = await SyncApiFixture.PushAsync(
            windowsClient,
            AnswerEntry(1, turn, AssistantTurnStatus.Completed, payload =>
            {
                payload.ActualResponder = AssistantResponder.Windows;
                payload.Result = new AssistantTurnResult
                {
                    Answer = "You stopped at a diner outside Green River.",
                    Grounding = AssistantGrounding.Archive,
                };
            }));
        var lateResult = late.Results.Should().ContainSingle().Which;
        lateResult.Accepted.Should().BeTrue("a discarded answer must not be re-pushed forever");
        lateResult.ServerChangeId.Should().BeNull();

        // A chunk that was already in flight is dropped for the same reason.
        var chunk = await SyncApiFixture.PushAsync(windowsClient, ChunkEntry(2, turn.TurnId, 0, "You stopped at"));
        chunk.Results.Should().ContainSingle().Which.ServerChangeId.Should().BeNull();

        var polled = await GetTurnAsync(phoneClient, turn.TurnId);
        polled.Status.Should().Be(AssistantTurnStatus.Cancelled);
        polled.Result.Should().BeNull("a cancelled turn must never acquire an answer");
        polled.ActualResponder.Should().BeNull();

        // The phone's feed carries the cancellation and nothing after it.
        var phonePull = await SyncApiFixture.PullAsync(phoneClient, phoneBaseline);
        var phoneChanges = TurnChanges(phonePull, turn.TurnId);
        phoneChanges.Should().BeEmpty("the phone published both revisions itself");

        // Cancelling again is a no-op that appends nothing.
        var again = await CancelTurnAsync(phoneClient, turn.TurnId);
        again.Status.Should().Be(AssistantTurnStatus.Cancelled);
        var afterSecondCancel = await SyncApiFixture.PullAsync(windowsClient, windowsBaseline);
        TurnChanges(afterSecondCancel, turn.TurnId).Should().HaveCount(2);
    }

    [Fact]
    public async Task Assistant_CancelAfterCompletion_DoesNotEraseTheAnswer()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var session = await CreateSessionAsync(phoneClient);
        var turn = await SubmitTurnAsync(phoneClient, session.SessionId, NewTurnRequest());

        await SyncApiFixture.PushAsync(
            windowsClient,
            AnswerEntry(1, turn, AssistantTurnStatus.Completed, payload =>
            {
                payload.ActualResponder = AssistantResponder.Windows;
                payload.Result = new AssistantTurnResult
                {
                    Answer = "Green River.",
                    Grounding = AssistantGrounding.Archive,
                };
            }));

        // The user hit stop just after the answer landed. Completed is terminal
        // too, so the cancel is a no-op: erasing an answer the user may already
        // have heard would be a stranger outcome than ignoring a late stop.
        var cancelled = await CancelTurnAsync(phoneClient, turn.TurnId);
        cancelled.Status.Should().Be(AssistantTurnStatus.Completed);
        cancelled.Result!.Answer.Should().Be("Green River.");
    }

    [Fact]
    public async Task Assistant_StreamedChunks_RideTheFeedAheadOfTheCompletedTurn()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var session = await CreateSessionAsync(phoneClient);
        var phoneBaseline = await SyncApiFixture.GetCursorAsync(phoneClient);
        var turn = await SubmitTurnAsync(phoneClient, session.SessionId, NewTurnRequest(r => r.Stream = true));

        var push = await SyncApiFixture.PushAsync(
            windowsClient,
            ChunkEntry(1, turn.TurnId, 0, "You stopped "),
            ChunkEntry(2, turn.TurnId, 1, "at a diner.", isFinal: true),
            AnswerEntry(3, turn, AssistantTurnStatus.Completed, payload =>
            {
                payload.ActualResponder = AssistantResponder.Windows;
                payload.Result = new AssistantTurnResult
                {
                    Answer = "You stopped at a diner.",
                    Grounding = AssistantGrounding.Archive,
                };
            }));
        push.Results.Should().OnlyContain(r => r.Accepted && !r.Duplicate);

        var pull = await SyncApiFixture.PullAsync(phoneClient, phoneBaseline);
        var chunks = pull.Changes
            .Where(c => c.EntityType == SyncChangeEntityType.AssistantTurnChunk && c.EntityId == turn.TurnId)
            .OrderBy(c => c.ChangeId)
            .ToList();
        chunks.Should().HaveCount(2);
        // The revision carries the chunk index, so a consumer spots a gap
        // without having to parse the payload.
        chunks.Select(c => c.Revision).Should().Equal(1, 2);

        var deltas = chunks
            .Select(c => JsonSerializer.Deserialize<AssistantTurnChunkPayload>(c.PayloadJson!, SyncApiFixture.Json)!)
            .ToList();
        deltas.Select(d => d.Delta).Should().Equal("You stopped ", "at a diner.");
        deltas[^1].IsFinal.Should().BeTrue();

        // The authoritative answer still arrives as the completed turn, so a
        // client that missed a chunk is never left assembling a partial one.
        TurnPayload(TurnChanges(pull, turn.TurnId).Should().ContainSingle().Which)
            .Result!.Answer.Should().Be("You stopped at a diner.");
    }

    [Fact]
    public async Task Assistant_MalformedAnswerPush_IsRejectedAndLeavesTheTurnPending()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var session = await CreateSessionAsync(phoneClient);
        var turn = await SubmitTurnAsync(phoneClient, session.SessionId, NewTurnRequest());

        var unknownStatus = AnswerEntry(1, turn, "thinking_hard");
        var mismatchedId = AnswerEntry(2, turn, AssistantTurnStatus.Answering);
        mismatchedId.EntityId = Guid.NewGuid().ToString("D");
        var unparseable = AnswerEntry(3, turn, AssistantTurnStatus.Answering);
        unparseable.PayloadJson = "{ not json";
        var completedWithoutAnswer = AnswerEntry(4, turn, AssistantTurnStatus.Completed);
        var overLongAnswer = AnswerEntry(5, turn, AssistantTurnStatus.Completed, payload =>
            payload.Result = new AssistantTurnResult
            {
                Answer = new string('a', AssistantLimits.AnswerMaxChars + 1),
                Grounding = AssistantGrounding.Archive,
            });
        var unknownResponder = AnswerEntry(6, turn, AssistantTurnStatus.Answering, payload =>
            payload.ActualResponder = "cloud_brain");

        var push = await SyncApiFixture.PushAsync(
            windowsClient,
            unknownStatus, mismatchedId, unparseable, completedWithoutAnswer, overLongAnswer, unknownResponder);
        push.Results.Should().HaveCount(6);
        push.Results.Should().OnlyContain(r => !r.Accepted && r.Error!.StartsWith("validation_error"));

        // An answer for a turn that does not exist is not a validation problem
        // but a missing target, and says so.
        var unknownTurn = AnswerEntry(7, turn, AssistantTurnStatus.Answering);
        unknownTurn.EntityId = Guid.NewGuid().ToString("D");
        unknownTurn.PayloadJson = JsonSerializer.Serialize(
            new AssistantTurnChangePayload
            {
                TurnId = unknownTurn.EntityId,
                SessionId = session.SessionId,
                Status = AssistantTurnStatus.Answering,
                UpdatedAtUtc = AnsweredAt,
            },
            SyncApiFixture.Json);
        var missing = await SyncApiFixture.PushAsync(windowsClient, unknownTurn);
        missing.Results.Should().ContainSingle().Which.Error
            .Should().StartWith("assistant_turn_not_found");

        var polled = await GetTurnAsync(phoneClient, turn.TurnId);
        polled.Status.Should().Be(AssistantTurnStatus.Pending);
        polled.Result.Should().BeNull();
    }

    [Fact]
    public async Task Assistant_FailedAnswer_ReachesTheClientAsATerminalFailure()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var session = await CreateSessionAsync(phoneClient);
        var turn = await SubmitTurnAsync(phoneClient, session.SessionId, NewTurnRequest());

        // A responder may also claim a turn before answering it; the client
        // sees the progression rather than a silent gap.
        await SyncApiFixture.PushAsync(
            windowsClient,
            AnswerEntry(1, turn, AssistantTurnStatus.Answering, payload =>
                payload.ActualResponder = AssistantResponder.Windows));
        (await GetTurnAsync(phoneClient, turn.TurnId)).Status.Should().Be(AssistantTurnStatus.Answering);

        await SyncApiFixture.PushAsync(
            windowsClient,
            AnswerEntry(2, turn, AssistantTurnStatus.Failed, payload =>
            {
                payload.ActualResponder = AssistantResponder.Windows;
                payload.FailureReason = "The archive index is rebuilding; try again shortly.";
                payload.FailureRetryable = true;
            }));

        var failed = await GetTurnAsync(phoneClient, turn.TurnId);
        failed.Status.Should().Be(AssistantTurnStatus.Failed);
        failed.FailureReason.Should().Be("The archive index is rebuilding; try again shortly.");
        failed.FailureRetryable.Should().BeTrue();
        failed.CompletedAtUtc.Should().NotBeNull();
        failed.Result.Should().BeNull();

        // Failed is terminal too: a later answer for it is discarded.
        var late = await SyncApiFixture.PushAsync(
            windowsClient,
            AnswerEntry(3, turn, AssistantTurnStatus.Completed, payload =>
                payload.Result = new AssistantTurnResult
                {
                    Answer = "Green River.",
                    Grounding = AssistantGrounding.Archive,
                }));
        late.Results.Should().ContainSingle().Which.ServerChangeId.Should().BeNull();
        (await GetTurnAsync(phoneClient, turn.TurnId)).Status.Should().Be(AssistantTurnStatus.Failed);
    }

    [Fact]
    public async Task Assistant_UnauthenticatedCaller_CannotReachAnyAssistantRoute()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var session = await CreateSessionAsync(phoneClient);
        var turn = await SubmitTurnAsync(phoneClient, session.SessionId, NewTurnRequest());

        using var anonymous = _fixture.CreateClient();
        var routes = new[]
        {
            await anonymous.PostAsJsonAsync(
                "/api/v1/assistant/sessions", new AssistantSessionCreateRequest(), SyncApiFixture.Json),
            await anonymous.GetAsync($"/api/v1/assistant/sessions/{session.SessionId}"),
            await anonymous.PostAsJsonAsync(
                $"/api/v1/assistant/sessions/{session.SessionId}/turns", NewTurnRequest(), SyncApiFixture.Json),
            await anonymous.GetAsync($"/api/v1/assistant/turns/{turn.TurnId}"),
            await anonymous.PostAsJsonAsync(
                $"/api/v1/assistant/turns/{turn.TurnId}/cancel", new { }, SyncApiFixture.Json),
        };

        routes.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Unauthorized);
        foreach (var route in routes)
        {
            (await SyncApiFixture.ReadApiErrorAsync(route)).Code.Should().Be("unauthorized");
            route.Dispose();
        }
    }

    /// <summary>Opens a session for the calling device; throws unless the server replies 201.</summary>
    private static async Task<AssistantSessionResponse> CreateSessionAsync(
        HttpClient client, string preferredResponder = AssistantResponder.Windows)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/assistant/sessions",
            new AssistantSessionCreateRequest { PreferredResponder = preferredResponder, Surface = "carplay" },
            SyncApiFixture.Json);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException(
                $"Assistant session create failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<AssistantSessionResponse>(SyncApiFixture.Json)
            ?? throw new InvalidOperationException("Assistant session create returned no body.");
    }

    /// <summary>Submits a turn; throws unless the server accepts it as new (201).</summary>
    private static async Task<AssistantTurnResponse> SubmitTurnAsync(
        HttpClient client, string sessionId, AssistantTurnCreateRequest request)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/assistant/sessions/{sessionId}/turns", request, SyncApiFixture.Json);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException(
                $"Assistant turn submit failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<AssistantTurnResponse>(SyncApiFixture.Json)
            ?? throw new InvalidOperationException("Assistant turn submit returned no body.");
    }

    private static async Task<AssistantTurnResponse> GetTurnAsync(HttpClient client, string turnId)
    {
        var response = await client.GetAsync($"/api/v1/assistant/turns/{turnId}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AssistantTurnResponse>(SyncApiFixture.Json)
            ?? throw new InvalidOperationException("Assistant turn get returned no body.");
    }

    private static async Task<AssistantTurnResponse> CancelTurnAsync(HttpClient client, string turnId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/assistant/turns/{turnId}/cancel", new { }, SyncApiFixture.Json);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AssistantTurnResponse>(SyncApiFixture.Json)
            ?? throw new InvalidOperationException("Assistant turn cancel returned no body.");
    }

    /// <summary>A valid turn submission with a fresh turn ID.</summary>
    private static AssistantTurnCreateRequest NewTurnRequest(Action<AssistantTurnCreateRequest>? customize = null)
    {
        var request = new AssistantTurnCreateRequest
        {
            TurnId = Guid.NewGuid().ToString("D"),
            Question = "Where did we stop for lunch on the Utah trip?",
        };
        customize?.Invoke(request);
        return request;
    }

    private static AssistantContextItem ContextItem(string excerpt) => new()
    {
        EventId = Guid.NewGuid().ToString("D"),
        Title = "Drive through Moab",
        Excerpt = excerpt,
    };

    /// <summary>
    /// Builds an assistant_turn push entry the way a responder's outbox does:
    /// entityId equal to the payload's turnId, upsert operation, camelCase
    /// payload JSON. The question is deliberately left empty — a responder
    /// echoing it back would be pointless, and the service re-serializes the
    /// stored turn anyway.
    /// </summary>
    private static SyncPushEntry AnswerEntry(
        long clientSequence,
        AssistantTurnResponse turn,
        string status,
        Action<AssistantTurnChangePayload>? customize = null)
    {
        var payload = new AssistantTurnChangePayload
        {
            TurnId = turn.TurnId,
            SessionId = turn.SessionId,
            Sequence = turn.Sequence,
            Status = status,
            RequestedResponder = turn.RequestedResponder,
            UpdatedAtUtc = AnsweredAt,
        };
        customize?.Invoke(payload);

        return new SyncPushEntry
        {
            ClientSequence = clientSequence,
            EntityType = SyncChangeEntityType.AssistantTurn,
            EntityId = turn.TurnId,
            Operation = SyncOperation.Upsert,
            PayloadJson = JsonSerializer.Serialize(payload, SyncApiFixture.Json),
            ChangedAtUtc = DateTime.UtcNow,
        };
    }

    private static SyncPushEntry ChunkEntry(
        long clientSequence, string turnId, int chunkIndex, string delta, bool isFinal = false)
    {
        var payload = new AssistantTurnChunkPayload
        {
            TurnId = turnId,
            ChunkIndex = chunkIndex,
            Delta = delta,
            IsFinal = isFinal,
        };

        return new SyncPushEntry
        {
            ClientSequence = clientSequence,
            EntityType = SyncChangeEntityType.AssistantTurnChunk,
            EntityId = turnId,
            Operation = SyncOperation.Upsert,
            PayloadJson = JsonSerializer.Serialize(payload, SyncApiFixture.Json),
            ChangedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>assistant_turn changes for one turn, in cursor order.</summary>
    private static List<SyncChangeDto> TurnChanges(SyncPullResponse pull, string turnId) =>
        pull.Changes
            .Where(c => c.EntityType == SyncChangeEntityType.AssistantTurn && c.EntityId == turnId)
            .OrderBy(c => c.ChangeId)
            .ToList();

    private static AssistantTurnChangePayload TurnPayload(SyncChangeDto change) =>
        JsonSerializer.Deserialize<AssistantTurnChangePayload>(change.PayloadJson!, SyncApiFixture.Json)!;
}
