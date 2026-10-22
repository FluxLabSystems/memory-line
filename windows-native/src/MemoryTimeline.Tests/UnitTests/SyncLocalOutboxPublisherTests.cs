using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Sync;
using MemoryTimeline.SyncContracts;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Tests for <see cref="LocalOutboxPublisher"/> over the REAL
/// <see cref="SyncOutboxRepository"/> and the EF InMemory provider (unique
/// database per test instance) with a mocked <see cref="ISyncClient"/>. Covers
/// ordering, the ClientSequence=OutboxId idempotency mapping, delivery
/// bookkeeping, and failure behavior.
/// </summary>
public class SyncLocalOutboxPublisherTests
{
    private readonly TestDbContextFactory _contextFactory;
    private readonly SyncOutboxRepository _repository;
    private readonly Mock<ISyncClient> _syncClient = new();
    private readonly FakeSyncSettingsStore _settingsStore = new();
    private readonly LocalOutboxPublisher _publisher;

    public SyncLocalOutboxPublisherTests()
    {
        _contextFactory = TestDbContextFactory.CreateInMemory();
        _repository = new SyncOutboxRepository(_contextFactory);
        _publisher = new LocalOutboxPublisher(
            _repository, _syncClient.Object, _settingsStore, NullLogger<LocalOutboxPublisher>.Instance);
    }

    [Fact]
    public async Task PublishPendingAsync_PendingEntries_PushesInOrderWithClientSequenceAndMarksDelivered()
    {
        // Arrange
        var entries = new List<SyncOutbox>();
        for (var i = 1; i <= 3; i++)
        {
            entries.Add(await AddEntryAsync($"capture-{i}"));
        }

        var pushedRequests = new List<SyncPushRequest>();
        _syncClient
            .Setup(c => c.PushChangesAsync(It.IsAny<SyncPushRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SyncPushRequest request, CancellationToken _) =>
            {
                pushedRequests.Add(request);
                return Task.FromResult(AcceptAll(request));
            });

        // Act
        var delivered = await _publisher.PublishPendingAsync();

        // Assert
        delivered.Should().Be(3);

        pushedRequests.Should().HaveCount(1);
        var pushedEntries = pushedRequests[0].Entries;
        pushedEntries.Select(e => e.ClientSequence)
            .Should().Equal(entries.Select(e => e.OutboxId))
            .And.BeInAscendingOrder();
        pushedEntries.Select(e => e.EntityId).Should().Equal("capture-1", "capture-2", "capture-3");
        pushedEntries[0].EntityType.Should().Be(SyncEntityType.Capture);
        pushedEntries[0].Operation.Should().Be(SyncOutboxOperation.Upsert);
        pushedEntries[0].PayloadJson.Should().Be(entries[0].PayloadJson);
        pushedEntries[0].ChangedAtUtc.Should().Be(entries[0].CreatedAt);

        (await _repository.GetPendingCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PublishPendingAsync_DuplicateResult_IsMarkedDeliveredToo()
    {
        // Arrange - a replayed entry the server already applied earlier
        var entry = await AddEntryAsync("capture-1");
        _syncClient
            .Setup(c => c.PushChangesAsync(It.IsAny<SyncPushRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncPushResponse
            {
                Results = new List<SyncPushEntryResult>
                {
                    new() { ClientSequence = entry.OutboxId, Accepted = false, Duplicate = true },
                },
            });

        // Act
        var delivered = await _publisher.PublishPendingAsync();

        // Assert - duplicates are done, not errors
        delivered.Should().Be(1);
        (await _repository.GetPendingCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PublishPendingAsync_PerEntryError_RecordsFailedAttemptAndKeepsEntryPending()
    {
        // Arrange
        var accepted = await AddEntryAsync("capture-1");
        var rejected = await AddEntryAsync("capture-2");
        _syncClient
            .Setup(c => c.PushChangesAsync(It.IsAny<SyncPushRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncPushResponse
            {
                Results = new List<SyncPushEntryResult>
                {
                    new() { ClientSequence = accepted.OutboxId, Accepted = true },
                    new() { ClientSequence = rejected.OutboxId, Accepted = false, Error = "capture rejected" },
                },
            });

        // Act
        var delivered = await _publisher.PublishPendingAsync();

        // Assert - the rejected entry stays pending with the failure recorded
        delivered.Should().Be(1);

        var pending = await _repository.GetPendingAsync();
        pending.Should().ContainSingle().Which.OutboxId.Should().Be(rejected.OutboxId);

        await using var context = _contextFactory.CreateDbContext();
        var stored = await context.SyncOutboxEntries.AsNoTracking()
            .SingleAsync(o => o.OutboxId == rejected.OutboxId);
        stored.AttemptCount.Should().Be(1);
        stored.LastError.Should().Be("capture rejected");
        stored.DeliveredAt.Should().BeNull();
    }

    [Fact]
    public async Task PublishPendingAsync_PushThrows_ReturnsDeliveredSoFarAndLeavesEntriesPending()
    {
        // Arrange
        await AddEntryAsync("capture-1");
        await AddEntryAsync("capture-2");
        _syncClient
            .Setup(c => c.PushChangesAsync(It.IsAny<SyncPushRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SyncApiException(
                "service unavailable", HttpStatusCode.ServiceUnavailable, null, retryable: true));

        // Act
        var delivered = await _publisher.PublishPendingAsync();

        // Assert - nothing consumed; the next cycle retries the same entries
        delivered.Should().Be(0);
        (await _repository.GetPendingCountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task PublishPendingAsync_SyncDisabled_PublishesNothing()
    {
        // Arrange
        _settingsStore.SyncEnabled = false;
        await AddEntryAsync("capture-1");

        // Act
        var delivered = await _publisher.PublishPendingAsync();

        // Assert
        delivered.Should().Be(0);
        (await _repository.GetPendingCountAsync()).Should().Be(1);
        _syncClient.Verify(
            c => c.PushChangesAsync(It.IsAny<SyncPushRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PublishPendingAsync_EmptyOutbox_MakesNoPushCall()
    {
        // Act
        var delivered = await _publisher.PublishPendingAsync();

        // Assert
        delivered.Should().Be(0);
        _syncClient.Verify(
            c => c.PushChangesAsync(It.IsAny<SyncPushRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PublishPendingAsync_AcceptedCaptureEntry_AcknowledgesTheReceivedQueueItem()
    {
        // Arrange - a remote capture ingested here (received) whose capture
        // entry is still in the outbox
        var queueRepository = new RecordingQueueRepository(_contextFactory);
        var item = await queueRepository.AddAsync(RemoteQueueItem("capture-1"));
        await AddEntryAsync("capture-1");
        _syncClient
            .Setup(c => c.PushChangesAsync(It.IsAny<SyncPushRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SyncPushRequest request, CancellationToken _) => Task.FromResult(AcceptAll(request)));

        // Act
        var delivered = await CreatePublisher(queueRepository).PublishPendingAsync();

        // Assert - the service accepted the receipt, so the item says so
        delivered.Should().Be(1);
        (await queueRepository.GetByIdAsync(item.QueueId))!.SyncState.Should().Be(QueueSyncState.Acknowledged);
    }

    [Fact]
    public async Task PublishPendingAsync_DuplicateCaptureResult_AcknowledgesToo()
    {
        // Arrange - the server already applied this sequence on an earlier
        // push whose response was lost; it holds the record either way
        var queueRepository = new RecordingQueueRepository(_contextFactory);
        var item = await queueRepository.AddAsync(RemoteQueueItem("capture-1"));
        var entry = await AddEntryAsync("capture-1");
        _syncClient
            .Setup(c => c.PushChangesAsync(It.IsAny<SyncPushRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncPushResponse
            {
                Results = new List<SyncPushEntryResult>
                {
                    new() { ClientSequence = entry.OutboxId, Accepted = false, Duplicate = true },
                },
            });

        // Act
        await CreatePublisher(queueRepository).PublishPendingAsync();

        // Assert
        (await queueRepository.GetByIdAsync(item.QueueId))!.SyncState.Should().Be(QueueSyncState.Acknowledged);
    }

    [Fact]
    public async Task PublishPendingAsync_RejectedCaptureEntry_LeavesTheQueueItemReceived()
    {
        // Arrange
        var queueRepository = new RecordingQueueRepository(_contextFactory);
        var item = await queueRepository.AddAsync(RemoteQueueItem("capture-1"));
        var entry = await AddEntryAsync("capture-1");
        _syncClient
            .Setup(c => c.PushChangesAsync(It.IsAny<SyncPushRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncPushResponse
            {
                Results = new List<SyncPushEntryResult>
                {
                    new() { ClientSequence = entry.OutboxId, Accepted = false, Error = "capture rejected" },
                },
            });

        // Act
        await CreatePublisher(queueRepository).PublishPendingAsync();

        // Assert - no receipt, no acknowledgement; the entry is retried later
        (await queueRepository.GetByIdAsync(item.QueueId))!.SyncState.Should().Be(QueueSyncState.Received);
    }

    [Fact]
    public async Task PublishPendingAsync_AcceptedCaptureEntryForALocalRecording_LeavesItLocalOnly()
    {
        // Arrange - a recording made on this machine: the service now holds
        // its capture record, but nobody is waiting to hear it arrived
        var queueRepository = new RecordingQueueRepository(_contextFactory);
        var local = RemoteQueueItem("capture-1");
        local.SourcePlatform = CapturePlatform.Windows;
        local.SyncState = QueueSyncState.LocalOnly;
        var item = await queueRepository.AddAsync(local);
        await AddEntryAsync("capture-1");
        _syncClient
            .Setup(c => c.PushChangesAsync(It.IsAny<SyncPushRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SyncPushRequest request, CancellationToken _) => Task.FromResult(AcceptAll(request)));

        // Act
        var delivered = await CreatePublisher(queueRepository).PublishPendingAsync();

        // Assert
        delivered.Should().Be(1);
        (await queueRepository.GetByIdAsync(item.QueueId))!.SyncState.Should().Be(QueueSyncState.LocalOnly);
    }

    [Fact]
    public async Task PublishPendingAsync_AcceptedNonCaptureEntry_TouchesNoQueueItem()
    {
        // Arrange - a capture_status projection for the same capture is not
        // the capture record itself
        var queueRepository = new RecordingQueueRepository(_contextFactory);
        var item = await queueRepository.AddAsync(RemoteQueueItem("capture-1"));
        await _repository.AddAsync(new SyncOutbox
        {
            EntityType = SyncEntityType.CaptureStatus,
            EntityId = "capture-1",
            Operation = SyncOutboxOperation.Upsert,
            PayloadJson = "{\"captureId\":\"capture-1\"}",
        });
        _syncClient
            .Setup(c => c.PushChangesAsync(It.IsAny<SyncPushRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SyncPushRequest request, CancellationToken _) => Task.FromResult(AcceptAll(request)));

        // Act
        await CreatePublisher(queueRepository).PublishPendingAsync();

        // Assert
        (await queueRepository.GetByIdAsync(item.QueueId))!.SyncState.Should().Be(QueueSyncState.Received);
    }

    [Fact]
    public async Task PublishPendingAsync_WithoutAQueueRepository_StillDeliversCaptureEntries()
    {
        // Arrange - the construction every existing caller uses
        await AddEntryAsync("capture-1");
        _syncClient
            .Setup(c => c.PushChangesAsync(It.IsAny<SyncPushRequest>(), It.IsAny<CancellationToken>()))
            .Returns((SyncPushRequest request, CancellationToken _) => Task.FromResult(AcceptAll(request)));

        // Act
        var delivered = await _publisher.PublishPendingAsync();

        // Assert
        delivered.Should().Be(1);
        (await _repository.GetPendingCountAsync()).Should().Be(0);
    }

    #region Helpers

    private LocalOutboxPublisher CreatePublisher(IRecordingQueueRepository queueRepository) => new(
        _repository, _syncClient.Object, _settingsStore, NullLogger<LocalOutboxPublisher>.Instance, queueRepository);

    /// <summary>A queue item as remote-capture ingestion writes it: received, not yet acknowledged.</summary>
    private static RecordingQueue RemoteQueueItem(string captureId) => new()
    {
        QueueId = Guid.NewGuid().ToString(),
        AudioFilePath = "/cache/clip.wav",
        Status = QueueStatus.Pending,
        SourceCaptureId = captureId,
        SourcePlatform = CapturePlatform.Ios,
        SyncState = QueueSyncState.Received,
        CreatedAt = DateTime.UtcNow,
    };

    private async Task<SyncOutbox> AddEntryAsync(string entityId)
    {
        return await _repository.AddAsync(new SyncOutbox
        {
            EntityType = SyncEntityType.Capture,
            EntityId = entityId,
            Operation = SyncOutboxOperation.Upsert,
            PayloadJson = $"{{\"captureId\":\"{entityId}\"}}",
        });
    }

    private static SyncPushResponse AcceptAll(SyncPushRequest request) => new()
    {
        Results = request.Entries
            .Select(e => new SyncPushEntryResult { ClientSequence = e.ClientSequence, Accepted = true })
            .ToList(),
    };

    /// <summary>Enabled, paired sync settings with a toggleable enabled flag.</summary>
    private sealed class FakeSyncSettingsStore : ISyncSettingsStore
    {
        public bool SyncEnabled { get; set; } = true;

        public Task<bool> IsSyncEnabledAsync() => Task.FromResult(SyncEnabled);

        public Task<string?> GetServerUrlAsync() => Task.FromResult<string?>("https://sync.example.test");

        public Task<string?> GetDeviceIdAsync() => Task.FromResult<string?>("windows-device-1");

        public Task<string?> GetAccessTokenAsync() => Task.FromResult<string?>("access-token");

        public Task<string?> GetRefreshTokenAsync() => Task.FromResult<string?>("refresh-token");

        public Task<bool> IsAutoProcessEnabledAsync() => Task.FromResult(true);

        public Task SaveRegistrationAsync(string serverUrl, DeviceRegisterResponse registration) =>
            Task.CompletedTask;

        public Task SaveTokensAsync(string accessToken, string refreshToken) => Task.CompletedTask;

        public Task ClearRegistrationAsync() => Task.CompletedTask;
    }

    #endregion
}
