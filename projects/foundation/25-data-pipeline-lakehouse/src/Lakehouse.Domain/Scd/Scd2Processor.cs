using System.Globalization;
using Lakehouse.Domain.Data;

namespace Lakehouse.Domain.Scd;

/// <summary>One tracked change to a dimension member. A null <see cref="Attributes"/> with
/// <see cref="IsDelete"/> = true is a tombstone that closes the currently-open version.</summary>
public sealed record DimChange(
    string BusinessKey,
    DateTimeOffset EffectiveFrom,
    long Sequence,
    Row? Attributes,
    bool IsDelete = false);

/// <summary>A materialised SCD2 version row (valid-from / valid-to / is-current).</summary>
public sealed record DimVersion(
    long SurrogateKey,
    string BusinessKey,
    Row Attributes,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    bool IsCurrent);

/// <summary>
/// Builds Slowly-Changing-Dimension Type 2 versions from a change feed. The build is a deterministic
/// fold over the fully re-sorted event log, so out-of-order and late-arriving updates are correct by
/// construction and re-running is idempotent. Surrogate keys are content-derived (hash of business key
/// + valid-from) so the same input always yields the same keys — important for downstream fact joins.
/// </summary>
public static class Scd2Processor
{
    public static readonly DateTimeOffset OpenEnd = DateTimeOffset.MaxValue;

    public static IReadOnlyList<DimVersion> Build(IEnumerable<DimChange> changes, IReadOnlyList<string> trackedAttributes)
    {
        var result = new List<DimVersion>();

        foreach (var group in changes.GroupBy(c => c.BusinessKey, StringComparer.Ordinal))
        {
            // Collapse events that share an effective instant: the highest-sequence event wins for that instant.
            var perInstant = group
                .GroupBy(c => c.EffectiveFrom)
                .Select(g => g.OrderBy(c => c.Sequence).Last())
                .OrderBy(c => c.EffectiveFrom)
                .ThenBy(c => c.Sequence)
                .ToList();

            var segments = new List<(DateTimeOffset From, Row Attrs)>();
            (DateTimeOffset From, Row Attrs)? open = null;
            var closed = new List<(DateTimeOffset From, DateTimeOffset To, Row Attrs)>();

            void CloseOpen(DateTimeOffset at)
            {
                if (open is { } o && at > o.From)
                    closed.Add((o.From, at, o.Attrs));
                open = null;
            }

            foreach (var ev in perInstant)
            {
                if (ev.IsDelete)
                {
                    CloseOpen(ev.EffectiveFrom);
                    continue;
                }

                var attrs = Project(ev.Attributes!, trackedAttributes);
                if (open is { } cur && AttributesEqual(cur.Attrs, attrs, trackedAttributes))
                    continue; // no material change — dedup

                CloseOpen(ev.EffectiveFrom);
                open = (ev.EffectiveFrom, attrs);
            }

            foreach (var c in closed)
                result.Add(Version(group.Key, c.Attrs, c.From, c.To, isCurrent: false));

            if (open is { } stillOpen)
                result.Add(Version(group.Key, stillOpen.Attrs, stillOpen.From, null, isCurrent: true));
        }

        return result
            .OrderBy(v => v.BusinessKey, StringComparer.Ordinal)
            .ThenBy(v => v.ValidFrom)
            .ToList();
    }

    private static DimVersion Version(string businessKey, Row attrs, DateTimeOffset from, DateTimeOffset? to, bool isCurrent)
        => new(SurrogateKey(businessKey, from), businessKey, attrs, from, to, isCurrent);

    private static Row Project(Row source, IReadOnlyList<string> tracked)
    {
        var row = new Row();
        foreach (var col in tracked) row[col] = source[col];
        return row;
    }

    public static bool AttributesEqual(Row a, Row b, IReadOnlyList<string> tracked)
    {
        foreach (var col in tracked)
            if (!string.Equals(Canonical(a[col]), Canonical(b[col]), StringComparison.Ordinal))
                return false;
        return true;
    }

    private static string Canonical(object? value) => value switch
    {
        null => "\u0000",
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "\u0000"
    };

    /// <summary>Deterministic positive 63-bit surrogate key via FNV-1a over the natural key + valid-from.</summary>
    public static long SurrogateKey(string businessKey, DateTimeOffset validFrom)
    {
        var seed = businessKey + "|" + validFrom.UtcTicks.ToString(CultureInfo.InvariantCulture);
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var ch in seed)
        {
            hash ^= ch;
            hash *= prime;
        }
        return (long)(hash & 0x7FFFFFFFFFFFFFFFUL);
    }
}
