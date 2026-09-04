namespace ReconEngine.Domain.Normalization;

/// <summary>Rules for canonicalising a reference id so that trivially-different strings compare equal.</summary>
public sealed record ReferenceCanonicalizationRules(
    bool Trim = true,
    bool ToUpper = true,
    bool RemoveWhitespace = false,
    IReadOnlyList<string>? StripPrefixes = null)
{
    public static ReferenceCanonicalizationRules Default { get; } = new();
}

/// <summary>
/// Canonicalises reference identifiers (trim, case-fold, strip configured prefixes) so that
/// <c>"txn_00123"</c> and <c>" TXN_00123 "</c> reconcile against the settlement id <c>00123</c>.
/// </summary>
public static class ReferenceCanonicalizer
{
    public static string Canonicalize(string? raw, ReferenceCanonicalizationRules? rules = null)
    {
        rules ??= ReferenceCanonicalizationRules.Default;
        var value = raw ?? string.Empty;

        if (rules.Trim)
            value = value.Trim();

        if (rules.RemoveWhitespace)
            value = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());

        if (rules.ToUpper)
            value = value.ToUpperInvariant();

        if (rules.StripPrefixes is { Count: > 0 })
        {
            foreach (var prefix in rules.StripPrefixes)
            {
                if (string.IsNullOrEmpty(prefix))
                    continue;
                var p = rules.ToUpper ? prefix.ToUpperInvariant() : prefix;
                if (value.StartsWith(p, StringComparison.Ordinal))
                {
                    value = value[p.Length..];
                    break;
                }
            }
        }

        return value;
    }
}
