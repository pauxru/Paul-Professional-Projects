using SavannaLogistics.Domain;

namespace SavannaLogistics.UnitTests;

public sealed class GeoMathTests
{
    [Fact]
    public void Haversine_NairobiToMombasa_IsWithinKnownDistanceTolerance()
    {
        var distance = GeoMath.HaversineKm(
            new GeoPoint(-1.286389, 36.817223),
            new GeoPoint(-4.043477, 39.668206));

        Assert.InRange(distance, 440, 443);
    }

    [Fact]
    public void Haversine_SamePoint_IsZero()
    {
        var point = new GeoPoint(-1.2921, 36.8219);
        Assert.Equal(0, GeoMath.HaversineKm(point, point), 10);
    }

    [Fact]
    public void Haversine_IsSymmetric()
    {
        var left = new GeoPoint(-1.286389, 36.817223);
        var right = new GeoPoint(-1.2107, 36.9231);
        Assert.Equal(GeoMath.HaversineKm(left, right), GeoMath.HaversineKm(right, left), 10);
    }

    [Fact]
    public void PointInPolygon_PointInsideConvexPolygon_ReturnsTrue()
    {
        var polygon = Square();
        Assert.True(GeoMath.PointInPolygon(new GeoPoint(0.5, 0.5), polygon));
    }

    [Fact]
    public void PointInPolygon_PointOutside_ReturnsFalse()
    {
        Assert.False(GeoMath.PointInPolygon(new GeoPoint(1.5, 0.5), Square()));
    }

    [Fact]
    public void PointInPolygon_PointOnEdge_IsIncluded()
    {
        Assert.True(GeoMath.PointInPolygon(new GeoPoint(0.5, 0), Square()));
    }

    [Fact]
    public void PointInPolygon_PointOnVertex_IsIncluded()
    {
        Assert.True(GeoMath.PointInPolygon(new GeoPoint(0, 0), Square()));
    }

    [Fact]
    public void PointInPolygon_ConcaveNotch_IsExcluded()
    {
        GeoPoint[] concave =
        [
            new(0, 0), new(0, 3), new(1, 3), new(1, 1),
            new(2, 1), new(2, 3), new(3, 3), new(3, 0)
        ];
        Assert.False(GeoMath.PointInPolygon(new GeoPoint(1.5, 2), concave));
        Assert.True(GeoMath.PointInPolygon(new GeoPoint(0.5, 2), concave));
    }

    [Fact]
    public void DistanceToPolyline_PointOnLine_IsZero()
    {
        var distance = GeoMath.DistanceToPolylineKm(
            new GeoPoint(-1.25, 36.85),
            [new GeoPoint(-1.3, 36.8), new GeoPoint(-1.2, 36.9)]);
        Assert.InRange(distance, 0, 0.001);
    }

    [Fact]
    public void BoundingBox_CircleContainsCardinalBoundaryPoints()
    {
        var center = new GeoPoint(-1.286389, 36.817223);
        var box = GeoMath.BoundingBoxForCircle(center, 1);
        Assert.True(box.Contains(center));
        Assert.True(box.Contains(new GeoPoint(center.Latitude + 0.008, center.Longitude)));
    }

    private static GeoPoint[] Square() =>
        [new(0, 0), new(0, 1), new(1, 1), new(1, 0)];
}
