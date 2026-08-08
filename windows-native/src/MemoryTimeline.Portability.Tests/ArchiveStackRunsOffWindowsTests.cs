using FluentAssertions;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MemoryTimeline.Portability.Tests;

/// <summary>
/// Proves the archive stack <b>runs</b> on a non-Windows machine, not merely
/// that it compiles there.
///
/// <para><b>Why this exists.</b> macOS is intended to be a peer of Windows — a
/// full desktop app the iOS companion can feed — not a reader of a projection
/// Windows publishes. That target has one load-bearing question underneath it:
/// does the business layer have to be reimplemented in Swift, or can the Mac run
/// the same C# Windows runs? `docs/design/MACOS-PORT-PLAN.md` framed it as a
/// three-way choice and deferred it, and the honest reason it stayed deferred is
/// that nobody had evidence either way.</para>
///
/// <para>The macOS workflow's `dotnet` job already <i>builds</i>
/// Core/Data/Sync on a Mac. Building proves no Windows-only <i>type</i> crept in;
/// it says nothing about whether EF Core can open a SQLite file, whether
/// `SchemaUpgrader` can raise the real schema, or whether a service that works
/// on Windows behaves the same elsewhere. Any of those could fail on a native
/// asset or a runtime assumption while the build stays green.</para>
///
/// <para><b>What a failure here means.</b> Not "fix the test" — it means the
/// plan to run the archive in-process on macOS is wrong, and the reimplement-in-
/// Swift option is back on the table. Treat a red run as an architectural
/// finding.</para>
///
/// <para>Deliberately narrow: schema, one write path, one read path, and the
/// date-precision formatter. Those are the pieces every other feature stands on,
/// and the ones a Swift reimplementation would have had to reproduce first.
/// Breadth belongs in MemoryTimeline.Tests, which runs on Windows.</para>
/// </summary>
public class ArchiveStackRunsOffWindowsTests : IDisposable
{
    private readonly string _databasePath;
    private readonly TestContextFactory _factory;

    public ArchiveStackRunsOffWindowsTests()
    {
        // A real file, not in-memory and not the EF InMemory provider: the point
        // is to exercise the native SQLite library on this platform. An
        // in-memory provider would pass on a machine where the native asset is
        // missing entirely.
        _databasePath = Path.Combine(
            Path.GetTempPath(), $"portability-{Guid.NewGuid():N}.db");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            // Pooling=False so the file is unlocked by the time Dispose deletes
            // it — the same hazard that made RecallPromptSchemaTests flaky.
            .UseSqlite($"Data Source={_databasePath};Pooling=False")
            .Options;
        _factory = new TestContextFactory(options);
    }

    [Fact]
    public async Task SchemaUpgrader_OnThisPlatform_RaisesTheWholeArchiveSchema()
    {
        await using (var context = _factory.CreateDbContext())
        {
            await SchemaUpgrader.EnsureSchemaAsync(context);
        }

        await using var probe = _factory.CreateDbContext();
        var connection = probe.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        var tables = Convert.ToInt32(await command.ExecuteScalarAsync());

        // A floor, not an exact count: the schema grows, and pinning the number
        // would make every new table a failing test in this file for no reason.
        // The floor still catches the failure that matters — EnsureCreated
        // silently producing nothing.
        tables.Should().BeGreaterThan(20,
            "the archive schema is the whole product; a handful of tables means it did not really run");
    }

    [Fact]
    public async Task EventService_OnThisPlatform_WritesAndReadsBackThroughTheRealRepository()
    {
        await using (var context = _factory.CreateDbContext())
        {
            await SchemaUpgrader.EnsureSchemaAsync(context);
        }

        var service = new EventService(
            new EventRepository(_factory), _factory, NullLogger<EventService>.Instance);

        var created = await service.CreateEventAsync(new Event
        {
            Title = "Ferry to the island",
            StartDate = new DateTime(2003, 7, 14),
            DatePrecision = DatePrecision.Season,
            Category = EventCategory.Travel,
        });

        created.Should().NotBeNull();
        var all = await service.GetAllEventsAsync();
        all.Should().ContainSingle(e => e.Title == "Ferry to the island");
    }

    /// <summary>
    /// The single most-copied piece of logic in the product, and the one a Swift
    /// reimplementation would have had to reproduce exactly. Every client renders
    /// the string this produces rather than formatting a date itself, precisely
    /// so a memory the user dated to a summer is never shown as a precise day.
    /// </summary>
    [Fact]
    public void DateDisplay_OnThisPlatform_FormatsPrecisionHonestly()
    {
        DateDisplay.FormatPrecise(new DateTime(2003, 7, 14), DatePrecision.Season)
            .Should().Be("Summer 2003");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a run over.
        }
        GC.SuppressFinalize(this);
    }

    private sealed class TestContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
