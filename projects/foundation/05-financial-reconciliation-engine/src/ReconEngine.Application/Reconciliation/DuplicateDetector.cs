using ReconEngine.Domain.Entities;

namespace ReconEngine.Application.Reconciliation;

/// <summary>
/// Splits a set of records into the unique rows and the duplicates, using the stable row hash. The
/// first occurrence (ordered deterministically) is kept as unique; every later row with the same hash
/// is a duplicate. Keeping this deterministic is what makes runs reproducible.
/// </summary>
public static class DuplicateDetector
{
    public static (IReadOnlyList<ReconRecord> Unique, IReadOnlyList<ReconRecord> Duplicates) Detect(
        IEnumerable<ReconRecord> records)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<ReconRecord>();
        var duplicates = new List<ReconRecord>();

        foreach (var r in records
                     .OrderBy(r => r.RowHash, StringComparer.Ordinal)
                     .ThenBy(r => r.LineNumber))
        {
            if (seen.Add(r.RowHash))
                unique.Add(r);
            else
                duplicates.Add(r);
        }

        return (unique, duplicates);
    }
}
