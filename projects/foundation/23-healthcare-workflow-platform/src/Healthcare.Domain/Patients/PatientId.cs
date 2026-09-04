namespace Healthcare.Domain.Patients;

/// <summary>
/// Fictional patient identifier. Format: NDC-XXXXXXX-C
/// - NDC prefix denotes the (fictional) Nairobi Demo Clinic scheme.
/// - 7 numeric digits (payload) followed by a single check digit computed with a Luhn-style algorithm.
/// - Deterministic: the same payload yields the same check digit; any single-digit substitution or
///   most single-digit transpositions are detected.
/// </summary>
public readonly record struct PatientId
{
    public string Value { get; }

    private PatientId(string value) => Value = value;

    public override string ToString() => Value;

    public static PatientId Create(int payload)
    {
        if (payload is < 0 or > 9_999_999)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                "Payload must be a non-negative integer with at most 7 digits.");
        }

        var payloadDigits = payload.ToString("D7");
        var check = ComputeCheckDigit(payloadDigits);
        return new PatientId($"NDC-{payloadDigits}-{check}");
    }

    public static bool TryParse(string? input, out PatientId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var trimmed = input.Trim().ToUpperInvariant();
        // Format: NDC-#######-# (total 13 chars)
        if (trimmed.Length != 13) return false;
        if (!trimmed.StartsWith("NDC-", StringComparison.Ordinal)) return false;
        if (trimmed[11] != '-') return false;

        var payload = trimmed.Substring(4, 7);
        var checkStr = trimmed.Substring(12, 1);
        foreach (var c in payload) if (!char.IsDigit(c)) return false;
        if (!char.IsDigit(checkStr[0])) return false;

        var expected = ComputeCheckDigit(payload);
        if (expected != checkStr[0]) return false;

        id = new PatientId(trimmed);
        return true;
    }

    public static PatientId Parse(string input) =>
        TryParse(input, out var id)
            ? id
            : throw new FormatException($"Invalid patient id '{input}'.");

    /// <summary>Luhn algorithm over the 7 payload digits, returning a single decimal digit.</summary>
    private static char ComputeCheckDigit(string payloadDigits)
    {
        var sum = 0;
        // Right-most payload digit is at index length-1; check digit sits to its right at "position 1".
        // We start doubling from position 2 (second-from-right of the number that includes check).
        // Practically: iterate payload digits right-to-left; every OTHER digit (starting index 0) is doubled.
        var doubleIt = true;
        for (var i = payloadDigits.Length - 1; i >= 0; i--)
        {
            var d = payloadDigits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            doubleIt = !doubleIt;
        }
        var check = (10 - (sum % 10)) % 10;
        return (char)('0' + check);
    }
}
