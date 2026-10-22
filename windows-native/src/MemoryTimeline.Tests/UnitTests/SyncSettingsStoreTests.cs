using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Sync;
using MemoryTimeline.SyncContracts;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Tests for <see cref="SyncSettingsStore"/> over the REAL
/// <see cref="SettingsService"/> and the EF InMemory provider (unique database
/// per test instance), covering defaults, registration round-trips, and unpair.
/// </summary>
public class SyncSettingsStoreTests
{
    private readonly SettingsService _settingsService;
    private readonly SyncSettingsStore _store;

    public SyncSettingsStoreTests()
    {
        var contextFactory = TestDbContextFactory.CreateInMemory();
        _settingsService = new SettingsService(contextFactory, NullLogger<SettingsService>.Instance);
        _store = new SyncSettingsStore(_settingsService, NullLogger<SyncSettingsStore>.Instance);
    }

    [Fact]
    public async Task Defaults_NoStoredSettings_SyncDisabledAutoProcessEnabledIdentityEmpty()
    {
        // Assert - a fresh install must not sync until the user pairs, but a
        // paired install processes remote captures automatically by default
        (await _store.IsSyncEnabledAsync()).Should().BeFalse();
        (await _store.IsAutoProcessEnabledAsync()).Should().BeTrue();
        (await _store.GetServerUrlAsync()).Should().BeNull();
        (await _store.GetDeviceIdAsync()).Should().BeNull();
        (await _store.GetAccessTokenAsync()).Should().BeNull();
        (await _store.GetRefreshTokenAsync()).Should().BeNull();
    }

    [Fact]
    public async Task SaveRegistrationAsync_PersistsIdentityAndTokensAndEnablesSync()
    {
        // Arrange
        var registration = CreateRegistration();

        // Act - trailing slash on the URL is normalized away
        await _store.SaveRegistrationAsync("https://sync.example.test:8443/", registration);

        // Assert
        (await _store.IsSyncEnabledAsync()).Should().BeTrue();
        (await _store.GetServerUrlAsync()).Should().Be("https://sync.example.test:8443");
        (await _store.GetDeviceIdAsync()).Should().Be(registration.DeviceId);
        (await _store.GetAccessTokenAsync()).Should().Be(registration.AccessToken);
        (await _store.GetRefreshTokenAsync()).Should().Be(registration.RefreshToken);
        (await _settingsService.GetSettingAsync<string>(SettingKeys.SyncDeviceDisplayName))
            .Should().Be(Environment.MachineName);
    }

    [Fact]
    public async Task SaveTokensAsync_ReplacesBothTokens()
    {
        // Arrange
        await _store.SaveRegistrationAsync("https://sync.example.test", CreateRegistration());

        // Act - a token refresh rotates both tokens
        await _store.SaveTokensAsync("rotated-access", "rotated-refresh");

        // Assert
        (await _store.GetAccessTokenAsync()).Should().Be("rotated-access");
        (await _store.GetRefreshTokenAsync()).Should().Be("rotated-refresh");
    }

    [Fact]
    public async Task ClearRegistrationAsync_ClearsIdentityTokensCursorAndDisablesSync()
    {
        // Arrange - a fully paired device with a stored cursor
        await _store.SaveRegistrationAsync("https://sync.example.test", CreateRegistration());
        var cursorStore = new SettingsSyncCursorStore(_settingsService);
        await cursorStore.SetCursorAsync(42);

        // Act - unpair
        await _store.ClearRegistrationAsync();

        // Assert - identity, tokens, and cursor are gone and sync is off
        (await _store.IsSyncEnabledAsync()).Should().BeFalse();
        (await _store.GetDeviceIdAsync()).Should().BeNull();
        (await _store.GetAccessTokenAsync()).Should().BeNull();
        (await _store.GetRefreshTokenAsync()).Should().BeNull();
        (await _settingsService.SettingExistsAsync(SettingKeys.SyncCursor)).Should().BeFalse();
        (await cursorStore.GetCursorAsync()).Should().Be(0);

        // The server URL survives so re-pairing can pre-fill it
        (await _store.GetServerUrlAsync()).Should().Be("https://sync.example.test");
    }

    private static DeviceRegisterResponse CreateRegistration() => new()
    {
        DeviceId = Guid.NewGuid().ToString(),
        OwnerId = "owner-1",
        AccessToken = "access-token-1",
        AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
        RefreshToken = "refresh-token-1",
    };

    [Fact]
    public async Task SaveRegistrationAsync_OwesAFullProjectionBackfill()
    {
        // A new registration is a server that has never seen this archive:
        // the worker must project everything again in its first cycle.
        (await _store.IsProjectionBackfillPendingAsync()).Should().BeFalse("nothing is owed on a fresh install");

        await _store.SaveRegistrationAsync("https://sync.example.test", CreateRegistration());

        (await _store.IsProjectionBackfillPendingAsync()).Should().BeTrue();
        (await _settingsService.GetSettingAsync<bool>(SettingKeys.TimelineProjectionBackfillPending)).Should().BeTrue();
    }

    [Fact]
    public async Task SetProjectionBackfillPendingAsync_False_RemovesTheRowRatherThanStoringFalse()
    {
        // Absent means "nothing owed" (the key is deliberately not seeded).
        await _store.SetProjectionBackfillPendingAsync(true);
        await _store.SetProjectionBackfillPendingAsync(false);

        (await _store.IsProjectionBackfillPendingAsync()).Should().BeFalse();
        (await _settingsService.SettingExistsAsync(SettingKeys.TimelineProjectionBackfillPending)).Should().BeFalse();
    }
}
