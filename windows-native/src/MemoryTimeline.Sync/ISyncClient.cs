using MemoryTimeline.SyncContracts;

namespace MemoryTimeline.Sync;

/// <summary>
/// HTTP client for the Memory Line Sync API v1 (design §7.4). Implementations
/// attach the device access token from <see cref="ISyncSettingsStore"/> and
/// transparently refresh it once on a 401 before failing.
/// </summary>
public interface ISyncClient
{
    /// <summary>
    /// Registers this machine as a device. The only call that takes an explicit
    /// server URL and needs no stored credentials; callers persist the returned
    /// identity/tokens via <see cref="ISyncSettingsStore"/> on success.
    /// </summary>
    Task<DeviceRegisterResponse> RegisterDeviceAsync(
        string serverUrl,
        DeviceRegisterRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Pulls changes strictly after <paramref name="cursor"/>, oldest first.</summary>
    Task<SyncPullResponse> PullChangesAsync(long cursor, int limit, CancellationToken cancellationToken = default);

    Task<SyncPushResponse> PushChangesAsync(SyncPushRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reports the highest durably-applied change ID (server-side diagnostics).</summary>
    Task AckAsync(long cursor, CancellationToken cancellationToken = default);

    /// <summary>Revokes this device's registration on the server.</summary>
    Task RevokeDeviceAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Downloads artifact bytes to a local cache path (design §7.4 "artifact
/// transfer"). Integrity is re-verified downstream by capture ingestion.
/// </summary>
public interface IArtifactTransferClient
{
    Task DownloadArtifactAsync(string artifactId, string destinationPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sync configuration and device identity persisted in app settings
/// (SettingKeys.Sync*). The access and refresh tokens are stored the way the
/// app's API keys are: in the settings table, DPAPI-protected at rest through
/// the ISecretProtector port (SettingsService protects every key listed in
/// SecretProtection.SecretKeys), so a backup restored under another Windows
/// account reads them as empty and the machine must re-pair.
/// </summary>
public interface ISyncSettingsStore
{
    Task<bool> IsSyncEnabledAsync();

    Task<string?> GetServerUrlAsync();

    Task<string?> GetDeviceIdAsync();

    Task<string?> GetAccessTokenAsync();

    Task<string?> GetRefreshTokenAsync();

    Task<bool> IsAutoProcessEnabledAsync();

    /// <summary>Persists the registration result (device identity + both tokens + URL).</summary>
    Task SaveRegistrationAsync(string serverUrl, DeviceRegisterResponse registration);

    Task SaveTokensAsync(string accessToken, string refreshToken);

    /// <summary>Clears device identity, tokens, and cursor (unpair).</summary>
    Task ClearRegistrationAsync();

    /// <summary>
    /// Whether a full timeline projection backfill is owed to the server
    /// (<c>SettingKeys.TimelineProjectionBackfillPending</c>): set by
    /// <see cref="SaveRegistrationAsync"/> and by a restore, consumed by the
    /// sync worker. Default implementations so a fake store that does not
    /// care about backfills keeps compiling and never asks for one.
    /// </summary>
    Task<bool> IsProjectionBackfillPendingAsync() => Task.FromResult(false);

    /// <summary>Records or clears the owed backfill.</summary>
    Task SetProjectionBackfillPendingAsync(bool pending) => Task.CompletedTask;
}
