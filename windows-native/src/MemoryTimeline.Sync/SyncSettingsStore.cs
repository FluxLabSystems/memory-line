using MemoryTimeline.Core.Services;
using MemoryTimeline.SyncContracts;
using Microsoft.Extensions.Logging;

namespace MemoryTimeline.Sync;

/// <summary>
/// <see cref="ISyncSettingsStore"/> over the app settings table via
/// <see cref="ISettingsService"/>, using the <see cref="SettingKeys"/> Sync*
/// constants. Tokens live in app_settings like the app's other keys; both token
/// keys are in <see cref="SecretProtection.SecretKeys"/>, so the settings
/// service stores them through the registered <see cref="ISecretProtector"/>
/// (DPAPI on Windows) and hands this store plaintext on read. Token values are
/// never logged.
/// </summary>
public class SyncSettingsStore : ISyncSettingsStore
{
    private readonly ISettingsService _settingsService;
    private readonly ILogger<SyncSettingsStore> _logger;

    public SyncSettingsStore(ISettingsService settingsService, ILogger<SyncSettingsStore> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> IsSyncEnabledAsync() =>
        await _settingsService.GetSettingAsync(SettingKeys.SyncEnabled, false);

    /// <inheritdoc />
    public Task<string?> GetServerUrlAsync() => GetNonEmptyAsync(SettingKeys.SyncServerUrl);

    /// <inheritdoc />
    public Task<string?> GetDeviceIdAsync() => GetNonEmptyAsync(SettingKeys.SyncDeviceId);

    /// <inheritdoc />
    public Task<string?> GetAccessTokenAsync() => GetNonEmptyAsync(SettingKeys.SyncAccessToken);

    /// <inheritdoc />
    public Task<string?> GetRefreshTokenAsync() => GetNonEmptyAsync(SettingKeys.SyncRefreshToken);

    /// <inheritdoc />
    public async Task<bool> IsAutoProcessEnabledAsync() =>
        await _settingsService.GetSettingAsync(SettingKeys.SyncAutoProcess, true);

    /// <inheritdoc />
    /// <remarks>
    /// The display name persisted is this machine's name — the value the
    /// registration request carried; <see cref="DeviceRegisterResponse"/> does
    /// not echo it back.
    /// </remarks>
    public async Task SaveRegistrationAsync(string serverUrl, DeviceRegisterResponse registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        ArgumentNullException.ThrowIfNull(registration);

        await _settingsService.SetSettingAsync(SettingKeys.SyncServerUrl, serverUrl.TrimEnd('/'));
        await _settingsService.SetSettingAsync(SettingKeys.SyncDeviceId, registration.DeviceId);
        await _settingsService.SetSettingAsync(SettingKeys.SyncDeviceDisplayName, Environment.MachineName);
        await _settingsService.SetSettingAsync(SettingKeys.SyncAccessToken, registration.AccessToken);
        await _settingsService.SetSettingAsync(SettingKeys.SyncRefreshToken, registration.RefreshToken);
        await _settingsService.SetSettingAsync(SettingKeys.SyncEnabled, true);

        // A new registration means a server (or a device identity) that has
        // never seen this archive: whatever the outbox remembers delivering
        // was delivered as someone else. The worker projects everything again
        // in its first cycle.
        await SetProjectionBackfillPendingAsync(true);

        _logger.LogInformation("Sync registration saved for device {DeviceId}; sync enabled", registration.DeviceId);
    }

    /// <inheritdoc />
    public async Task<bool> IsProjectionBackfillPendingAsync() =>
        await _settingsService.GetSettingAsync(SettingKeys.TimelineProjectionBackfillPending, false);

    /// <inheritdoc />
    public async Task SetProjectionBackfillPendingAsync(bool pending)
    {
        if (pending)
        {
            await _settingsService.SetSettingAsync(SettingKeys.TimelineProjectionBackfillPending, true);
        }
        else
        {
            // Absent means "nothing owed" (the key is not seeded), so clearing
            // is a delete rather than a "false" row.
            await _settingsService.DeleteSettingAsync(SettingKeys.TimelineProjectionBackfillPending);
        }
    }

    /// <inheritdoc />
    public async Task SaveTokensAsync(string accessToken, string refreshToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        await _settingsService.SetSettingAsync(SettingKeys.SyncAccessToken, accessToken);
        await _settingsService.SetSettingAsync(SettingKeys.SyncRefreshToken, refreshToken);
    }

    /// <inheritdoc />
    public async Task ClearRegistrationAsync()
    {
        await _settingsService.DeleteSettingAsync(SettingKeys.SyncDeviceId);
        await _settingsService.DeleteSettingAsync(SettingKeys.SyncAccessToken);
        await _settingsService.DeleteSettingAsync(SettingKeys.SyncRefreshToken);
        await _settingsService.DeleteSettingAsync(SettingKeys.SyncCursor);
        await _settingsService.SetSettingAsync(SettingKeys.SyncEnabled, false);

        _logger.LogInformation("Sync registration cleared; sync disabled");
    }

    private async Task<string?> GetNonEmptyAsync(string key)
    {
        var value = await _settingsService.GetSettingAsync<string>(key);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
