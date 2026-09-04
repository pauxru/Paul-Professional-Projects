using System.Globalization;
using System.Text;

namespace Contoso.Payments.Application.Reconciliation;

/// <summary>
/// Generates synthetic settlement files that intentionally embed each mismatch class.  Used by
/// tests and the demo script so the reconciliation report can be exercised end-to-end.
/// </summary>
public static class SettlementFileGenerator
{
    /// <summary>
    /// Build a settlement CSV.  The seed row set represents the "clean" file; each mismatch flag
    /// toggles the injection of one specific defect on top of that baseline.
    /// </summary>
    public static string Build(IEnumerable<SettlementRow> baseRows, MismatchFlags flags)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ProviderReference,PaymentIntentId,AmountMinor,Currency,Status");

        var rows = baseRows.ToList();

        if (flags.HasFlag(MismatchFlags.MissingInProvider) && rows.Count > 0)
        {
            rows.RemoveAt(0); // drop first — it will look "missing in provider" internally
        }
        if (flags.HasFlag(MismatchFlags.AmountMismatch) && rows.Count > 0)
        {
            var idx = Math.Min(1, rows.Count - 1);
            var r = rows[idx];
            rows[idx] = r with { AmountMinor = r.AmountMinor + 5 };
        }
        if (flags.HasFlag(MismatchFlags.DuplicateInProvider) && rows.Count > 0)
        {
            rows.Add(rows[rows.Count - 1]);
        }
        if (flags.HasFlag(MismatchFlags.StatusMismatch) && rows.Count > 0)
        {
            var idx = rows.Count - 1;
            var r = rows[idx];
            rows[idx] = r with { Status = r.Status == "Captured" ? "Failed" : "Captured" };
        }
        if (flags.HasFlag(MismatchFlags.MissingInternally))
        {
            rows.Add(new SettlementRow("prov-" + Guid.NewGuid().ToString("N")[..8], null, 12345, "USD", "Captured"));
        }

        foreach (var r in rows)
        {
            sb.Append(r.ProviderReference).Append(',');
            sb.Append(r.PaymentIntentId?.ToString() ?? "").Append(',');
            sb.Append(r.AmountMinor.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(r.Currency).Append(',');
            sb.AppendLine(r.Status);
        }
        return sb.ToString();
    }
}

[Flags]
public enum MismatchFlags
{
    None = 0,
    MissingInProvider = 1,
    MissingInternally = 2,
    AmountMismatch = 4,
    DuplicateInProvider = 8,
    StatusMismatch = 16
}
