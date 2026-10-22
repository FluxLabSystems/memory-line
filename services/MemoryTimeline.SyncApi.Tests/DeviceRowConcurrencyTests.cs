using FluentAssertions;
using MemoryTimeline.SyncApi.Application;
using MemoryTimeline.SyncApi.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MemoryTimeline.SyncApi.Tests;

/// <summary>
/// The device row's refresh hash is a concurrency token, so any save of that
/// row that races a token rotation throws. These tests stage that race
/// deterministically — the concurrent rotation happens inside the mutation
/// callback, between the helper's load and its save — and check that the
/// helper lands the caller's change on top of the rotated row rather than
/// failing, and that it gives up after a bounded number of collisions.
/// </summary>
public class DeviceRowConcurrencyTests : IClassFixture<SyncApiFixture>
{
    private readonly SyncApiFixture _fixture;

    public DeviceRowConcurrencyTests(SyncApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SaveAsync_RacingATokenRotation_ReloadsAndKeepsBothChanges()
    {
        var device = await _fixture.RegisterDeviceAsync("ios", "iPhone");
        var factory = _fixture.Services.GetRequiredService<IDbContextFactory<SyncDbContext>>();

        var attempts = 0;
        await using var db = await factory.CreateDbContextAsync();
        var saved = await DeviceRowConcurrency.SaveAsync(
            db,
            device.DeviceId,
            row =>
            {
                attempts++;
                if (attempts == 1)
                {
                    // A refresh rotates the hash after this call loaded the row
                    // and before it saves — exactly the ack-vs-refresh race.
                    using var other = factory.CreateDbContext();
                    var concurrent = other.Devices.Single(d => d.DeviceId == device.DeviceId);
                    concurrent.RefreshTokenHash = "rotated-by-a-concurrent-refresh";
                    other.SaveChanges();
                }

                row.AckedCursor = 42;
            },
            CancellationToken.None);

        attempts.Should().Be(2, "the first save collided and the mutation was re-applied to a reloaded row");
        saved.Should().NotBeNull();
        saved!.AckedCursor.Should().Be(42);

        await using var check = await factory.CreateDbContextAsync();
        var stored = await check.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == device.DeviceId);
        stored.AckedCursor.Should().Be(42, "the caller's change landed");
        stored.RefreshTokenHash.Should().Be("rotated-by-a-concurrent-refresh", "the rotation was not clobbered");
    }

    [Fact]
    public async Task SaveAsync_CollidingOnEveryAttempt_PropagatesTheLastConflict()
    {
        var device = await _fixture.RegisterDeviceAsync("ios", "iPhone");
        var factory = _fixture.Services.GetRequiredService<IDbContextFactory<SyncDbContext>>();

        var attempts = 0;
        await using var db = await factory.CreateDbContextAsync();
        var act = () => DeviceRowConcurrency.SaveAsync(
            db,
            device.DeviceId,
            row =>
            {
                attempts++;
                using var other = factory.CreateDbContext();
                var concurrent = other.Devices.Single(d => d.DeviceId == device.DeviceId);
                concurrent.RefreshTokenHash = $"rotation-{attempts}";
                other.SaveChanges();
                row.AckedCursor = attempts;
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "a collision on every attempt is a bug to surface, not contention to hide");
        attempts.Should().Be(DeviceRowConcurrency.MaxAttempts);
    }

    [Fact]
    public async Task SaveAsync_UnknownDevice_ReturnsNullWithoutSaving()
    {
        var factory = _fixture.Services.GetRequiredService<IDbContextFactory<SyncDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        var saved = await DeviceRowConcurrency.SaveAsync(
            db, Guid.NewGuid().ToString("D"), _ => throw new InvalidOperationException("must not be called"),
            CancellationToken.None);

        saved.Should().BeNull();
    }
}
