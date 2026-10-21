using System.Data.Common;
using System.Reflection;
using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace MemoryTimeline.Tests.Integration;

/// <summary>
/// The three-way seed parity rule for app_settings, checked as a whole rather
/// than per feature: the keys <see cref="SchemaUpgrader"/> backfills with
/// INSERT OR IGNORE, the keys <c>AppDbContext.SeedDefaultSettings</c> seeds
/// through HasData, and the <see cref="SettingKeys"/> constants must name the
/// same set. Also pins the clean-up of seeded keys that nothing ever read, and
/// the promise that a backfill never overwrites a user's row.
/// </summary>
public class SettingsSeedParityTests : IDisposable
{
    /// <summary>Seeded in older builds, read by nothing, no longer seeded anywhere.</summary>
    private static readonly string[] DroppedKeys =
    {
        "audio_quality", "stt_engine", "stt_config", "rag_auto_run_enabled", "rag_schedule",
        "send_transcripts_only", "require_confirmation",
    };

    /// <summary>
    /// Constants that are deliberately NOT seeded: an absent row carries meaning.
    /// </summary>
    private static readonly string[] UnseededKeys =
    {
        // The Anthropic key is never seeded: an absent row is "not configured",
        // and seeding an empty secret would only create a row to protect.
        SettingKeys.AnthropicApiKey,
        SettingKeys.LastToastDate,
        SettingKeys.LastBackupAt,
        SettingKeys.TimelineProjectionBackfillPending,
        SettingKeys.SyncEnabled, SettingKeys.SyncServerUrl, SettingKeys.SyncDeviceId,
        SettingKeys.SyncDeviceDisplayName, SettingKeys.SyncAccessToken, SettingKeys.SyncRefreshToken,
        SettingKeys.SyncCursor, SettingKeys.SyncAutoProcess,
    };

    private readonly List<(TestDbContextFactory Factory, string DatabasePath)> _databases = new();

    [Fact]
    public async Task SchemaUpgraderBackfill_HasDataSeed_AndSettingKeys_NameTheSameKeys()
    {
        // Arrange - an old-shape app_settings with one user-changed row and one
        // obsolete seeded row, so the backfill has something to leave alone
        var factory = CreateFactory();
        await using (var setupContext = factory.CreateDbContext())
        {
            var connection = setupContext.Database.GetDbConnection();
            await connection.OpenAsync();
            await ExecuteAsync(connection,
                """
                CREATE TABLE "app_settings" (
                    "setting_key" TEXT NOT NULL CONSTRAINT "PK_app_settings" PRIMARY KEY,
                    "setting_value" TEXT NOT NULL,
                    "updated_at" TEXT NOT NULL
                );
                """);
            await ExecuteAsync(connection,
                "INSERT INTO \"app_settings\" VALUES ('theme', 'dark', '2026-01-01 00:00:00');");
            await ExecuteAsync(connection,
                "INSERT INTO \"app_settings\" VALUES ('audio_quality', 'high', '2025-01-21 00:00:00');");
        }

        // Act
        await using (var upgradeContext = factory.CreateDbContext())
        {
            await SchemaUpgrader.EnsureSchemaAsync(upgradeContext);
        }

        // Assert
        await using var context = factory.CreateDbContext();
        var backfilled = await context.AppSettings.AsNoTracking()
            .ToDictionaryAsync(s => s.SettingKey, s => s.SettingValue);

        var hasDataSeed = HasDataSeed(context);
        var constants = SettingKeyConstants();

        // (1) the backfill wrote exactly the HasData keys (plus the two rows it found)
        backfilled.Keys.Except(new[] { "audio_quality" }).Should().BeEquivalentTo(hasDataSeed.Keys,
            "SchemaUpgrader's INSERT OR IGNORE list and AppDbContext.SeedDefaultSettings must stay in sync");

        // (2) with the same default values
        foreach (var (key, value) in hasDataSeed)
        {
            if (key != "theme")
            {
                backfilled[key].Should().Be(value, $"the backfill default for '{key}' must match HasData");
            }
        }

        // (3) every seeded key has a SettingKeys constant, and every constant is
        // either seeded or deliberately unseeded
        hasDataSeed.Keys.Should().BeSubsetOf(constants, "a seeded key without a constant has no safe reader");
        constants.Except(hasDataSeed.Keys).Should().BeEquivalentTo(UnseededKeys,
            "a constant that is neither seeded nor on the deliberately-unseeded list is a parity gap");

        // (4) the user's row survived and the obsolete row was neither re-seeded nor deleted
        backfilled["theme"].Should().Be("dark", "INSERT OR IGNORE never overwrites a user's value");
        backfilled["audio_quality"].Should().Be("high", "existing rows are never deleted, only ignored");

        // (5) dropped keys are seeded nowhere
        hasDataSeed.Keys.Should().NotIntersectWith(DroppedKeys);
        constants.Should().NotIntersectWith(DroppedKeys);
    }

    [Fact]
    public async Task EnsureSchemaAsync_FreshDatabase_SeedsTheConsumedKeysWithTheirDefaults()
    {
        // The four keys that gained readers keep their seeded values, which the
        // readers' fallbacks and the Settings UI both rely on.
        var factory = CreateFactory();
        await using (var context = factory.CreateDbContext())
        {
            await SchemaUpgrader.EnsureSchemaAsync(context);
        }

        await using var verify = factory.CreateDbContext();
        var settings = await verify.AppSettings.AsNoTracking().ToDictionaryAsync(s => s.SettingKey, s => s.SettingValue);
        settings[SettingKeys.LlmMaxTokens].Should().Be("4000");
        settings[SettingKeys.LlmTemperature].Should().Be("0.3");
        settings[SettingKeys.RagSimilarityThreshold].Should().Be("0.75");
        settings[SettingKeys.AutoGenerateEmbeddings].Should().Be("true");
        settings.Keys.Should().NotIntersectWith(DroppedKeys);
    }

    // ---- helpers ----

    private static Dictionary<string, string> HasDataSeed(AppDbContext context)
    {
        // Seed data lives on the design-time model only; the read-optimized
        // runtime model EF hands out by default throws for GetSeedData.
        var model = context.GetService<IDesignTimeModel>().Model;
        var entityType = model.FindEntityType(typeof(AppSetting))!;
        return entityType.GetSeedData()
            .ToDictionary(row => (string)row["SettingKey"]!, row => (string)row["SettingValue"]!);
    }

    private static List<string> SettingKeyConstants() => typeof(SettingKeys)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToList();

    private TestDbContextFactory CreateFactory()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"SettingsSeedParity_{Guid.NewGuid():N}.db");
        var factory = TestDbContextFactory.CreateSqliteFile(databasePath);
        _databases.Add((factory, databasePath));
        return factory;
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        foreach (var (factory, databasePath) in _databases)
        {
            using (var context = factory.CreateDbContext())
            {
                context.Database.EnsureDeleted();
            }

            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var path = databasePath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
