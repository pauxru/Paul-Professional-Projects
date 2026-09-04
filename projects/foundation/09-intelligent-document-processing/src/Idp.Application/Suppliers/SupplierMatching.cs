using Idp.Domain.Suppliers;
using Idp.Domain.Text;

namespace Idp.Application.Suppliers;

/// <summary>Fuzzy supplier master-data matching using Jaro-Winkler over name and aliases.</summary>
public static class SupplierMatching
{
    public static (Supplier? Supplier, double Score) BestMatch(
        string? candidateName, IReadOnlyList<Supplier> suppliers)
    {
        if (string.IsNullOrWhiteSpace(candidateName) || suppliers.Count == 0)
            return (null, 0.0);

        var normalizedCandidate = Normalize(candidateName);
        Supplier? best = null;
        var bestScore = 0.0;

        foreach (var supplier in suppliers)
        {
            var score = JaroWinkler(normalizedCandidate, Normalize(supplier.Name));
            foreach (var alias in supplier.AliasList)
                score = Math.Max(score, JaroWinkler(normalizedCandidate, Normalize(alias)));

            if (score > bestScore)
            {
                bestScore = score;
                best = supplier;
            }
        }
        return (best, bestScore);
    }

    private static double JaroWinkler(string a, string b) => StringDistance.JaroWinkler(a, b);

    /// <summary>Lower-case, strip common company suffixes/punctuation for a fairer comparison.</summary>
    public static string Normalize(string value)
    {
        var lowered = value.Trim().ToLowerInvariant();
        var cleaned = new string(lowered.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());
        foreach (var suffix in new[] { " limited", " ltd", " plc", " inc", " llc", " co", " company" })
        {
            if (cleaned.EndsWith(suffix, StringComparison.Ordinal))
                cleaned = cleaned[..^suffix.Length];
        }
        return cleaned.Replace("  ", " ").Trim();
    }
}
