using Lakehouse.Domain.Data;

namespace Lakehouse.Application.Pipelines;

/// <summary>
/// Resolves the dimension version effective at an event instant — the crux of a correct SCD2 star join.
/// The classic bug is joining a fact to the <em>current</em> dimension row; instead we must pick the
/// version whose [valid_from, valid_to) window contains the event time. A null valid_to means "open".
/// This is a pure function so it can be unit-tested in isolation.
/// </summary>
public static class DimensionResolver
{
    public static Row? Effective(IEnumerable<Row> versions, string keyColumn, string businessKey, DateTimeOffset eventTs)
    {
        Row? best = null;
        DateTimeOffset bestFrom = DateTimeOffset.MinValue;

        foreach (var v in versions)
        {
            if (!string.Equals(v.GetString(keyColumn), businessKey, StringComparison.Ordinal)) continue;

            var from = v.GetTimestamp("valid_from");
            if (from is null || from.Value > eventTs) continue;

            var to = v.GetTimestamp("valid_to");
            if (to is not null && eventTs >= to.Value) continue;

            // Among all versions covering the instant, keep the one with the latest valid_from.
            if (best is null || from.Value >= bestFrom)
            {
                best = v;
                bestFrom = from.Value;
            }
        }

        return best;
    }
}
