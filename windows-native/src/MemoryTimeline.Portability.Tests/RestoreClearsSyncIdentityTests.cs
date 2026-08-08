using FluentAssertions;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MemoryTimeline.Portability.Tests;

/// <summary>
/// Restoring a backup must not leave this installation holding the sync
/// identity of the machine that made it.
///
/// <para><b>The bug this pins.</b> <c>sync_device_id</c> lives in
/// <c>app_settings</c>, which lives in the database file
/// <see cref="BackupService.RestoreAsync"/> overwrites wholesale. "Set my new
/// computer up from a backup" is the most natural route to two machines sharing
/// one device id, and the sync system then fails silently in two compounding
/// ways:</para>
///
/// <list type="bullet">
/// <item><description>Pull excludes the caller's own changes
/// (<c>SyncChangeService.PullAsync</c> filters
/// <c>SourceDeviceId != caller.DeviceId</c>), so two machines sharing an id
/// become permanently invisible to each other while both report healthy
/// syncs.</description></item>
/// <item><description>A push receipt is keyed <c>(DeviceId, ClientSequence)</c>
/// and <c>ClientSequence</c> is the local outbox row id. Colliding ids make the
/// service short-circuit on the receipt <i>before validating anything</i> and
/// answer <c>Accepted=true, Duplicate=true</c>; the client treats Duplicate
/// exactly like Accepted and marks the row delivered. Real edits vanish with a
/// green status and nothing logged.</description></item>
/// </list>
///
/// <para>This lives in the portability suite rather than
/// <c>MemoryTimeline.Tests</c> because the two-desktop topology it protects is
/// specifically Windows-plus-macOS, and because it must keep passing on the
/// platform the second desktop will run on.</para>
/// </summary>
public class RestoreClearsSyncIdentityTests : IDisposable
{
    private readonly string _root;
    private readonly string _databasePath;
    private readonly TestContextFactory _factory;
    private readonly SettingsService _settings;
    private readonly BackupService _backup;

    public RestoreClearsSyncIdentityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"restore-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "memory-timeline.db");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=False")
            .Options;
        _factory = new TestContextFactory(options);
        _settings = new SettingsService(_factory, NullLogger<SettingsService>.Instance);
        _backup = new BackupService(
            _factory,
            _settings,
            new StubMediaService(Path.Combine(_root, "media")),
            NullLogger<BackupService>.Instance,
            audioRootOverride: Path.Combine(_root, "audio"));
    }

    [Fact]
    public async Task RestoreAsync_ClearsTheDeviceIdentityCarriedInsideTheBackup()
    {
        await using (var context = _factory.CreateDbContext())
        {
            await SchemaUpgrader.EnsureSchemaAsync(context);
        }

        // A paired machine, with real content so the backup is not degenerate.
        await _settings.SetSettingAsync(SettingKeys.SyncDeviceId, "device-from-the-old-pc");
        await _settings.SetSettingAsync(SettingKeys.SyncAccessToken, "access-token");
        await _settings.SetSettingAsync(SettingKeys.SyncRefreshToken, "refresh-token");
        await _settings.SetSettingAsync(SettingKeys.SyncCursor, "4200");
        await _settings.SetSettingAsync(SettingKeys.SyncEnabled, true);

        await using (var context = _factory.CreateDbContext())
        {
            context.Events.Add(new Event
            {
                Title = "Ferry to the island",
                StartDate = new DateTime(2003, 7, 14),
                DatePrecision = DatePrecision.Season,
            });
            await context.SaveChangesAsync();
        }

        var backupPath = Path.Combine(_root, "archive.mtbak");
        await _backup.CreateBackupAsync(backupPath, new BackupOptions());
        File.Exists(backupPath).Should().BeTrue("the backup is the fixture for the restore");

        // The second machine: a different identity, about to be overwritten by
        // the backup's copy of app_settings.
        await _settings.SetSettingAsync(SettingKeys.SyncDeviceId, "device-on-this-mac");

        await _backup.RestoreAsync(backupPath, RestoreMode.Replace);

        // Read straight from the restored file rather than the settings cache:
        // the assertion is about what is on disk, and a cache that happened to
        // be clean would hide a row that is not.
        await using var probe = _factory.CreateDbContext();
        var remaining = await probe.AppSettings.AsNoTracking()
            .Where(s => s.SettingKey == SettingKeys.SyncDeviceId
                || s.SettingKey == SettingKeys.SyncAccessToken
                || s.SettingKey == SettingKeys.SyncRefreshToken
                || s.SettingKey == SettingKeys.SyncCursor)
            .Select(s => s.SettingKey)
            .ToListAsync();

        remaining.Should().BeEmpty(
            "an installation restored from someone else's backup must re-pair as itself, "
            + "or the two machines share a device id and silently stop seeing each other");

        var enabled = await probe.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SettingKey == SettingKeys.SyncEnabled);
        enabled?.SettingValue.Should().NotBe("true", "sync must not resume automatically on a foreign identity");
    }

    [Fact]
    public async Task RestoreAsync_StillRestoresTheArchiveItself()
    {
        // Guards the fix: clearing the identity must not have cost the restore.
        await using (var context = _factory.CreateDbContext())
        {
            await SchemaUpgrader.EnsureSchemaAsync(context);
            context.Events.Add(new Event
            {
                Title = "Ferry to the island",
                StartDate = new DateTime(2003, 7, 14),
            });
            await context.SaveChangesAsync();
        }

        var backupPath = Path.Combine(_root, "archive.mtbak");
        await _backup.CreateBackupAsync(backupPath, new BackupOptions());

        await using (var context = _factory.CreateDbContext())
        {
            context.Events.RemoveRange(context.Events);
            await context.SaveChangesAsync();
        }

        await _backup.RestoreAsync(backupPath, RestoreMode.Replace);

        await using var probe = _factory.CreateDbContext();
        (await probe.Events.AsNoTracking().CountAsync())
            .Should().Be(1, "the whole point of a restore is getting the memories back");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp tree is not worth failing a run over.
        }
        GC.SuppressFinalize(this);
    }

    private sealed class TestContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    /// <summary>
    /// Only <c>MediaRoot</c> is reached by backup and restore; everything else
    /// throws so an unexpected call is a loud failure rather than a silent no-op.
    /// </summary>
    private sealed class StubMediaService(string mediaRoot) : IMediaService
    {
        public string MediaRoot { get; } = mediaRoot;

        public Task<EventMedia> AttachAsync(string eventId, string sourceFilePath, string? caption = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<List<EventMedia>> AttachManyAsync(string eventId, IEnumerable<string> paths, IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<List<EventMedia>> GetForEventAsync(string eventId) => throw new NotSupportedException();
        public Task RemoveAsync(string mediaId, bool deleteFile = true) => throw new NotSupportedException();
        public Task UpdateCaptionAsync(string mediaId, string? caption) => throw new NotSupportedException();
        public Task ReorderAsync(string eventId, IReadOnlyList<string> mediaIdsInOrder) => throw new NotSupportedException();
        public Task<MediaProbeResult> ProbeAsync(string filePath) => throw new NotSupportedException();
        public string GetAbsolutePath(EventMedia media) => throw new NotSupportedException();
        public string? GetAbsoluteThumbnailPath(EventMedia media) => throw new NotSupportedException();
        public void DeleteFiles(IEnumerable<EventMedia> media) => throw new NotSupportedException();
        public Task<MediaCleanupResult> CleanupOrphansAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
