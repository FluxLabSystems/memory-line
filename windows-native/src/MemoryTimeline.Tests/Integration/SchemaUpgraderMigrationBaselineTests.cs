using FluentAssertions;
using MemoryTimeline.Data;
using MemoryTimeline.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace MemoryTimeline.Tests.Integration;

/// <summary>
/// The migration switch-over in <see cref="SchemaUpgrader.EnsureSchemaAsync"/>
/// against real FILE-based SQLite databases in each of the three states it
/// distinguishes (empty, unversioned, versioned), plus the two invariants
/// that keep the switch-over safe: the model has no change outside a
/// migration (MigrateAsync refuses to run otherwise, which would fail every
/// app start), and the id the upgrader labels unversioned databases with is
/// the first migration in the assembly. Schema is asserted with raw
/// Microsoft.Data.Sqlite queries so the tests verify what is in the file,
/// not what EF believes about it.
/// </summary>
public class SchemaUpgraderMigrationBaselineTests : IDisposable
{
    private const string HistoryTable = "__EFMigrationsHistory";

    private readonly string _databasePath;
    private readonly TestDbContextFactory _factory;

    public SchemaUpgraderMigrationBaselineTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"SchemaBaselineTest_{Guid.NewGuid()}.db");
        _factory = TestDbContextFactory.CreateSqliteFile(_databasePath);
    }

    private string RawConnectionString => $"Data Source={_databasePath};Pooling=False";

    [Fact]
    public void Model_HasNoChangesOutsideAMigration()
    {
        using var context = _factory.CreateDbContext();

        // MigrateAsync throws on pending model changes, so a model edit
        // without `dotnet ef migrations add` would fail every app start.
        context.Database.HasPendingModelChanges().Should().BeFalse(
            "every model change must ship with a migration (dotnet ef migrations add <Name> " +
            "--project windows-native/src/MemoryTimeline.Data)");
    }

    [Fact]
    public void BaselineMigrationId_IsTheFirstMigrationInTheAssembly()
    {
        using var context = _factory.CreateDbContext();

        context.Database.GetMigrations().First().Should().Be(SchemaUpgrader.BaselineMigrationId,
            "an unversioned database is labelled with the migration whose shape the repair raises it to");
    }

    [Fact]
    public async Task EnsureSchemaAsync_EmptyDatabase_AppliesEveryMigrationWithSeeds()
    {
        // Act
        await RunSchemaUpgraderAsync();

        // Assert - the whole migration chain is recorded and nothing is pending
        await using var context = _factory.CreateDbContext();
        var applied = await context.Database.GetAppliedMigrationsAsync();
        applied.Should().Equal(context.Database.GetMigrations());
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

        // The schema came from the migrations, seeds included: every mapped
        // entity set is queryable and the HasData rows are present.
        (await context.Events.CountAsync()).Should().Be(0);
        (await context.Captures.CountAsync()).Should().Be(0);
        (await context.EraCategories.CountAsync()).Should().Be(7);
        (await context.AppSettings.AnyAsync(s => s.SettingKey == "theme")).Should().BeTrue();

        await using var connection = new SqliteConnection(RawConnectionString);
        await connection.OpenAsync();
        (await ReadColumnAsync(connection, $"SELECT \"MigrationId\" FROM \"{HistoryTable}\" ORDER BY \"MigrationId\""))
            .Should().Contain(SchemaUpgrader.BaselineMigrationId);
    }

    [Fact]
    public async Task EnsureSchemaAsync_EnsureCreatedEraDatabase_IsBaselinedThenMigratedForward()
    {
        // Arrange - a database exactly as the EnsureCreated-based startup of
        // the baseline era left it: the InitialCreate shape (EnsureCreated and
        // the migration generate the same DDL from the same model snapshot),
        // seeds included, and no migrations history at all.
        await using (var context = _factory.CreateDbContext())
        {
            await context.GetService<IMigrator>().MigrateAsync(targetMigration: SchemaUpgrader.BaselineMigrationId);
        }

        await using (var connection = new SqliteConnection(RawConnectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, $"DROP TABLE \"{HistoryTable}\";");
            await ExecuteAsync(connection, "DROP TABLE IF EXISTS \"__EFMigrationsLock\";");
            (await GetTableNamesAsync(connection)).Should().NotContain(HistoryTable);
        }

        var indexSqlBefore = await GetIndexSqlAsync("IX_people_name");

        // Act
        await RunSchemaUpgraderAsync();

        // Assert - labelled with the baseline, then migrated forward to the end
        await using (var context = _factory.CreateDbContext())
        {
            var applied = (await context.Database.GetAppliedMigrationsAsync()).ToList();
            applied.Should().Contain(SchemaUpgrader.BaselineMigrationId);
            applied.Should().Equal(context.Database.GetMigrations());
            (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

            // Every mapped column exists: a post-baseline migration has been
            // applied on top of the baselined shape.
            (await context.Captures.CountAsync()).Should().Be(0);
            (await context.EraCategories.CountAsync()).Should().Be(7, "the baseline's seed rows survived untouched");
        }

        // The EnsureCreated-era shape already carries NOCASE on the column, so
        // the identity repair found nothing to rebuild.
        (await GetIndexSqlAsync("IX_people_name")).Should().Be(indexSqlBefore);
        (await GetTableSqlAsync("people")).Should().Contain("COLLATE NOCASE");

        // A second start is a no-op: same history, same schema.
        var schemaAfterFirstRun = await SnapshotSchemaAsync();
        await RunSchemaUpgraderAsync();
        (await SnapshotSchemaAsync()).Should().Equal(schemaAfterFirstRun);
    }

    [Fact]
    public async Task EnsureSchemaAsync_OldShapePreNocaseDatabase_IsRepairedBaselinedAndMigrated()
    {
        // Arrange - a database from a build before the NOCASE identity work
        // and before the sync tables: BINARY unique name indexes holding a
        // case-variant duplicate, events without the precision columns, no
        // captures table, no migrations history.
        await using (var connection = new SqliteConnection(RawConnectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection,
                """
                CREATE TABLE "app_settings" (
                    "setting_key" TEXT NOT NULL CONSTRAINT "PK_app_settings" PRIMARY KEY,
                    "setting_value" TEXT NOT NULL,
                    "updated_at" TEXT NOT NULL
                );
                CREATE TABLE "eras" (
                    "era_id" TEXT NOT NULL CONSTRAINT "PK_eras" PRIMARY KEY,
                    "name" TEXT NOT NULL,
                    "start_date" TEXT NOT NULL,
                    "end_date" TEXT NULL,
                    "color" TEXT NOT NULL,
                    "description" TEXT NULL,
                    "created_at" TEXT NOT NULL,
                    "updated_at" TEXT NOT NULL
                );
                CREATE TABLE "events" (
                    "event_id" TEXT NOT NULL CONSTRAINT "PK_events" PRIMARY KEY,
                    "title" TEXT NOT NULL,
                    "description" TEXT NULL,
                    "start_date" TEXT NOT NULL,
                    "end_date" TEXT NULL,
                    "category" TEXT NOT NULL,
                    "era_id" TEXT NULL,
                    "raw_transcript" TEXT NULL,
                    "audio_file_path" TEXT NULL,
                    "created_at" TEXT NOT NULL,
                    "updated_at" TEXT NOT NULL
                );
                CREATE TABLE "people" (
                    "person_id" TEXT NOT NULL CONSTRAINT "PK_people" PRIMARY KEY,
                    "name" TEXT NOT NULL,
                    "created_at" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX "IX_people_name" ON "people" ("name");
                CREATE TABLE "event_people" (
                    "event_id" TEXT NOT NULL,
                    "person_id" TEXT NOT NULL,
                    CONSTRAINT "PK_event_people" PRIMARY KEY ("event_id", "person_id")
                );
                CREATE TABLE "tags" (
                    "tag_id" TEXT NOT NULL CONSTRAINT "PK_tags" PRIMARY KEY,
                    "tag_name" TEXT NOT NULL,
                    "created_at" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX "IX_tags_tag_name" ON "tags" ("tag_name");
                CREATE TABLE "locations" (
                    "location_id" TEXT NOT NULL CONSTRAINT "PK_locations" PRIMARY KEY,
                    "name" TEXT NOT NULL,
                    "created_at" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX "IX_locations_name" ON "locations" ("name");
                CREATE TABLE "recording_queue" (
                    "queue_id" TEXT NOT NULL CONSTRAINT "PK_recording_queue" PRIMARY KEY,
                    "audio_file_path" TEXT NOT NULL,
                    "status" TEXT NOT NULL,
                    "duration_seconds" REAL NULL,
                    "file_size_bytes" INTEGER NULL,
                    "created_at" TEXT NOT NULL,
                    "processed_at" TEXT NULL,
                    "error_message" TEXT NULL
                );
                INSERT INTO "people" VALUES ('person-upper', 'Sarah', '2024-01-01 00:00:00');
                INSERT INTO "people" VALUES ('person-lower', 'sarah', '2024-02-01 00:00:00');
                INSERT INTO "event_people" VALUES ('event-1', 'person-upper');
                INSERT INTO "event_people" VALUES ('event-2', 'person-lower');
                INSERT INTO "event_people" VALUES ('event-3', 'person-lower');
                """);
        }

        // Act
        await RunSchemaUpgraderAsync();

        // Assert - repaired first (the duplicate is merged, the index is
        // NOCASE, the drift columns and tables exist), then labelled with the
        // baseline, then migrated forward.
        await using (var context = _factory.CreateDbContext())
        {
            var applied = (await context.Database.GetAppliedMigrationsAsync()).ToList();
            applied.Should().Contain(SchemaUpgrader.BaselineMigrationId);
            applied.Should().Equal(context.Database.GetMigrations());
            (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

            // The repair added the precision columns to the old events table
            // and created the captures table; a later migration altered that
            // repair-created table. All of it is queryable through the model.
            (await context.Events.CountAsync()).Should().Be(0);
            (await context.Captures.CountAsync()).Should().Be(0);
            (await context.People.CountAsync()).Should().Be(1, "the case-variant duplicate was merged");
            (await context.AppSettings.AnyAsync(s => s.SettingKey == "theme")).Should().BeTrue("the settings backfill ran");
        }

        // The documented difference between a baselined database and the
        // migration-built one: NOCASE lives on the rebuilt INDEX, the column
        // keeps its original (BINARY) collation.
        (await GetIndexSqlAsync("IX_people_name")).Should().Contain("COLLATE NOCASE");
        (await GetTableSqlAsync("people")).Should().NotContain("NOCASE");

        // A second start is a no-op: same history, same schema, same rows.
        var schemaAfterFirstRun = await SnapshotSchemaAsync();
        await RunSchemaUpgraderAsync();
        (await SnapshotSchemaAsync()).Should().Equal(schemaAfterFirstRun);
        await using (var context = _factory.CreateDbContext())
        {
            (await context.People.CountAsync()).Should().Be(1);
        }
    }

    [Fact]
    public async Task EnsureSchemaAsync_RunTwiceOnAFreshDatabase_IsANoOp()
    {
        // Arrange
        await RunSchemaUpgraderAsync();
        var schemaAfterFirstRun = await SnapshotSchemaAsync();
        var historyAfterFirstRun = await ReadHistoryAsync();

        // Act
        var secondRun = RunSchemaUpgraderAsync;
        await secondRun.Should().NotThrowAsync();

        // Assert
        (await SnapshotSchemaAsync()).Should().Equal(schemaAfterFirstRun);
        (await ReadHistoryAsync()).Should().Equal(historyAfterFirstRun);
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

    /// <summary>Every object's definition, so two schema states can be compared whole.</summary>
    private async Task<List<string>> SnapshotSchemaAsync()
    {
        await using var connection = new SqliteConnection(RawConnectionString);
        await connection.OpenAsync();
        return await ReadColumnAsync(connection,
            "SELECT type || ' ' || name || ' ' || COALESCE(sql, '') FROM sqlite_master ORDER BY type, name");
    }

    private async Task<List<string>> ReadHistoryAsync()
    {
        await using var connection = new SqliteConnection(RawConnectionString);
        await connection.OpenAsync();
        return await ReadColumnAsync(connection,
            $"SELECT \"MigrationId\" || ' ' || \"ProductVersion\" FROM \"{HistoryTable}\" ORDER BY \"MigrationId\"");
    }

    private async Task<string?> GetIndexSqlAsync(string indexName)
    {
        await using var connection = new SqliteConnection(RawConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name=$name";
        command.Parameters.AddWithValue("$name", indexName);
        return (await command.ExecuteScalarAsync()) as string;
    }

    private async Task<string?> GetTableSqlAsync(string tableName)
    {
        await using var connection = new SqliteConnection(RawConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", tableName);
        return (await command.ExecuteScalarAsync()) as string;
    }

    private static async Task<HashSet<string>> GetTableNamesAsync(SqliteConnection connection)
    {
        var names = await ReadColumnAsync(connection, "SELECT name FROM sqlite_master WHERE type='table'");
        return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<List<string>> ReadColumnAsync(SqliteConnection connection, string sql)
    {
        var values = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    #endregion
}
