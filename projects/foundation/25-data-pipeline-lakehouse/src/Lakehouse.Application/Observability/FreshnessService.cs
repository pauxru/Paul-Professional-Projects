using Lakehouse.Application.Abstractions;

namespace Lakehouse.Application.Observability;

/// <summary>Freshness of one table: when it was last written and how far behind "now" that is.</summary>
public sealed record TableFreshness(string Table, DateTimeOffset? LastWriteUtc, double? LagSeconds, long Snapshots);

/// <summary>
/// Computes per-table freshness gauges from the table format's commit log — the newest snapshot
/// timestamp is the last time the table changed, so "now − that" is a real staleness signal.
/// </summary>
public sealed class FreshnessService(ILakehouse lake, IClock clock)
{
    public IReadOnlyList<TableFreshness> Snapshot()
    {
        var now = clock.UtcNow;
        var result = new List<TableFreshness>();
        foreach (var name in lake.ListTables())
        {
            var history = lake.Table(name).History();
            if (history.Count == 0)
            {
                result.Add(new TableFreshness(name, null, null, 0));
                continue;
            }
            var last = history[^1].TimestampUtc;
            result.Add(new TableFreshness(name, last, (now - last).TotalSeconds, history.Count));
        }
        return result.OrderBy(f => f.Table, StringComparer.Ordinal).ToList();
    }
}
