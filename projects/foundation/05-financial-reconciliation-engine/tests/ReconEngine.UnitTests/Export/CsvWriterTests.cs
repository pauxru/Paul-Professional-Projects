using ReconEngine.Infrastructure.Export;

namespace ReconEngine.UnitTests.Export;

/// <summary>CSV export safety: RFC-4180 quoting and formula-injection neutralisation.</summary>
public sealed class CsvWriterTests
{
    [Theory]
    [InlineData("=SUM(A1:A2)", "'=SUM(A1:A2)")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@cmd", "'@cmd")]
    public void Formula_triggers_are_neutralised_with_a_leading_quote(string input, string expected)
    {
        Assert.Equal(expected, CsvWriter.Encode(input));
    }

    [Fact]
    public void A_tab_prefixed_value_is_also_neutralised()
    {
        Assert.Equal("'\tx", CsvWriter.Encode("\tx"));
    }

    [Fact]
    public void Ordinary_values_pass_through_unchanged()
    {
        Assert.Equal("normal", CsvWriter.Encode("normal"));
    }

    [Fact]
    public void Values_with_delimiters_are_quoted()
    {
        Assert.Equal("\"a,b\"", CsvWriter.Encode("a,b"));
    }

    [Fact]
    public void Embedded_quotes_are_doubled_and_wrapped()
    {
        Assert.Equal("\"a\"\"b\"", CsvWriter.Encode("a\"b"));
    }

    [Fact]
    public void Embedded_newlines_force_quoting()
    {
        Assert.Equal("\"a\nb\"", CsvWriter.Encode("a\nb"));
    }

    [Fact]
    public void An_injecting_value_containing_a_comma_is_both_neutralised_and_quoted()
    {
        // Leading '=' triggers neutralisation; the comma then forces quoting.
        Assert.Equal("\"'=1,2\"", CsvWriter.Encode("=1,2"));
    }

    [Fact]
    public void Null_encodes_to_empty()
    {
        Assert.Equal(string.Empty, CsvWriter.Encode(null));
    }

    [Fact]
    public void Write_emits_header_and_rows_with_crlf()
    {
        var csv = CsvWriter.Write(
            new[] { "a", "b" },
            new[] { new[] { "1", "2" } });

        Assert.Equal("a,b\r\n1,2\r\n", csv);
    }
}
