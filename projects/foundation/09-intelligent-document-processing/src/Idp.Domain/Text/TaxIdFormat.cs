using System.Text.RegularExpressions;

namespace Idp.Domain.Text;

/// <summary>
/// Synthetic tax-id format with a checksum-style final character, used by the validation guardrail
/// and by the document generator so that "valid" and "deliberately corrupted" tax ids are testable.
///
/// Format: two country letters, nine digits, one check letter where the check letter is
/// ('A' + (sum of the nine digits) mod 26). Example: KE001234571 -> check letter for digit sum.
/// </summary>
public static partial class TaxIdFormat
{
    [GeneratedRegex(@"^[A-Z]{2}\d{9}[A-Z]$")]
    private static partial Regex Shape();

    public static char CheckLetter(string nineDigits)
    {
        var sum = 0;
        foreach (var c in nineDigits)
            if (char.IsDigit(c)) sum += c - '0';
        return (char)('A' + (sum % 26));
    }

    public static string Build(string twoLetterCountry, string nineDigits)
    {
        if (nineDigits.Length != 9)
            throw new ArgumentException("Expected nine digits.", nameof(nineDigits));
        return $"{twoLetterCountry.ToUpperInvariant()}{nineDigits}{CheckLetter(nineDigits)}";
    }

    public static bool IsValid(string? taxId)
    {
        if (string.IsNullOrWhiteSpace(taxId)) return false;
        var value = taxId.Trim().ToUpperInvariant();
        if (!Shape().IsMatch(value)) return false;
        var digits = value.Substring(2, 9);
        var expected = CheckLetter(digits);
        return value[^1] == expected;
    }
}
