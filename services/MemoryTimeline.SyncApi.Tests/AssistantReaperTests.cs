using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MemoryTimeline.SyncApi.Application;
using MemoryTimeline.SyncContracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MemoryTimeline.SyncApi.Tests;

/// <summary>
/// The idle assistant-session reaper against the real host: a session with no
/// accepted turn inside the idle window is deleted with its turns, any turn
/// still waiting on a responder is cancelled on the feed for everyone to see,
/// an active session is untouched, and already-finished turns are not
/// republished. The reaper takes its clock as a parameter, so "a day later"
/// is a value rather than a wait.
/// </summary>
public class AssistantReaperTests : IClassFixture<SyncApiFixture>
{
    private readonly SyncApiFixture _fixture;

    public AssistantReaperTests(SyncApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Reap_IdleSession_DeletesItAndCancelsItsUnansweredTurnForEveryone()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var session = await CreateSessionAsync(phoneClient);
        var turn = await SubmitTurnAsync(phoneClient, session.SessionId, "Where did we camp last June?");
        var phoneBaseline = await SyncApiFixture.GetCursorAsync(phoneClient);
        var windowsBaseline = await SyncApiFixture.GetCursorAsync(windowsClient);

        var reaper = _fixture.Services.GetRequiredService<IAssistantSessionReaper>();

        // Inside the window: nothing happens.
        var early = await reaper.ReapAsync(DateTime.UtcNow.AddHours(23), CancellationToken.None);
        early.SessionsReaped.Should().Be(0);
        (await phoneClient.GetAsync($"/api/v1/assistant/sessions/{session.SessionId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // A day later the conversation is gone.
        var late = await reaper.ReapAsync(DateTime.UtcNow.AddHours(25), CancellationToken.None);
        late.SessionsReaped.Should().Be(1);
        late.TurnsCancelled.Should().Be(1);
        late.TurnsDeleted.Should().Be(1);

        (await phoneClient.GetAsync($"/api/v1/assistant/sessions/{session.SessionId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await phoneClient.GetAsync($"/api/v1/assistant/turns/{turn.TurnId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The cancellation reaches the responder, so a Windows that wakes up
        // later drops the turn instead of answering into a deleted session...
        var windowsPull = await SyncApiFixture.PullAsync(windowsClient, windowsBaseline);
        var change = windowsPull.Changes.Should()
            .ContainSingle(c => c.EntityType == SyncChangeEntityType.AssistantTurn).Which;
        change.EntityId.Should().Be(turn.TurnId);
        change.Revision.Should().Be(2);
        change.SourceDeviceId.Should().BeNull("the reaper is the service, not a device");
        var payload = JsonSerializer.Deserialize<AssistantTurnChangePayload>(change.PayloadJson!, SyncApiFixture.Json)!;
        payload.Status.Should().Be(AssistantTurnStatus.Cancelled);

        // ...and the asking phone, which echo suppression would have hidden a
        // device-sourced change from.
        var phonePull = await SyncApiFixture.PullAsync(phoneClient, phoneBaseline);
        phonePull.Changes.Should().ContainSingle(
            c => c.EntityType == SyncChangeEntityType.AssistantTurn && c.EntityId == turn.TurnId);

        // A late answer is discarded, as for any cancelled turn.
        var push = await SyncApiFixture.PushAsync(windowsClient, AnswerEntry(1, turn.TurnId));
        var result = push.Results.Should().ContainSingle().Which;
        result.Accepted.Should().BeFalse("the turn no longer exists to be answered");
        result.Error.Should().StartWith("assistant_turn_not_found");
    }

    [Fact]
    public async Task Reap_ActiveSession_IsLeftAlone()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var session = await CreateSessionAsync(phoneClient);
        await SubmitTurnAsync(phoneClient, session.SessionId, "What was the name of that diner?");

        var reaper = _fixture.Services.GetRequiredService<IAssistantSessionReaper>();
        var report = await reaper.ReapAsync(DateTime.UtcNow.AddHours(1), CancellationToken.None);

        // Other tests' sessions on the shared host are also younger than a day,
        // so nothing at all is reaped.
        report.SessionsReaped.Should().Be(0);
        (await phoneClient.GetAsync($"/api/v1/assistant/sessions/{session.SessionId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reap_IdleSessionWithAnsweredTurn_DeletesWithoutRepublishing()
    {
        var (_, phoneClient) = await _fixture.RegisterClientAsync("ios", "iPhone");
        var (_, windowsClient) = await _fixture.RegisterClientAsync("windows", "Desktop");
        var session = await CreateSessionAsync(phoneClient);
        var turn = await SubmitTurnAsync(phoneClient, session.SessionId, "How long was the drive to Moab?");
        var push = await SyncApiFixture.PushAsync(windowsClient, AnswerEntry(1, turn.TurnId));
        push.Results.Should().ContainSingle().Which.Accepted.Should().BeTrue();
        var windowsBaseline = await SyncApiFixture.GetCursorAsync(windowsClient);
        var phoneBaseline = await SyncApiFixture.GetCursorAsync(phoneClient);

        var reaper = _fixture.Services.GetRequiredService<IAssistantSessionReaper>();
        var report = await reaper.ReapAsync(DateTime.UtcNow.AddHours(25), CancellationToken.None);
        // Counts are host-wide (other tests' idle sessions age out too), so the
        // "nothing republished" claim is checked on this turn, below.
        report.SessionsReaped.Should().BeGreaterThanOrEqualTo(1);

        (await phoneClient.GetAsync($"/api/v1/assistant/turns/{turn.TurnId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SyncApiFixture.PullAsync(windowsClient, windowsBaseline)).Changes
            .Should().NotContain(c => c.EntityId == turn.TurnId);
        (await SyncApiFixture.PullAsync(phoneClient, phoneBaseline)).Changes
            .Should().NotContain(c => c.EntityId == turn.TurnId);
    }

    private static async Task<AssistantSessionResponse> CreateSessionAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/assistant/sessions", new AssistantSessionCreateRequest { Surface = "test" }, SyncApiFixture.Json);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<AssistantSessionResponse>(SyncApiFixture.Json))!;
    }

    private static async Task<AssistantTurnResponse> SubmitTurnAsync(HttpClient client, string sessionId, string question)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/assistant/sessions/{sessionId}/turns",
            new AssistantTurnCreateRequest { TurnId = Guid.NewGuid().ToString("D"), Question = question },
            SyncApiFixture.Json);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<AssistantTurnResponse>(SyncApiFixture.Json))!;
    }

    /// <summary>A Windows-authored completed answer for the turn, as its outbox would push it.</summary>
    private static SyncPushEntry AnswerEntry(long clientSequence, string turnId)
    {
        var payload = new AssistantTurnChangePayload
        {
            TurnId = turnId,
            Status = AssistantTurnStatus.Completed,
            ActualResponder = AssistantResponder.Windows,
            Result = new AssistantTurnResult { Answer = "About four hours.", Grounding = AssistantGrounding.Archive },
            UpdatedAtUtc = DateTime.UtcNow,
        };
        return new SyncPushEntry
        {
            ClientSequence = clientSequence,
            EntityType = SyncChangeEntityType.AssistantTurn,
            EntityId = turnId,
            Operation = SyncOperation.Upsert,
            PayloadJson = JsonSerializer.Serialize(payload, SyncApiFixture.Json),
            ChangedAtUtc = DateTime.UtcNow,
        };
    }
}
