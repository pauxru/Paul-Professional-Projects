namespace SavannaLogistics.Domain;

public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    public bool IsValid => Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
}

public readonly record struct GeoBoundingBox(
    double MinLatitude,
    double MinLongitude,
    double MaxLatitude,
    double MaxLongitude)
{
    public bool Contains(GeoPoint point) =>
        point.Latitude >= MinLatitude &&
        point.Latitude <= MaxLatitude &&
        point.Longitude >= MinLongitude &&
        point.Longitude <= MaxLongitude;
}

public static class GeoMath
{
    public const double EarthRadiusKm = 6371.0088;
    private const double DegreesToRadians = Math.PI / 180d;

    public static double HaversineKm(GeoPoint from, GeoPoint to)
    {
        Validate(from);
        Validate(to);

        var latitudeDelta = (to.Latitude - from.Latitude) * DegreesToRadians;
        var longitudeDelta = (to.Longitude - from.Longitude) * DegreesToRadians;
        var fromLatitude = from.Latitude * DegreesToRadians;
        var toLatitude = to.Latitude * DegreesToRadians;

        var a = Math.Pow(Math.Sin(latitudeDelta / 2), 2) +
                Math.Cos(fromLatitude) * Math.Cos(toLatitude) *
                Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    public static bool PointInPolygon(GeoPoint point, IReadOnlyList<GeoPoint> polygon)
    {
        Validate(point);
        if (polygon.Count < 3)
        {
            return false;
        }

        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var current = polygon[i];
            var previous = polygon[(i + polygon.Count - 1) % polygon.Count];
            Validate(current);
            Validate(previous);

            if (IsPointOnSegment(point, previous, current))
            {
                return true;
            }

            var crossesLatitude = (current.Latitude > point.Latitude) !=
                                  (previous.Latitude > point.Latitude);
            if (!crossesLatitude)
            {
                continue;
            }

            var longitudeAtRay = (previous.Longitude - current.Longitude) *
                                 (point.Latitude - current.Latitude) /
                                 (previous.Latitude - current.Latitude) +
                                 current.Longitude;
            if (point.Longitude < longitudeAtRay)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    public static bool IsPointOnSegment(
        GeoPoint point,
        GeoPoint segmentStart,
        GeoPoint segmentEnd,
        double tolerance = 1e-9)
    {
        var cross = (point.Longitude - segmentStart.Longitude) *
                    (segmentEnd.Latitude - segmentStart.Latitude) -
                    (point.Latitude - segmentStart.Latitude) *
                    (segmentEnd.Longitude - segmentStart.Longitude);
        if (Math.Abs(cross) > tolerance)
        {
            return false;
        }

        return point.Longitude >= Math.Min(segmentStart.Longitude, segmentEnd.Longitude) - tolerance &&
               point.Longitude <= Math.Max(segmentStart.Longitude, segmentEnd.Longitude) + tolerance &&
               point.Latitude >= Math.Min(segmentStart.Latitude, segmentEnd.Latitude) - tolerance &&
               point.Latitude <= Math.Max(segmentStart.Latitude, segmentEnd.Latitude) + tolerance;
    }

    public static double DistanceToPolylineKm(GeoPoint point, IReadOnlyList<GeoPoint> polyline)
    {
        if (polyline.Count == 0)
        {
            return double.PositiveInfinity;
        }

        if (polyline.Count == 1)
        {
            return HaversineKm(point, polyline[0]);
        }

        var minimum = double.PositiveInfinity;
        for (var i = 1; i < polyline.Count; i++)
        {
            minimum = Math.Min(minimum, DistanceToSegmentKm(point, polyline[i - 1], polyline[i]));
        }

        return minimum;
    }

    public static double DistanceToSegmentKm(GeoPoint point, GeoPoint start, GeoPoint end)
    {
        Validate(point);
        Validate(start);
        Validate(end);

        var referenceLatitude = (point.Latitude + start.Latitude + end.Latitude) / 3d * DegreesToRadians;
        var kmPerDegreeLatitude = 111.195d;
        var kmPerDegreeLongitude = kmPerDegreeLatitude * Math.Cos(referenceLatitude);

        var ax = (start.Longitude - point.Longitude) * kmPerDegreeLongitude;
        var ay = (start.Latitude - point.Latitude) * kmPerDegreeLatitude;
        var bx = (end.Longitude - point.Longitude) * kmPerDegreeLongitude;
        var by = (end.Latitude - point.Latitude) * kmPerDegreeLatitude;
        var dx = bx - ax;
        var dy = by - ay;
        var lengthSquared = dx * dx + dy * dy;

        if (lengthSquared <= double.Epsilon)
        {
            return Math.Sqrt(ax * ax + ay * ay);
        }

        var projection = Math.Clamp(-(ax * dx + ay * dy) / lengthSquared, 0d, 1d);
        var closestX = ax + projection * dx;
        var closestY = ay + projection * dy;
        return Math.Sqrt(closestX * closestX + closestY * closestY);
    }

    public static double PolylineLengthKm(IReadOnlyList<GeoPoint> points)
    {
        var total = 0d;
        for (var i = 1; i < points.Count; i++)
        {
            total += HaversineKm(points[i - 1], points[i]);
        }

        return total;
    }

    public static GeoBoundingBox BoundingBoxForCircle(GeoPoint center, double radiusKm)
    {
        Validate(center);
        if (radiusKm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusKm));
        }

        var latitudeDelta = radiusKm / 111.195d;
        var longitudeScale = Math.Max(0.01d, Math.Cos(center.Latitude * DegreesToRadians));
        var longitudeDelta = radiusKm / (111.195d * longitudeScale);
        return new GeoBoundingBox(
            Math.Max(-90, center.Latitude - latitudeDelta),
            Math.Max(-180, center.Longitude - longitudeDelta),
            Math.Min(90, center.Latitude + latitudeDelta),
            Math.Min(180, center.Longitude + longitudeDelta));
    }

    public static GeoBoundingBox BoundingBoxForPolygon(IReadOnlyList<GeoPoint> polygon)
    {
        if (polygon.Count < 3)
        {
            throw new ArgumentException("A polygon requires at least three points.", nameof(polygon));
        }

        foreach (var point in polygon)
        {
            Validate(point);
        }

        return new GeoBoundingBox(
            polygon.Min(p => p.Latitude),
            polygon.Min(p => p.Longitude),
            polygon.Max(p => p.Latitude),
            polygon.Max(p => p.Longitude));
    }

    private static void Validate(GeoPoint point)
    {
        if (!point.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(point), "Latitude or longitude is invalid.");
        }
    }
}
