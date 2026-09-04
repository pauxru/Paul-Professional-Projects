using Idp.Application.Suppliers;
using Idp.Domain.Text;

namespace Idp.UnitTests;

/// <summary>Fuzzy supplier matching, string-distance properties, and tax-id checksum format.</summary>
public class SupplierMatchingTests
{
    private static IReadOnlyList<Idp.Domain.Suppliers.Supplier> Master() => new[]
    {
        DocBuilder.MakeSupplier("Rift Valley Supplies Ltd", currency: "KES",
            aliases: new[] { "Rift Valley Supplies", "RVS Ltd" }),
        DocBuilder.MakeSupplier("Nairobi Steel Traders", currency: "KES"),
        DocBuilder.MakeSupplier("Mombasa Imports Co", currency: "USD"),
    };

    [Fact]
    public void Matches_supplier_by_alias()
    {
        var (match, score) = SupplierMatching.BestMatch("RVS Ltd", Master());
        Assert.NotNull(match);
        Assert.Equal("Rift Valley Supplies Ltd", match!.Name);
        Assert.True(score >= 0.86, $"score {score}");
    }

    [Fact]
    public void Matches_supplier_despite_a_misspelling()
    {
        var (match, score) = SupplierMatching.BestMatch("Rift Vally Supplies", Master());
        Assert.NotNull(match);
        Assert.Equal("Rift Valley Supplies Ltd", match!.Name);
        Assert.True(score >= 0.86, $"score {score}");
    }

    [Fact]
    public void Does_not_match_an_unrelated_name()
    {
        var (_, score) = SupplierMatching.BestMatch("Totally Unrelated Trading House", Master());
        Assert.True(score < 0.86, $"score {score}");
    }

    [Fact]
    public void Normalize_strips_company_suffixes()
    {
        Assert.Equal("rift valley supplies", SupplierMatching.Normalize("Rift Valley Supplies Ltd"));
        Assert.Equal("acme", SupplierMatching.Normalize("ACME, Inc."));
    }

    [Fact]
    public void JaroWinkler_is_one_for_identical_strings_and_lower_for_different()
    {
        Assert.Equal(1.0, StringDistance.JaroWinkler("nairobi", "nairobi"), 6);
        Assert.True(StringDistance.JaroWinkler("nairobi", "mombasa") < 0.8);
    }

    [Fact]
    public void Levenshtein_counts_edits()
    {
        Assert.Equal(0, StringDistance.Levenshtein("abc", "abc"));
        Assert.Equal(1, StringDistance.Levenshtein("abc", "abd"));
        Assert.Equal(3, StringDistance.Levenshtein("abc", "xyz"));
    }

    // ---- Tax id checksum-style format --------------------------------------------------------

    [Fact]
    public void TaxId_build_roundtrips_and_validates()
    {
        var id = TaxIdFormat.Build("KE", "001234571");
        Assert.True(TaxIdFormat.IsValid(id));
        Assert.Equal(11 + 1, id.Length); // 2 letters + 9 digits + 1 check letter
    }

    [Fact]
    public void TaxId_rejects_bad_checksum_and_shape()
    {
        var good = TaxIdFormat.Build("KE", "001234571");
        var tampered = good[..^1] + (good[^1] == 'A' ? 'B' : 'A');
        Assert.False(TaxIdFormat.IsValid(tampered));
        Assert.False(TaxIdFormat.IsValid("KE12345"));    // wrong shape
        Assert.False(TaxIdFormat.IsValid(null));
    }

    [Fact]
    public void TaxId_check_letter_is_deterministic()
    {
        Assert.Equal(TaxIdFormat.CheckLetter("001234571"), TaxIdFormat.CheckLetter("001234571"));
    }
}
