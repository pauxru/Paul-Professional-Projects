using System.Globalization;
using System.Text;

namespace Contoso.Payments.Application.Reconciliation;

/// <summary>
/// Row parsed from a settlement CSV.  Columns: ProviderReference,PaymentIntentId,AmountMinor,Currency,Status.
/// </summary>
public sealed record SettlementRow(
    string ProviderReference,
    Guid? PaymentIntentId,
    long AmountMinor,
    string Currency,
    string Status);

public static class SettlementFileParser
{
    public static IReadOnlyList<SettlementRow> Parse(Stream csv)
    {
        var rows = new List<SettlementRow>();
        using var reader = new StreamReader(csv, Encoding.UTF8, leaveOpen: true);
        var line = reader.ReadLine(); // header
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (parts.Length < 5) continue;
            var providerRef = parts[0].Trim();
            Guid? intentId = Guid.TryParse(parts[1].Trim(), out var g) ? g : null;
            if (!long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor))
                continue;
            var currency = parts[3].Trim();
            var status = parts[4].Trim();
            rows.Add(new SettlementRow(providerRef, intentId, minor, currency, status));
        }
        return rows;
    }
}
