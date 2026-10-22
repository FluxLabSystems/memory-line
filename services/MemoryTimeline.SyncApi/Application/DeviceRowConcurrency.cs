using MemoryTimeline.SyncApi.Domain;
using MemoryTimeline.SyncApi.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// Saves a change to a device row in the face of concurrent refresh rotations.
///
/// <c>Device.RefreshTokenHash</c> is an EF concurrency token so two rotations
/// cannot silently clobber each other — but the token guards <em>every</em>
/// update of the row, so an ack or a revoke that loads the device, waits on a
/// network round trip, and saves after a refresh rotated the hash fails with
/// <see cref="DbUpdateConcurrencyException"/> too. Only the refresh path handled
/// that; everywhere else it surfaced as a retryable 500 for an operation that
/// had nothing to do with tokens. This helper gives those callers the same
/// reload-and-retry the refresh path has: the mutation is re-applied to a
/// freshly loaded row, so the rotated hash is kept and the caller's change
/// lands on top of it.
/// </summary>
public static class DeviceRowConcurrency
{
    /// <summary>Attempts before giving up; a third collision in a row is a bug, not contention.</summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// Loads the device, applies <paramref name="mutate"/>, and saves; on a
    /// concurrency conflict the tracker is cleared and the whole step repeats
    /// against the row as it now is. Returns the saved row, or null when no
    /// such device exists. The last attempt's conflict propagates.
    /// </summary>
    public static async Task<Device?> SaveAsync(
        SyncDbContext db,
        string deviceId,
        Action<Device> mutate,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);
            if (device is null)
            {
                return null;
            }

            mutate(device);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return device;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}
