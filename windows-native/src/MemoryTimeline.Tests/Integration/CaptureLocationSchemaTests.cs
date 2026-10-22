using FluentAssertions;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MemoryTimeline.Tests.Integration;

/// <summary>
/// The capture location columns (the first schema change shipped as a
/// migration after the baseline, not as SchemaUpgrader DDL) against real
/// FILE-based SQLite databases: a fresh database has them and the capture
/// repository round-trips them; a database whose captures table has the
/// baseline shape gains them through the migration on upgrade.
/// </summary>
public class CaptureLocationSchemaTests : IDisposable
{
    private static readonly string[] LocationColumns =
    {
        "location_latitude",
        "location_longitude",
        "location_accuracy_m",
    };

    private readonly string _databasePath;
    private readonly TestDbContextFactory _factory;

    public CaptureLocationSchemaTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"CaptureLocationTest_{Guid.NewGuid()}.db");
        _factory = TestDbContextFactory.CreateSqliteFile(_databasePath);
    }

    private string RawConnectionString => $"Data Source={_databasePath};Pooling=False";

    [Fact]
    public async Task EnsureSchemaAsync_FreshDatabase_HasTheColumns_AndTheRepositoryRoundTripsASnapshot()
    {
        // Arrange
        await RunSchemaUpgraderAsync();
        (await GetColumnNamesAsync("captures")).Should().Contain(LocationColumns);

        var repository = new CaptureRepository(_factory);
        var capture = new Capture
        {
            SourcePlatform = CapturePlatform.Ios,
            CaptureType = CaptureTypes.TripLog,
            LocationLatitude = 36.0544,
            LocationLongitude = -112.1401,
            LocationAccuracyM = 12.5,
        };
        var artifact = new CaptureArtifact
        {
            CaptureId = capture.CaptureId,
            ArtifactType = CaptureArtifactType.AudioOriginal,
            StorageLocator = "/cache/clip.m4a",
            UploadState = CaptureArtifactUploadState.Downloaded,
        };
        var queueItem = new RecordingQueue
        {
            AudioFilePath = "/cache/clip.m4a",
            SourceCaptureId = capture.CaptureId,
            SourcePlatform = CapturePlatform.Ios,
            AudioArtifactId = artifact.ArtifactId,
            SyncState = QueueSyncState.Received,
        };
        var outbox = new SyncOutbox
        {
            EntityType = SyncEntityType.Capture,
            EntityId = capture.CaptureId,
            PayloadJson = "{}",
        };

        // Act
        await repository.IngestAsync(capture, artifact, queueItem, outbox);

        // Assert
        var stored = await repository.GetByIdAsync(capture.CaptureId);
        stored!.LocationLatitude.Should().Be(36.0544);
        stored.LocationLongitude.Should().Be(-112.1401);
        stored.LocationAccuracyM.Should().Be(12.5);
    }

    [Fact]
    public async Task EnsureSchemaAsync_FreshDatabase_StoresNoSnapshotAsNulls()
    {
        // Arrange - a Windows-local capture has no fix
        await RunSchemaUpgraderAsync();
        var repository = new CaptureRepository(_factory);
        var capture = new Capture { SourcePlatform = CapturePlatform.Windows };

        // Act
        await repository.IngestAsync(
            capture,
            new CaptureArtifact { CaptureId = capture.CaptureId, StorageLocator = "/rec.wav" },
            new RecordingQueue { AudioFilePath = "/rec.wav", SourceCaptureId = capture.CaptureId },
            new SyncOutbox { EntityType = SyncEntityType.Capture, EntityId = capture.CaptureId });

        // Assert
        var stored = await repository.GetByIdAsync(capture.CaptureId);
        stored!.LocationLatitude.Should().BeNull();
        stored.LocationLongitude.Should().BeNull();
        stored.LocationAccuracyM.Should().BeNull();
    }

    [Fact]
    public async Task EnsureSchemaAsync_BaselineShapeCapturesTable_GainsTheColumnsThroughTheMigration()
    {
        // Arrange - captures as the baseline (and every EnsureCreated-era
        // build) created it: no location columns, no migrations history.
        await using (var connection = new SqliteConnection(RawConnectionString))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE "captures" (
                    "capture_id" TEXT NOT NULL CONSTRAINT "PK_captures" PRIMARY KEY,
                    "source_device_id" TEXT NULL,
                    "source_platform" TEXT NOT NULL,
                    "capture_type" TEXT NOT NULL,
                    "created_at_utc" TEXT NOT NULL,
                    "captured_at_utc" TEXT NOT NULL,
                    "timezone_id" TEXT NULL,
                    "local_offset_minutes" INTEGER NULL,
                    "status" TEXT NOT NULL,
                    "title_hint" TEXT NULL,
                    "user_note" TEXT NULL,
                    "trip_id" TEXT NULL,
                    "client_schema_version" INTEGER NOT NULL,
                    "server_revision" INTEGER NOT NULL,
                    "deleted_at_utc" TEXT NULL
                );
                INSERT INTO "captures" ("capture_id", "source_platform", "capture_type", "created_at_utc", "captured_at_utc", "status", "client_schema_version", "server_revision")
                VALUES ('older-capture', 'ios', 'memory', '2026-01-01 00:00:00', '2026-01-01 00:00:00', 'received', 1, 1);
                """;
            await command.ExecuteNonQueryAsync();
        }

        // Act
        await RunSchemaUpgraderAsync();

        // Assert - the migration added the columns; the pre-existing row reads
        // back with no snapshot and the model can write one next to it.
        (await GetColumnNamesAsync("captures")).Should().Contain(LocationColumns);

        await using var context = _factory.CreateDbContext();
        var older = await context.Captures.SingleAsync(c => c.CaptureId == "older-capture");
        older.LocationLatitude.Should().BeNull();

        context.Captures.Add(new Capture
        {
            CaptureId = "newer-capture",
            SourcePlatform = CapturePlatform.Ios,
            LocationLatitude = 1.5,
            LocationLongitude = 2.5,
        });
        await context.SaveChangesAsync();
        (await context.Captures.AsNoTracking().SingleAsync(c => c.CaptureId == "newer-capture"))
            .LocationLongitude.Should().Be(2.5);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    #region Helpers

    private async Task RunSchemaUpgraderAsync()
    {
        await using var context = _factory.CreateDbContext();
        await SchemaUpgrader.EnsureSchemaAsync(context);
    }

    private async Task<HashSet<string>> GetColumnNamesAsync(string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var connection = new SqliteConnection(RawConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\")";
        await using var reader = await command.ExecuteReaderAsync();
        var nameOrdinal = reader.GetOrdinal("name");
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(nameOrdinal));
        }

        return columns;
    }

    #endregion
}
