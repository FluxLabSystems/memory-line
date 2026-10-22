using FluentAssertions;
using MemoryTimeline.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MemoryTimeline.Portability.Tests;

/// <summary>
/// Startup now raises the schema through EF Core migrations
/// (<see cref="SchemaUpgrader.EnsureSchemaAsync"/>), so the migrator and the
/// migrations assembly are part of what has to <b>run</b> on a non-Windows
/// machine, not merely compile there. Two checks: the migration chain applies
/// against the native SQLite library on this platform and leaves nothing
/// pending, and the model carries no change outside a migration (MigrateAsync
/// refuses to run otherwise, which would fail every start on every platform).
/// </summary>
public class MigrationsRunOffWindowsTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DbContextOptions<AppDbContext> _options;

    public MigrationsRunOffWindowsTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"portability-migrations-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=False")
            .Options;
    }

    [Fact]
    public async Task SchemaUpgrader_OnThisPlatform_AppliesEveryMigrationAndLeavesNonePending()
    {
        await using (var context = new AppDbContext(_options))
        {
            await SchemaUpgrader.EnsureSchemaAsync(context);
        }

        await using var probe = new AppDbContext(_options);
        var applied = await probe.Database.GetAppliedMigrationsAsync();
        applied.Should().Equal(probe.Database.GetMigrations(),
            "a new install is built by the migration chain, baseline first");
        (await probe.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public void Model_OnThisPlatform_HasNoChangesOutsideAMigration()
    {
        using var context = new AppDbContext(_options);
        context.Database.HasPendingModelChanges().Should().BeFalse(
            "a model change without `dotnet ef migrations add` makes MigrateAsync throw at startup");
    }

    public void Dispose()
    {
        try
        {
            foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a run over.
        }
        GC.SuppressFinalize(this);
    }
}
