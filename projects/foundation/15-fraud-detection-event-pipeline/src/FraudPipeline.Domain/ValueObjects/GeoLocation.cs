namespace FraudPipeline.Domain.ValueObjects;

/// <summary>
/// Geographic location. Latitude in degrees [-90, 90], longitude in degrees [-180, 180].
/// </summary>
public sealed record GeoLocation
{
    public double LatitudeDeg { get; init; }
    public double LongitudeDeg { get; init; }
    public string CountryIso2 { get; init; } = "US";

    private GeoLocation() { }

    public GeoLocation(double latitudeDeg, double longitudeDeg, string countryIso2)
    {
        LatitudeDeg = latitudeDeg;
        LongitudeDeg = longitudeDeg;
        CountryIso2 = countryIso2;
    }

    public static GeoLocation Of(double lat, double lon, string countryIso2)
    {
        if (double.IsNaN(lat) || double.IsInfinity(lat) || lat < -90 || lat > 90)
            throw new ArgumentOutOfRangeException(nameof(lat), "Latitude must be within [-90, 90].");
        if (double.IsNaN(lon) || double.IsInfinity(lon) || lon < -180 || lon > 180)
            throw new ArgumentOutOfRangeException(nameof(lon), "Longitude must be within [-180, 180].");
        if (string.IsNullOrWhiteSpace(countryIso2) || countryIso2.Length != 2)
            throw new ArgumentException("CountryIso2 must be a 2-letter ISO 3166 alpha-2 code.", nameof(countryIso2));
        return new GeoLocation(lat, lon, countryIso2.ToUpperInvariant());
    }

    /// <summary>
    /// Great-circle distance in kilometres using the haversine formula.
    /// </summary>
    public double DistanceKmTo(GeoLocation other)
    {
        const double earthRadiusKm = 6371.0088;
        var lat1 = DegreesToRadians(LatitudeDeg);
        var lat2 = DegreesToRadians(other.LatitudeDeg);
        var dLat = DegreesToRadians(other.LatitudeDeg - LatitudeDeg);
        var dLon = DegreesToRadians(other.LongitudeDeg - LongitudeDeg);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1) * Math.Cos(lat2)
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusKm * c;
    }

    private static double DegreesToRadians(double d) => d * Math.PI / 180.0;
}
