namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// The small amount of geometry a place adapter needs: distance from the
/// trip's latest position, and the honest, clearly-labelled estimate of what
/// a stop adds to the drive. No provider here returns a routed detour cost
/// (design §10.3 "estimate route delta"), so the estimate is the straight
/// line there and back at an assumed road speed, and
/// <c>verificationSourcesJson.route</c> says so on every candidate.
/// </summary>
public static class DetourGeometry
{
    /// <summary>Mean Earth radius, metres.</summary>
    private const double EarthRadiusMeters = 6_371_000;

    /// <summary>Assumed average road speed for the duration estimate: 50 km/h.</summary>
    public const double AssumedSpeedMetersPerSecond = 13.9;

    /// <summary>What <c>verificationSourcesJson.route</c> carries for every estimate made here.</summary>
    public const string RouteEstimateSource = "straight_line_estimate";

    /// <summary>Great-circle distance between two coordinates, in metres.</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Distance added to the drive by going there and coming back to the route.</summary>
    public static double AddedDistanceMeters(double distanceMeters) => 2 * distanceMeters;

    /// <summary>Time added at the assumed speed.</summary>
    public static double AddedDurationSeconds(double addedDistanceMeters)
        => addedDistanceMeters / AssumedSpeedMetersPerSecond;

    /// <summary>
    /// A latitude/longitude bounding box of <paramref name="radiusMeters"/>
    /// around a point, as (north, east, south, west) degrees — the shape the
    /// Apple Maps Server API's <c>searchRegion</c> takes.
    /// </summary>
    public static (double North, double East, double South, double West) BoundingBox(
        double latitude, double longitude, double radiusMeters)
    {
        var dLat = radiusMeters / 111_320.0;
        var cosLat = Math.Max(0.01, Math.Cos(ToRadians(latitude)));
        var dLon = radiusMeters / (111_320.0 * cosLat);
        return (
            Math.Min(90, latitude + dLat),
            Math.Min(180, longitude + dLon),
            Math.Max(-90, latitude - dLat),
            Math.Max(-180, longitude - dLon));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;
}
