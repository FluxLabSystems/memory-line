using System.Text.Json;
using MemoryTimeline.SyncApi.Domain;
using MemoryTimeline.SyncContracts;

namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// Mapping between domain entities and the shared wire contracts, plus
/// construction of capture change-log rows. Kept in one place so the capture
/// payload shape stays identical wherever a change is appended.
/// </summary>
internal static class EntityMappers
{
    /// <summary>Maps an artifact row to its wire summary, preferring measured values once complete.</summary>
    public static ArtifactSummary ToSummary(ServerArtifact artifact) => new()
    {
        ArtifactId = artifact.ArtifactId,
        ArtifactType = artifact.ArtifactType,
        MediaType = artifact.MediaType,
        OriginalFileName = artifact.OriginalFileName,
        ByteLength = artifact.ActualByteLength ?? artifact.ExpectedByteLength,
        Sha256 = artifact.ActualSha256 ?? artifact.ExpectedSha256,
        State = artifact.State,
    };

    /// <summary>Maps a capture row plus its artifacts to the wire response.</summary>
    public static CaptureResponse ToCaptureResponse(ServerCapture capture, IEnumerable<ServerArtifact> artifacts) => new()
    {
        CaptureId = capture.CaptureId,
        SourceDeviceId = capture.SourceDeviceId,
        SourcePlatform = capture.SourcePlatform,
        CaptureType = capture.CaptureType,
        CapturedAtUtc = capture.CapturedAtUtc,
        TimezoneId = capture.TimezoneId,
        LocalOffsetMinutes = capture.LocalOffsetMinutes,
        TripId = capture.TripId,
        TitleHint = capture.TitleHint,
        UserNote = capture.UserNote,
        Location = DeserializeLocation(capture.LocationJson),
        Status = capture.Status,
        ServerRevision = capture.Revision,
        CreatedAtUtc = capture.CreatedAtUtc,
        Artifacts = artifacts.Select(ToSummary).ToList(),
    };

    /// <summary>Maps a device row to the wire device info.</summary>
    public static DeviceInfoResponse ToDeviceInfo(Device device) => new()
    {
        DeviceId = device.DeviceId,
        Platform = device.Platform,
        DisplayName = device.DisplayName,
        LastSeenAtUtc = device.LastSeenAtUtc,
        RevokedAtUtc = device.RevokedAtUtc,
    };

    /// <summary>
    /// Builds a capture upsert change-log row carrying a CaptureChangePayload
    /// (camelCase JSON). <paramref name="audioArtifact"/> is set only after an
    /// audio artifact completes — that change triggers Windows ingestion.
    /// </summary>
    public static SyncChangeRow CreateCaptureChange(ServerCapture capture, ArtifactSummary? audioArtifact, string sourceDeviceId)
    {
        var payload = new CaptureChangePayload
        {
            CaptureId = capture.CaptureId,
            SourceDeviceId = capture.SourceDeviceId,
            SourcePlatform = capture.SourcePlatform,
            CaptureType = capture.CaptureType,
            CapturedAtUtc = capture.CapturedAtUtc,
            TimezoneId = capture.TimezoneId,
            LocalOffsetMinutes = capture.LocalOffsetMinutes,
            TripId = capture.TripId,
            TitleHint = capture.TitleHint,
            UserNote = capture.UserNote,
            Location = DeserializeLocation(capture.LocationJson),
            ClientSchemaVersion = capture.ClientSchemaVersion,
            Status = capture.Status,
            AudioArtifact = audioArtifact,
        };

        return new SyncChangeRow
        {
            OwnerId = capture.OwnerId,
            EntityType = SyncChangeEntityType.Capture,
            EntityId = capture.CaptureId,
            Operation = SyncOperation.Upsert,
            Revision = capture.Revision,
            SourceDeviceId = sourceDeviceId,
            PayloadJson = JsonSerializer.Serialize(payload, SyncJson.Options),
            ChangedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Serializes the stored status projection as the payload of a
    /// capture_status change (camelCase JSON, design §19 Phase 3). The change
    /// log carries what the server persisted rather than the publisher's raw
    /// bytes, so a pulled payload can never disagree with the stored row.
    /// </summary>
    public static string SerializeCaptureStatusPayload(CaptureStatusRow status)
    {
        var payload = new CaptureStatusChangePayload
        {
            CaptureId = status.CaptureId,
            Status = status.Status,
            ProcessingStage = status.ProcessingStage,
            UpdatedAtUtc = status.UpdatedAtUtc,
            TranscriptAvailable = status.TranscriptAvailable,
            TranscriptPreview = status.TranscriptPreview,
            TranscriptCharCount = status.TranscriptCharCount,
            PendingEventCount = status.PendingEventCount,
            ApprovedEventCount = status.ApprovedEventCount,
            FailureReason = status.FailureReason,
            FailureRetryable = status.FailureRetryable,
        };

        return JsonSerializer.Serialize(payload, SyncJson.Options);
    }

    /// <summary>Maps an assistant session row to the wire response.</summary>
    public static AssistantSessionResponse ToAssistantSessionResponse(AssistantSessionRow session) => new()
    {
        SessionId = session.SessionId,
        DeviceId = session.DeviceId,
        PreferredResponder = session.PreferredResponder,
        CreatedAtUtc = session.CreatedAtUtc,
        LastTurnAtUtc = session.LastTurnAtUtc,
        TurnCount = session.TurnCount,
    };

    /// <summary>
    /// Maps an assistant turn row to the wire response. The result object is
    /// materialized only once an answer exists, so a client can test for its
    /// presence rather than having to inspect an empty answer string.
    /// </summary>
    public static AssistantTurnResponse ToAssistantTurnResponse(AssistantTurnRow turn) => new()
    {
        TurnId = turn.TurnId,
        SessionId = turn.SessionId,
        Sequence = turn.Sequence,
        Question = turn.Question,
        Status = turn.Status,
        RequestedResponder = turn.RequestedResponder,
        ActualResponder = turn.ActualResponder,
        CreatedAtUtc = turn.CreatedAtUtc,
        CompletedAtUtc = turn.CompletedAtUtc,
        Result = ToAssistantTurnResult(turn),
        FailureReason = turn.FailureReason,
        FailureRetryable = turn.FailureRetryable,
    };

    /// <summary>
    /// Builds an assistant_turn upsert change-log row from the stored turn. The
    /// change carries what the service persisted rather than a publisher's raw
    /// bytes, so a payload pulled off the feed can never disagree with the turn
    /// a client polls — the two are the same row, serialized twice.
    ///
    /// <paramref name="sourceDeviceId"/> is the device that caused this
    /// revision, which echo suppression uses to keep the change away from it:
    /// the asking phone does not need its own question back, and a responder
    /// does not need its own answer back. Null when the service itself caused
    /// it (the idle-session reaper cancelling an unanswered turn), so every
    /// device — asker and responder alike — receives it.
    /// </summary>
    public static SyncChangeRow CreateAssistantTurnChange(AssistantTurnRow turn, string? sourceDeviceId) => new()
    {
        OwnerId = turn.OwnerId,
        EntityType = SyncChangeEntityType.AssistantTurn,
        EntityId = turn.TurnId,
        Operation = SyncOperation.Upsert,
        Revision = turn.Revision,
        SourceDeviceId = sourceDeviceId,
        PayloadJson = SerializeAssistantTurnPayload(turn),
        ChangedAtUtc = DateTime.UtcNow,
    };

    /// <summary>Serializes the stored turn as the payload of an assistant_turn change (camelCase JSON).</summary>
    public static string SerializeAssistantTurnPayload(AssistantTurnRow turn)
    {
        var payload = new AssistantTurnChangePayload
        {
            TurnId = turn.TurnId,
            SessionId = turn.SessionId,
            Sequence = turn.Sequence,
            Question = turn.Question,
            Status = turn.Status,
            RequestedResponder = turn.RequestedResponder,
            ActualResponder = turn.ActualResponder,
            OriginDeviceId = turn.OriginDeviceId,
            ClientContext = DeserializeClientContext(turn.ClientContextJson),
            Result = ToAssistantTurnResult(turn),
            FailureReason = turn.FailureReason,
            FailureRetryable = turn.FailureRetryable,
            UpdatedAtUtc = turn.UpdatedAtUtc,
        };

        return JsonSerializer.Serialize(payload, SyncJson.Options);
    }

    /// <summary>
    /// The answer carried by a turn, or null when nothing has answered it yet.
    /// An empty citation list survives as an empty list rather than becoming
    /// null: a responder that found nothing relevant and said so honestly is
    /// materially different from one that has not answered.
    /// </summary>
    private static AssistantTurnResult? ToAssistantTurnResult(AssistantTurnRow turn)
    {
        if (turn.Answer is null)
        {
            return null;
        }

        return new AssistantTurnResult
        {
            Answer = turn.Answer,
            Grounding = turn.Grounding ?? AssistantGrounding.None,
            Citations = DeserializeCitations(turn.CitationsJson),
            Model = turn.Model,
            ElapsedMs = turn.ElapsedMs,
        };
    }

    /// <summary>
    /// Rehydrates stored client context. Written by this service after
    /// validation, so malformed JSON means the row was corrupted rather than
    /// that a client sent something odd; the turn still routes, without the
    /// context, instead of failing the whole pull.
    /// </summary>
    private static List<AssistantContextItem>? DeserializeClientContext(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<AssistantContextItem>>(json, SyncJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rehydrates the capture's location snapshot (contract v1.5). Written by
    /// CaptureService from a validated request, so malformed JSON means the
    /// row was corrupted rather than that a client sent something odd; the
    /// capture still maps, without its snapshot, instead of failing the read
    /// or the whole pull.
    /// </summary>
    private static LocationContext? DeserializeLocation(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LocationContext>(json, SyncJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Rehydrates stored citations; see <see cref="DeserializeClientContext"/> for the failure stance.</summary>
    private static List<AssistantCitation> DeserializeCitations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<AssistantCitation>>(json, SyncJson.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Whether the artifact carries capture audio (its completion triggers ingestion).</summary>
    public static bool IsAudioArtifact(ServerArtifact artifact)
        => artifact.ArtifactType is "audio_original" or "audio_normalized";
}
