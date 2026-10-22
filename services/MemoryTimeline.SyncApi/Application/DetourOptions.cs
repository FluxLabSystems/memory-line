namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// Configuration for detour generation, bound from the "Detours" section.
/// Design §22 decision 5: place lookups use Google by default with request
/// caps sized to stay inside the free tier, and Apple (MapKit) is selectable
/// as the primary provider. Nothing here has a secret default — with an
/// empty section the service runs with <see cref="DetourProviderNames.None"/>
/// and <c>POST /trips/{id}/detours/generate</c> honestly yields nothing.
/// </summary>
public sealed class DetourOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Detours";

    /// <summary>
    /// Which adapter answers generation: <c>none</c> (default), <c>google</c>
    /// (Places API (New) nearby search) or <c>apple</c> (Apple Maps Server API
    /// search). Case-insensitive.
    /// </summary>
    public string Provider { get; set; } = DetourProviderNames.None;

    /// <summary>
    /// The provider credential: a Google Maps Platform API key for
    /// <c>google</c>, or the Apple Maps Server API authorization token (the
    /// developer-signed JWT that is exchanged for short-lived access tokens)
    /// for <c>apple</c>. Required for either; never logged.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Maximum provider search requests per UTC day across all trips. Once
    /// reached, generation yields nothing until the day rolls over, and a
    /// warning is logged — the "cap sized for the free tier" of design §22
    /// decision 5. 0 disables the cap. Token exchanges (Apple) do not count.
    /// </summary>
    public int DailyRequestCap { get; set; } = 150;

    /// <summary>Most candidates returned per generation, nearest first.</summary>
    public int MaxCandidates { get; set; } = 5;

    /// <summary>
    /// Radius around the trip's latest position that is searched, in metres.
    /// Approximates the route corridor of design §10.2 with a circle, because
    /// a snapshot carries a point and a heading, not a polyline.
    /// </summary>
    public double SearchRadiusMeters { get; set; } = 15_000;

    /// <summary>Per-request timeout for provider calls, in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Google place types (Places API (New) "Table A" values) searched for.
    /// The default covers the design's detour categories (§10.2) with types
    /// that exist in that table; an unknown type fails the whole request.
    /// </summary>
    public string[] GooglePlaceTypes { get; set; } =
    [
        "tourist_attraction",
        "museum",
        "historical_landmark",
        "park",
        "national_park",
        "hiking_area",
        "art_gallery",
        "visitor_center",
        "cafe",
        "restaurant",
    ];

    /// <summary>
    /// Apple point-of-interest categories (<c>includePoiCategories</c> values
    /// of the Maps Server API search) the search is limited to.
    /// </summary>
    public string[] ApplePoiCategories { get; set; } =
    [
        "Museum",
        "Park",
        "NationalPark",
        "AmusementPark",
        "Aquarium",
        "Zoo",
        "Beach",
        "Winery",
        "Brewery",
        "Cafe",
        "Bakery",
        "Restaurant",
    ];

    /// <summary>
    /// Free-text query the Apple search is driven by; the Maps Server API
    /// search is query-based and has no "everything in these categories"
    /// form, so one query per generation keeps the request count predictable.
    /// </summary>
    public string AppleSearchQuery { get; set; } = "attractions";

    /// <summary>The configured <see cref="Provider"/>, normalized for comparison.</summary>
    public string NormalizedProvider => (Provider ?? string.Empty).Trim().ToLowerInvariant();
}

/// <summary>Accepted values of <see cref="DetourOptions.Provider"/>.</summary>
public static class DetourProviderNames
{
    public const string None = "none";
    public const string Google = "google";
    public const string Apple = "apple";

    public static readonly string[] All = [None, Google, Apple];
}
