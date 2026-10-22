using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// <see cref="BackupService.RestoreAsync"/> replaces the database file on
/// disk, <c>sync_outbox</c> included, so the restored outbox remembers
/// deliveries made for another archive state and nothing would republish the
/// restored rows. The restore must therefore flag a full projection backfill
/// for the sync worker's next cycle. File-backed SQLite, because restore
/// swaps the file underneath the factory (the EF InMemory provider has no
/// file to swap).
/// </summary>
public class RestoreSchedulesProjectionBackfillTests : IDisposable
{
    private readonly string _root;
    private readonly TestDbContextFactory _factory;
    private readonly SettingsService _settings;
    private readonly BackupService _backup;

    public RestoreSchedulesProjectionBackfillTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"RestoreBackfill_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "memory-timeline.db");
        _factory = TestDbContextFactory.CreateSqliteFile(databasePath);
        using (var context = _factory.CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        _settings = new SettingsService(_factory, NullLogger<SettingsService>.Instance);
        var media = new MediaService(
            new EventMediaRepository(_factory),
            _factory,
            new Mock<IThumbnailGenerator>().Object,
            NullLogger<MediaService>.Instance,
            mediaRootOverride: Path.Combine(_root, "media"));
        _backup = new BackupService(
            _factory,
            _settings,
            media,
            NullLogger<BackupService>.Instance,
            audioRootOverride: Path.Combine(_root, "audio"));
    }

    [Fact]
    public async Task RestoreAsync_FlagsAFullProjectionBackfill()
    {
        // Arrange - a backup of an archive with content, taken while nothing was owed
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

        (await _settings.SettingExistsAsync(SettingKeys.TimelineProjectionBackfillPending)).Should().BeFalse();

        var backupPath = Path.Combine(_root, "archive.mtbak");
        await _backup.CreateBackupAsync(backupPath, new BackupOptions());

        // Act
        await _backup.RestoreAsync(backupPath, RestoreMode.Replace);

        // Assert - read the restored file directly, not the settings cache
        await using var probe = _factory.CreateDbContext();
        var flag = await probe.AppSettings.AsNoTracking()
            .SingleOrDefaultAsync(s => s.SettingKey == SettingKeys.TimelineProjectionBackfillPending);
        flag.Should().NotBeNull("a restored outbox cannot be trusted to describe what the server holds");
        flag!.SettingValue.Should().Be("true");

        // ...and the archive itself still came back.
        (await probe.Events.CountAsync()).Should().Be(1);
    }

    public void Dispose()
    {
        using (var context = _factory.CreateDbContext())
        {
            context.Database.EnsureDeleted();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Leftover handles on Windows CI: temp dirs are cleaned by the OS.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
