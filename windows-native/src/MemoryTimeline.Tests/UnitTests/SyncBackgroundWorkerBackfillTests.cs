using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Sync;
using MemoryTimeline.SyncContracts;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// The sync worker's handling of an owed timeline projection backfill
/// (set by a new registration or a restore): it runs once, in the cycle,
/// BEFORE the outbox drain so its rows ride the same push; the flag clears
/// only on success; a failure neither fails the cycle nor loses the flag; and
/// without a publisher the cycle carries on. Companion test to
/// <see cref="SyncBackgroundWorkerTests"/>, which covers the pull/ack/drain
/// loop itself.
/// </summary>
public class SyncBackgroundWorkerBackfillTests
{
    private readonly Mock<ISyncClient> _syncClient = new();
    private readonly Mock<IRemoteChangeApplier> _changeApplier = new();
    private readonly Mock<ILocalOutboxPublisher> _outboxPublisher = new();
    private readonly Mock<ITimelineProjectionPublisher> _projectionPublisher = new();
    private readonly FakeCursorStore _cursorStore = new();
    private readonly FakeSyncSettingsStore _settingsStore = new();
    private readonly List<string> _sequence = new();

    public SyncBackgroundWorkerBackfillTests()
    {
        _syncClient
            .Setup(c => c.PullChangesAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncPullResponse { Changes = new List<SyncChangeDto>(), NextCursor = 0, HasMore = false });
        _outboxPublisher
            .Setup(p => p.PublishPendingAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _sequence.Add("drain"))
            .ReturnsAsync(3);
        _projectionPublisher
            .Setup(p => p.PublishAllAsync(It.IsAny<TimelineProjectionBackfillMode>(), It.IsAny<CancellationToken>()))
            .Callback(() => _sequence.Add("backfill"))
            .ReturnsAsync(new TimelineProjectionBackfillResult(1, 2, 3, 4, 0));
    }

    private SyncBackgroundWorker CreateWorker(bool withPublisher = true) => new(
        _syncClient.Object,
        _changeApplier.Object,
        _outboxPublisher.Object,
        _cursorStore,
        _settingsStore,
        NullLogger<SyncBackgroundWorker>.Instance,
        withPublisher ? _projectionPublisher.Object : null);

    [Fact]
    public async Task SyncNowAsync_BackfillOwed_RunsEverythingModeBeforeTheDrainAndClearsTheFlag()
    {
        // Arrange
        _settingsStore.BackfillPending = true;

        // Act
        var result = await CreateWorker().SyncNowAsync();

        // Assert
        result.Ran.Should().BeTrue();
        result.Error.Should().BeNull();
        result.ProjectionsBackfilled.Should().Be(10);
        result.OutboxPublished.Should().Be(3);
        _sequence.Should().Equal("backfill", "drain");
        _projectionPublisher.Verify(
            p => p.PublishAllAsync(TimelineProjectionBackfillMode.Everything, It.IsAny<CancellationToken>()),
            Times.Once);
        _settingsStore.BackfillPending.Should().BeFalse();
        _settingsStore.SetHistory.Should().Equal(false);
    }

    [Fact]
    public async Task SyncNowAsync_BackfillOwed_RunsOnlyOnce()
    {
        // Arrange
        _settingsStore.BackfillPending = true;
        var worker = CreateWorker();

        // Act
        await worker.SyncNowAsync();
        var second = await worker.SyncNowAsync();

        // Assert
        second.ProjectionsBackfilled.Should().Be(0);
        _projectionPublisher.Verify(
            p => p.PublishAllAsync(It.IsAny<TimelineProjectionBackfillMode>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SyncNowAsync_NothingOwed_NeverTouchesThePublisher()
    {
        // Arrange
        _settingsStore.BackfillPending = false;

        // Act
        var result = await CreateWorker().SyncNowAsync();

        // Assert
        result.ProjectionsBackfilled.Should().Be(0);
        _projectionPublisher.VerifyNoOtherCalls();
        _sequence.Should().Equal("drain");
    }

    [Fact]
    public async Task SyncNowAsync_BackfillThrows_FlagStaysOwedAndTheCycleStillDrains()
    {
        // Arrange
        _settingsStore.BackfillPending = true;
        _projectionPublisher
            .Setup(p => p.PublishAllAsync(It.IsAny<TimelineProjectionBackfillMode>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("outbox locked"));

        // Act
        var result = await CreateWorker().SyncNowAsync();

        // Assert - retried next cycle; this cycle is not an error
        result.Error.Should().BeNull();
        result.ProjectionsBackfilled.Should().Be(0);
        result.OutboxPublished.Should().Be(3);
        _settingsStore.BackfillPending.Should().BeTrue();
        _outboxPublisher.Verify(p => p.PublishPendingAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncNowAsync_NoPublisherRegistered_LeavesTheFlagAndCarriesOn()
    {
        // Arrange
        _settingsStore.BackfillPending = true;

        // Act
        var result = await CreateWorker(withPublisher: false).SyncNowAsync();

        // Assert
        result.Ran.Should().BeTrue();
        result.ProjectionsBackfilled.Should().Be(0);
        _settingsStore.BackfillPending.Should().BeTrue();
        _outboxPublisher.Verify(p => p.PublishPendingAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncNowAsync_SyncDisabled_DoesNotBackfill()
    {
        // Arrange - owed, but there is no server to send it to yet
        _settingsStore.BackfillPending = true;
        _settingsStore.SyncEnabled = false;

        // Act
        var result = await CreateWorker().SyncNowAsync();

        // Assert
        result.Ran.Should().BeFalse();
        _projectionPublisher.VerifyNoOtherCalls();
        _settingsStore.BackfillPending.Should().BeTrue();
    }

    #region Fakes

    private sealed class FakeCursorStore : ISyncCursorStore
    {
        public long Cursor { get; private set; }

        public Task<long> GetCursorAsync() => Task.FromResult(Cursor);

        public Task SetCursorAsync(long cursor)
        {
            Cursor = cursor;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSyncSettingsStore : ISyncSettingsStore
    {
        public bool SyncEnabled { get; set; } = true;

        public bool BackfillPending { get; set; }

        public List<bool> SetHistory { get; } = new();

        public Task<bool> IsSyncEnabledAsync() => Task.FromResult(SyncEnabled);

        public Task<string?> GetServerUrlAsync() => Task.FromResult<string?>("https://sync.example.test");

        public Task<string?> GetDeviceIdAsync() => Task.FromResult<string?>("windows-device-1");

        public Task<string?> GetAccessTokenAsync() => Task.FromResult<string?>("access-token");

        public Task<string?> GetRefreshTokenAsync() => Task.FromResult<string?>("refresh-token");

        public Task<bool> IsAutoProcessEnabledAsync() => Task.FromResult(true);

        public Task SaveRegistrationAsync(string serverUrl, DeviceRegisterResponse registration) => Task.CompletedTask;

        public Task SaveTokensAsync(string accessToken, string refreshToken) => Task.CompletedTask;

        public Task ClearRegistrationAsync() => Task.CompletedTask;

        public Task<bool> IsProjectionBackfillPendingAsync() => Task.FromResult(BackfillPending);

        public Task SetProjectionBackfillPendingAsync(bool pending)
        {
            BackfillPending = pending;
            SetHistory.Add(pending);
            return Task.CompletedTask;
        }
    }

    #endregion
}
