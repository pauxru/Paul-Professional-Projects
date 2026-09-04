using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.Domain.Rules;

/// <summary>
/// Impossible-travel maths. Computes the implied ground speed between two
/// transactions and classifies against a feasibility ceiling.
/// </summary>
public static class ImpossibleTravel
{
    /// <summary>
    /// Commercial-aviation ceiling used as the default feasibility bound (km/h).
    /// A same-city pair returns Feasible with a very small speed; antipodal pairs
    /// return the correct ~20,015 km great-circle distance.
    /// </summary>
    public const double DefaultMaxFeasibleKmh = 900.0;

    public static TravelClassification Assess(
        GeoLocation prev,
        DateTimeOffset prevAt,
        GeoLocation next,
        DateTimeOffset nextAt,
        double maxFeasibleKmh = DefaultMaxFeasibleKmh)
    {
        if (nextAt < prevAt) throw new ArgumentException("next time must be at or after prev time");
        var km = prev.DistanceKmTo(next);
        var elapsedHours = (nextAt - prevAt).TotalHours;

        // Two txns within the same second at the same location: feasible.
        if (elapsedHours <= 0)
        {
            return km < 0.5
                ? new TravelClassification(km, double.PositiveInfinity, false)
                : new TravelClassification(km, double.PositiveInfinity, true);
        }

        var speedKmh = km / elapsedHours;
        var impossible = speedKmh > maxFeasibleKmh;
        return new TravelClassification(km, speedKmh, impossible);
    }
}

public readonly record struct TravelClassification(double DistanceKm, double SpeedKmh, bool Impossible);
