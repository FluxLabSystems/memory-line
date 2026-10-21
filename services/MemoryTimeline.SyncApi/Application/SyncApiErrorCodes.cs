namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// Stable machine-readable error codes carried in ApiError.Code
/// (shared-contracts OpenAPI Error schema; design §16.2 failure classes).
/// Clients branch on these — never rename an existing value.
/// </summary>
public static class SyncApiErrorCodes
{
    public const string Unauthorized = "unauthorized";
    public const string InvalidPairingCode = "invalid_pairing_code";
    public const string DeviceUnknown = "device_unknown";
    public const string DeviceRevoked = "device_revoked";
    public const string RefreshTokenInvalid = "refresh_token_invalid";
    public const string ValidationError = "validation_error";
    public const string CaptureNotFound = "capture_not_found";
    public const string CaptureConflict = "capture_conflict";
    public const string ArtifactNotFound = "artifact_not_found";
    public const string ArtifactConflict = "artifact_conflict";
    public const string ArtifactInvalidState = "artifact_invalid_state";
    public const string ArtifactPartInvalid = "artifact_part_invalid";
    public const string ArtifactPartTooLarge = "artifact_part_too_large";
    public const string ArtifactIncompleteParts = "artifact_incomplete_parts";
    public const string ArtifactPartCountMismatch = "artifact_part_count_mismatch";
    public const string ArtifactLengthMismatch = "artifact_length_mismatch";
    public const string ArtifactHashMismatch = "artifact_hash_mismatch";
    public const string ArtifactNotComplete = "artifact_not_complete";

    /// <summary>
    /// The artifact's bytes were removed by the retention sweep (design §22
    /// decision 7). Permanent — the metadata row survives, the content does
    /// not — so a download answers 410, never a retryable error.
    /// </summary>
    public const string ArtifactPurged = "artifact_purged";
    public const string AssistantSessionNotFound = "assistant_session_not_found";
    public const string AssistantTurnNotFound = "assistant_turn_not_found";
    public const string AssistantTurnConflict = "assistant_turn_conflict";

    /// <summary>
    /// The caller is authenticated but may not author this change: a companion
    /// publishing a Windows-authored timeline projection, or a device
    /// attributing a review decision to a different device.
    ///
    /// Deliberately not <see cref="Unauthorized"/>. That code tells a client its
    /// credentials are the problem, and a sync client that reads it re-registers
    /// or refreshes its token — pointless here, because the token is fine and
    /// the same entry would be refused again. This code says the opposite: drop
    /// the entry from the outbox, it will never be accepted.
    /// </summary>
    public const string ChangeNotPermitted = "change_not_permitted";

    public const string InternalError = "internal_error";
}
