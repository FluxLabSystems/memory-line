using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MemoryTimeline.SyncApi.Domain;
using MemoryTimeline.SyncApi.Infrastructure;
using MemoryTimeline.SyncContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// <see cref="IDeviceService"/> over the sync database. Stateless over the
/// context factory (one short-lived context per operation) and registered as
/// a singleton. Never logs pairing codes, tokens, or token hashes — and never
/// stores raw tokens either: the register idempotency record holds only the
/// non-secret device identity, and replays mint fresh tokens.
/// </summary>
public sealed class DeviceService : IDeviceService
{
    /// <summary>Idempotency scope for the unauthenticated register endpoint (no device ID exists yet).</summary>
    private const string RegisterIdempotencyScope = "__register__";

    /// <summary>
    /// The platform enum of DeviceRegisterRequest/DeviceInfo in the OpenAPI
    /// contract. macos arrived in v1.5 so the Mac app registers as itself;
    /// a new value is added here and to the YAML enums in the same commit.
    /// </summary>
    private static readonly string[] AllowedPlatforms = ["windows", "ios", "macos", "other"];

    private readonly IDbContextFactory<SyncDbContext> _contextFactory;
    private readonly ISyncTokenService _tokens;
    private readonly SyncServerEnvironment _environment;
    private readonly SyncApiOptions _options;
    private readonly ILogger<DeviceService> _logger;

    public DeviceService(
        IDbContextFactory<SyncDbContext> contextFactory,
        ISyncTokenService tokens,
        SyncServerEnvironment environment,
        IOptions<SyncApiOptions> options,
        ILogger<DeviceService> logger)
    {
        _contextFactory = contextFactory;
        _tokens = tokens;
        _environment = environment;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ServiceResult<DeviceRegisterResponse>> RegisterAsync(
        DeviceRegisterRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Client JSON can null out non-nullable DTO strings; normalize before use.
        if (!FixedTimeEquals(request.PairingCode ?? string.Empty, _environment.PairingCode))
        {
            _logger.LogWarning("Device registration rejected: invalid pairing code.");
            return ServiceResult<DeviceRegisterResponse>.Fail(
                StatusCodes.Status401Unauthorized, SyncApiErrorCodes.InvalidPairingCode, "The pairing code is not valid.");
        }

        var platform = (request.Platform ?? string.Empty).Trim().ToLowerInvariant();
        if (!AllowedPlatforms.Contains(platform))
        {
            return ServiceResult<DeviceRegisterResponse>.Fail(
                StatusCodes.Status400BadRequest, SyncApiErrorCodes.ValidationError,
                "platform must be one of: windows, ios, macos, other.");
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Replay of a paired caller's earlier registration (pairing code already
        // validated above, so the caller re-proved pairing). The stored record
        // carries only the device identity — never tokens — so the replay mints
        // a fresh pair and rotates the stored refresh hash.
        if (idempotencyKey is not null)
        {
            var replayed = await TryReplayRegistrationAsync(db, idempotencyKey, cancellationToken);
            if (replayed is not null)
            {
                return replayed;
            }
        }

        var owner = await db.Owners.OrderBy(o => o.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Owner row missing — the database was not initialized at startup.");

        var now = DateTime.UtcNow;
        var refresh = _tokens.CreateRefreshToken();
        var device = new Device
        {
            DeviceId = Guid.NewGuid().ToString("D"),
            OwnerId = owner.OwnerId,
            Platform = platform,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? "Unnamed device" : request.DisplayName.Trim(),
            AppVersion = request.AppVersion,
            PublicKey = request.PublicKey,
            RefreshTokenHash = refresh.TokenHashHex,
            RefreshTokenExpiresAtUtc = refresh.ExpiresAtUtc,
            AckedCursor = 0,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
        };
        var access = _tokens.CreateAccessToken(device.DeviceId, owner.OwnerId);

        var response = new DeviceRegisterResponse
        {
            DeviceId = device.DeviceId,
            OwnerId = owner.OwnerId,
            AccessToken = access.Token,
            AccessTokenExpiresAtUtc = access.ExpiresAtUtc,
            RefreshToken = refresh.Token,
        };

        db.Devices.Add(device);
        if (idempotencyKey is not null)
        {
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                DeviceId = RegisterIdempotencyScope,
                IdempotencyKey = idempotencyKey,
                Endpoint = "POST /api/v1/devices/register",
                StatusCode = StatusCodes.Status201Created,
                // Identity only — token material must never be stored at rest.
                ResponseJson = JsonSerializer.Serialize(
                    new RegisteredDeviceIdentity(device.DeviceId, owner.OwnerId), SyncJson.Options),
                CreatedAtUtc = now,
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (idempotencyKey is not null)
        {
            // Concurrent registration with the same idempotency key: the whole
            // save rolled back (no orphan device); replay the winning request's
            // registration with freshly minted tokens.
            await using var retryDb = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var replayed = await TryReplayRegistrationAsync(retryDb, idempotencyKey, cancellationToken);
            if (replayed is null)
            {
                throw;
            }

            return replayed;
        }

        _logger.LogInformation(
            "Registered device {DeviceId} (platform {Platform}) for owner {OwnerId}.",
            device.DeviceId, device.Platform, owner.OwnerId);
        return ServiceResult<DeviceRegisterResponse>.Ok(response);
    }

    public async Task<ServiceResult<TokenRefreshResponse>> RefreshAsync(
        string deviceId,
        TokenRefreshRequest request,
        CancellationToken cancellationToken)
    {
        // A single 401 shape for unknown devices and bad tokens avoids device enumeration.
        var invalid = ServiceResult<TokenRefreshResponse>.Fail(
            StatusCodes.Status401Unauthorized, SyncApiErrorCodes.RefreshTokenInvalid,
            "The refresh token is not valid for this device.");

        if (!Guid.TryParse(deviceId, out var deviceGuid) || string.IsNullOrEmpty(request.RefreshToken))
        {
            return invalid;
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var canonicalDeviceId = deviceGuid.ToString("D");
        var presentedHash = _tokens.HashRefreshToken(request.RefreshToken);

        var device = await db.Devices.FirstOrDefaultAsync(
            d => d.DeviceId == canonicalDeviceId, cancellationToken);
        var result = EvaluateRefresh(device, presentedHash, out var response);
        if (response is null)
        {
            return result;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent refresh rotated first (RefreshTokenHash is the
            // concurrency token). Reload once and re-evaluate: the loser's
            // presented token now matches the previous hash within the grace
            // window and takes the recovery path; otherwise it fails with 401.
            db.ChangeTracker.Clear();
            device = await db.Devices.FirstOrDefaultAsync(
                d => d.DeviceId == canonicalDeviceId, cancellationToken);
            result = EvaluateRefresh(device, presentedHash, out response);
            if (response is null)
            {
                return result;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Validates the presented refresh token hash against <paramref name="device"/>
    /// and, when valid, mutates the tracked entity with the rotation:
    /// a match on the current hash moves it to the previous slot for the grace
    /// window and installs a new pair; a match on the still-valid previous hash
    /// (crash/lost-response recovery) installs a new pair while keeping the
    /// previous slot untouched. <paramref name="response"/> is null when the
    /// token is rejected; the caller must then return the failure result.
    /// </summary>
    private ServiceResult<TokenRefreshResponse> EvaluateRefresh(
        Device? device, string presentedHash, out TokenRefreshResponse? response)
    {
        response = null;

        // A single 401 shape for unknown devices and bad tokens avoids device enumeration.
        var invalid = ServiceResult<TokenRefreshResponse>.Fail(
            StatusCodes.Status401Unauthorized, SyncApiErrorCodes.RefreshTokenInvalid,
            "The refresh token is not valid for this device.");

        if (device is null || device.RefreshTokenHash.Length == 0)
        {
            return invalid;
        }

        if (device.RevokedAtUtc is not null)
        {
            _logger.LogWarning("Refresh rejected for revoked device {DeviceId}.", device.DeviceId);
            return ServiceResult<TokenRefreshResponse>.Fail(
                StatusCodes.Status401Unauthorized, SyncApiErrorCodes.DeviceRevoked, "This device has been revoked.");
        }

        var now = DateTime.UtcNow;
        if (FixedTimeEquals(presentedHash, device.RefreshTokenHash) && device.RefreshTokenExpiresAtUtc > now)
        {
            var (access, refresh) = RotateCredentials(device, now);
            response = new TokenRefreshResponse
            {
                AccessToken = access.Token,
                AccessTokenExpiresAtUtc = access.ExpiresAtUtc,
                RefreshToken = refresh.Token,
            };
            return ServiceResult<TokenRefreshResponse>.Ok(response);
        }

        if (device.PreviousRefreshTokenHash is not null
            && FixedTimeEquals(presentedHash, device.PreviousRefreshTokenHash)
            && device.PreviousRefreshTokenExpiresAtUtc is { } graceEndsAtUtc
            && now < graceEndsAtUtc)
        {
            // Recovery: the client never received the rotated token. Issue a
            // fresh pair; the previous slot stays as-is so its window still ends
            // on schedule.
            var refresh = _tokens.CreateRefreshToken();
            var access = _tokens.CreateAccessToken(device.DeviceId, device.OwnerId);
            device.RefreshTokenHash = refresh.TokenHashHex;
            device.RefreshTokenExpiresAtUtc = refresh.ExpiresAtUtc;
            device.LastSeenAtUtc = now;
            response = new TokenRefreshResponse
            {
                AccessToken = access.Token,
                AccessTokenExpiresAtUtc = access.ExpiresAtUtc,
                RefreshToken = refresh.Token,
            };
            return ServiceResult<TokenRefreshResponse>.Ok(response);
        }

        _logger.LogWarning("Refresh rejected for device {DeviceId}: stale or expired refresh token.", device.DeviceId);
        return invalid;
    }

    public async Task<ServiceResult<bool>> RevokeAsync(
        Device caller,
        string targetDeviceId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(targetDeviceId, out var targetGuid))
        {
            return ServiceResult<bool>.Fail(
                StatusCodes.Status404NotFound, SyncApiErrorCodes.DeviceUnknown, "No such device.");
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var canonicalTargetId = targetGuid.ToString("D");
        var owned = await db.Devices.AsNoTracking().AnyAsync(
            d => d.DeviceId == canonicalTargetId && d.OwnerId == caller.OwnerId, cancellationToken);
        if (!owned)
        {
            return ServiceResult<bool>.Fail(
                StatusCodes.Status404NotFound, SyncApiErrorCodes.DeviceUnknown, "No such device.");
        }

        // The target may be refreshing its token at this very moment (a lost
        // phone's background sync, say); the refresh hash is a concurrency
        // token, so the save goes through the reload-and-retry helper rather
        // than failing the revocation over a race it must win.
        var revokedNow = false;
        var target = await DeviceRowConcurrency.SaveAsync(
            db,
            canonicalTargetId,
            d =>
            {
                revokedNow = d.RevokedAtUtc is null;
                d.RevokedAtUtc ??= DateTime.UtcNow;
            },
            cancellationToken);
        if (target is null)
        {
            return ServiceResult<bool>.Fail(
                StatusCodes.Status404NotFound, SyncApiErrorCodes.DeviceUnknown, "No such device.");
        }

        if (revokedNow)
        {
            _logger.LogInformation(
                "Device {TargetDeviceId} revoked by device {CallerDeviceId}.", target.DeviceId, caller.DeviceId);
        }

        return ServiceResult<bool>.Ok(true);
    }

    public async Task<ServiceResult<List<DeviceInfoResponse>>> ListAsync(
        Device caller,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var devices = await db.Devices.AsNoTracking()
            .Where(d => d.OwnerId == caller.OwnerId)
            .OrderBy(d => d.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        return ServiceResult<List<DeviceInfoResponse>>.Ok(devices.Select(EntityMappers.ToDeviceInfo).ToList());
    }

    /// <summary>
    /// Replays a stored registration for the given idempotency key: returns the
    /// original device identity with freshly minted tokens (rotating the stored
    /// refresh hash), a 401 if that device has since been revoked, or null when
    /// no usable record exists and registration should proceed normally.
    /// </summary>
    private async Task<ServiceResult<DeviceRegisterResponse>?> TryReplayRegistrationAsync(
        SyncDbContext db, string idempotencyKey, CancellationToken cancellationToken)
    {
        var existing = await db.IdempotencyRecords.FindAsync(
            [RegisterIdempotencyScope, idempotencyKey], cancellationToken);
        if (existing?.ResponseJson is null)
        {
            return null;
        }

        var identity = JsonSerializer.Deserialize<RegisteredDeviceIdentity>(existing.ResponseJson, SyncJson.Options);
        if (identity is null || identity.DeviceId.Length == 0)
        {
            return null;
        }

        var device = await db.Devices.FirstOrDefaultAsync(
            d => d.DeviceId == identity.DeviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        if (device.RevokedAtUtc is not null)
        {
            _logger.LogWarning("Registration replay rejected for revoked device {DeviceId}.", device.DeviceId);
            return ServiceResult<DeviceRegisterResponse>.Fail(
                StatusCodes.Status401Unauthorized, SyncApiErrorCodes.DeviceRevoked, "This device has been revoked.");
        }

        var (access, refresh) = RotateCredentials(device, DateTime.UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Replayed registration of device {DeviceId} with fresh tokens.", device.DeviceId);
        return ServiceResult<DeviceRegisterResponse>.Ok(new DeviceRegisterResponse
        {
            DeviceId = device.DeviceId,
            OwnerId = device.OwnerId,
            AccessToken = access.Token,
            AccessTokenExpiresAtUtc = access.ExpiresAtUtc,
            RefreshToken = refresh.Token,
        });
    }

    /// <summary>
    /// Standard rotation on the tracked entity: the current refresh hash moves
    /// to the previous slot (honored for the configured grace window; 0 keeps
    /// strict rotation) and a freshly minted pair becomes current.
    /// </summary>
    private (AccessTokenResult Access, RefreshTokenResult Refresh) RotateCredentials(Device device, DateTime now)
    {
        var refresh = _tokens.CreateRefreshToken();
        var access = _tokens.CreateAccessToken(device.DeviceId, device.OwnerId);
        device.PreviousRefreshTokenHash = device.RefreshTokenHash;
        device.PreviousRefreshTokenExpiresAtUtc = now.AddSeconds(_options.RefreshTokenGraceSeconds);
        device.RefreshTokenHash = refresh.TokenHashHex;
        device.RefreshTokenExpiresAtUtc = refresh.ExpiresAtUtc;
        device.LastSeenAtUtc = now;
        return (access, refresh);
    }

    private static bool FixedTimeEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    /// <summary>
    /// The non-secret part of a registration stored for idempotent replay.
    /// Deliberately excludes all token material (design §14.5): replays mint
    /// fresh tokens instead of returning a stored credential.
    /// </summary>
    private sealed record RegisteredDeviceIdentity(string DeviceId, string OwnerId);
}
