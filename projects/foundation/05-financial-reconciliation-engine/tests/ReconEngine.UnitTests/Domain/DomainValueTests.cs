using ReconEngine.Domain.Normalization;
using ReconEngine.Domain.ValueObjects;

namespace ReconEngine.UnitTests.Domain;

public sealed class MoneyTests
{
    [Theory]
    [InlineData(100.00, "USD", 10_000)]
    [InlineData(123.45, "KES", 12_345)]
    [InlineData(100, "JPY", 100)]       // zero-decimal currency
    [InlineData(1.234, "BHD", 1_234)]   // three-decimal currency
    public void FromMajor_scales_by_currency_exponent(decimal major, string currency, long expectedMinor)
    {
        Assert.Equal(expectedMinor, Money.FromMajor(major, currency).MinorUnits);
    }

    [Theory]
    [InlineData(0.005, 0)] // 0.5 minor -> banker's rounding to even -> 0
    [InlineData(0.015, 2)] // 1.5 minor -> banker's rounding to even -> 2
    public void FromMajor_uses_banker_rounding(decimal major, long expectedMinor)
    {
        Assert.Equal(expectedMinor, Money.FromMajor(major, "USD").MinorUnits);
    }

    [Fact]
    public void ToMajor_is_inverse_of_minor_units()
    {
        Assert.Equal(100.00m, new Money(10_000, "USD").ToMajor());
        Assert.Equal(100m, new Money(100, "JPY").ToMajor());
    }

    [Fact]
    public void Currency_is_normalised_to_upper_invariant()
    {
        Assert.Equal("KES", new Money(1, "kes").Currency);
    }

    [Fact]
    public void Blank_currency_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new Money(1, " "));
    }

    [Fact]
    public void Arithmetic_across_currencies_is_forbidden()
    {
        var kes = new Money(1_000, "KES");
        var usd = new Money(1_000, "USD");
        Assert.Throws<InvalidOperationException>(() => kes.Add(usd));
    }

    [Fact]
    public void Add_and_absolute_difference_work_within_a_currency()
    {
        var a = new Money(1_000, "KES");
        var b = new Money(1_750, "KES");
        Assert.Equal(2_750, a.Add(b).MinorUnits);
        Assert.Equal(750, a.AbsoluteDifferenceMinor(b));
    }

    [Fact]
    public void ToString_is_invariant_and_currency_scaled()
    {
        Assert.Equal("100.00 KES", new Money(10_000, "KES").ToString());
        Assert.Equal("100 JPY", new Money(100, "JPY").ToString());
    }
}

public sealed class CurrencyInfoTests
{
    [Theory]
    [InlineData("KES", 2)]
    [InlineData("USD", 2)]
    [InlineData("EUR", 2)]
    [InlineData("JPY", 0)]
    [InlineData("BHD", 3)]
    [InlineData("ZZZ", 2)] // unknown falls back to 2
    public void DecimalsFor_returns_iso_exponent(string currency, int expected)
    {
        Assert.Equal(expected, CurrencyInfo.DecimalsFor(currency));
    }

    [Fact]
    public void IsKnown_reflects_the_supported_table()
    {
        Assert.True(CurrencyInfo.IsKnown("KES"));
        Assert.False(CurrencyInfo.IsKnown("ZZZ"));
    }
}

public sealed class ReferenceCanonicalizerTests
{
    [Fact]
    public void Trims_and_uppercases_by_default()
    {
        Assert.Equal("TXN_00123", ReferenceCanonicalizer.Canonicalize("  txn_00123 "));
    }

    [Fact]
    public void Strips_the_first_matching_prefix_only()
    {
        var rules = new ReferenceCanonicalizationRules(StripPrefixes: new[] { "TXN_", "STL_" });
        Assert.Equal("00123", ReferenceCanonicalizer.Canonicalize(" txn_00123 ", rules));
        Assert.Equal("00123", ReferenceCanonicalizer.Canonicalize("STL_00123", rules));
    }

    [Fact]
    public void Can_remove_all_internal_whitespace()
    {
        var rules = new ReferenceCanonicalizationRules(RemoveWhitespace: true);
        Assert.Equal("ABC", ReferenceCanonicalizer.Canonicalize("a b c", rules));
    }

    [Fact]
    public void Null_reference_canonicalises_to_empty()
    {
        Assert.Equal(string.Empty, ReferenceCanonicalizer.Canonicalize(null));
    }
}

public sealed class RowHasherTests
{
    private static string Hash(string status) =>
        RowHasher.Hash("Internal", "REF1", 10_000, "KES", new DateOnly(2024, 1, 15), status);

    [Fact]
    public void Hash_is_deterministic_for_identical_input()
    {
        Assert.Equal(Hash("Captured"), Hash("Captured"));
    }

    [Fact]
    public void Hash_changes_when_an_identity_field_changes()
    {
        Assert.NotEqual(Hash("Captured"), Hash("Reversed"));
    }

    [Fact]
    public void Checksum_is_independent_of_enumeration_order()
    {
        var h1 = Hash("Captured");
        var h2 = Hash("Reversed");
        Assert.Equal(
            RowHasher.Checksum(new[] { h1, h2 }),
            RowHasher.Checksum(new[] { h2, h1 }));
    }
}
