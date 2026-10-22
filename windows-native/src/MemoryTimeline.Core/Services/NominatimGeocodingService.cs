using MemoryTimeline.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Geocoding via OpenStreetMap Nominatim (jsonv2 search API). Registered as a
/// typed HttpClient (<c>AddHttpClient&lt;IGeocodingService, NominatimGeocodingService&gt;</c>,
/// same pattern as OpenAIEmbeddingService).
///
/// Privacy contract: every entry point re-reads
/// <see cref="SettingKeys.GeocodingEnabled"/> (OFF by default) and throws
/// BEFORE any request is built or sent - with the toggle off, zero bytes
/// reach the network and no place name leaves the machine.
///
/// Nominatim usage policy: a descriptive User-Agent is mandatory and batch
/// traffic must stay at or below one request per second, hence the delay
/// between batch lookups.
/// </summary>
public class NominatimGeocodingService : IGeocodingService
{
    private const string SearchUrl = "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&q=";
    private const string UserAgent = "MemoryTimeline/1.0";

    /// <summary>Milliseconds between batch requests (Nominatim allows at most 1 req/s).</summary>
    private const int BatchDelayMs = 1100;

    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<NominatimGeocodingService> _logger;
    private readonly Polly.ResiliencePipeline<HttpResponseMessage> _retry;

    public NominatimGeocodingService(
        HttpClient httpClient,
        ISettingsService settingsService,
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<NominatimGeocodingService> logger)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _contextFactory = contextFactory;
        _logger = logger;
        // Transport failures and 5xx are retried; 429 is NOT - the Nominatim
        // usage policy asks a rate-limited client to back off, and the batch
        // loop already spaces requests at 1.1 s.
        _retry = HttpRetryPolicy.CreateForResponses(_logger, "Nominatim geocode", retryTooManyRequests: false);

        // Required by the Nominatim usage policy; requests without an
        // identifying User-Agent are rejected.
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }
    }

    /// <inheritdoc />
    public async Task<(double Latitude, double Longitude, string DisplayName)?> GeocodeAsync(
        string placeName,
        CancellationToken ct = default)
    {
        await EnsureEnabledAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(placeName))
        {
            return null;
        }

        var url = SearchUrl + Uri.EscapeDataString(placeName.Trim());

        using var response = await _retry.ExecuteAsync(
            async token => await _httpClient.GetAsync(url, token).ConfigureAwait(false), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return null; // no match for this place name
            }

            var first = root[0];
            // Nominatim serializes lat/lon as strings.
            var latitude = double.Parse(first.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture);
            var longitude = double.Parse(first.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture);
            var displayName = first.TryGetProperty("display_name", out var displayElement)
                ? displayElement.GetString() ?? placeName
                : placeName;

            if (latitude < -90.0 || latitude > 90.0 || longitude < -180.0 || longitude > 180.0)
            {
                _logger.LogWarning(
                    "Nominatim returned out-of-range coordinates ({Latitude}, {Longitude}) for '{Place}'; treating as no match.",
                    latitude, longitude, placeName);
                return null;
            }

            return (latitude, longitude, displayName);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogError(ex, "Could not parse the Nominatim response for '{Place}'.", placeName);
            throw new InvalidOperationException(
                $"The geocoding service returned an unexpected response for '{placeName}'.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> GeocodeLocationAsync(string locationId, CancellationToken ct = default)
    {
        await EnsureEnabledAsync().ConfigureAwait(false);

        string name;
        await using (var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var location = await context.Locations.AsNoTracking()
                .FirstOrDefaultAsync(l => l.LocationId == locationId, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Location not found: {locationId}");
            name = location.Name;
        }

        var result = await GeocodeAsync(name, ct).ConfigureAwait(false);
        if (result == null)
        {
            _logger.LogInformation("Nominatim found no match for location '{Name}' ({LocationId}).", name, locationId);
            return false;
        }

        await PersistGeocodeResultAsync(locationId, result.Value, ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> GeocodeMissingAsync(
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        await EnsureEnabledAsync().ConfigureAwait(false);

        List<(string LocationId, string Name)> unlocated;
        await using (var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            unlocated = (await context.Locations.AsNoTracking()
                    .Where(l => l.Latitude == null || l.Longitude == null)
                    .OrderBy(l => l.Name)
                    .Select(l => new { l.LocationId, l.Name })
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .Select(l => (l.LocationId, l.Name))
                .ToList();
        }

        var done = 0;
        var succeeded = 0;

        foreach (var (locationId, name) in unlocated)
        {
            ct.ThrowIfCancellationRequested();

            if (done > 0)
            {
                // Nominatim usage policy: at most one request per second.
                await Task.Delay(BatchDelayMs, ct).ConfigureAwait(false);
            }

            try
            {
                var result = await GeocodeAsync(name, ct).ConfigureAwait(false);
                if (result != null)
                {
                    await PersistGeocodeResultAsync(locationId, result.Value, ct).ConfigureAwait(false);
                    succeeded++;
                }
                else
                {
                    _logger.LogInformation("Nominatim found no match for '{Name}' ({LocationId}).", name, locationId);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Geocoding is disabled", StringComparison.Ordinal))
            {
                // The user turned the toggle off mid-batch: stop immediately.
                throw;
            }
            catch (Exception ex)
            {
                // One failed lookup (network hiccup, malformed response) must
                // not abort the rest of the batch.
                _logger.LogWarning(ex, "Geocoding failed for '{Name}' ({LocationId}); continuing with the next place.", name, locationId);
            }

            done++;
            progress?.Report((done, unlocated.Count));
        }

        _logger.LogInformation("Geocoding batch finished: {Succeeded} of {Total} unlocated place(s) resolved.", succeeded, unlocated.Count);
        return succeeded;
    }

    /// <summary>
    /// Persists a geocode hit in one write: coordinates, the canonical display
    /// name (truncated to its column length) and the GeocodedAt stamp.
    /// </summary>
    private async Task PersistGeocodeResultAsync(
        string locationId,
        (double Latitude, double Longitude, string DisplayName) result,
        CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var location = await context.Locations
            .FirstOrDefaultAsync(l => l.LocationId == locationId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Location not found: {locationId}");

        location.Latitude = result.Latitude;
        location.Longitude = result.Longitude;
        location.CanonicalName = result.DisplayName.Length <= 300
            ? result.DisplayName
            : result.DisplayName[..300];
        location.GeocodedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Geocoded location '{Name}' ({LocationId}) to ({Latitude}, {Longitude}).",
            location.Name, locationId, result.Latitude, result.Longitude);
    }

    /// <summary>
    /// The privacy gate: throws while <see cref="SettingKeys.GeocodingEnabled"/>
    /// is anything but "true". Runs BEFORE any request is built, so a disabled
    /// service performs zero network calls.
    /// </summary>
    private async Task EnsureEnabledAsync()
    {
        var enabled = await _settingsService
            .GetSettingAsync<string>(SettingKeys.GeocodingEnabled, "false")
            .ConfigureAwait(false);

        if (!string.Equals(enabled?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Geocoding is disabled. Turn it on from the Map page to look up place coordinates online.");
        }
    }
}
