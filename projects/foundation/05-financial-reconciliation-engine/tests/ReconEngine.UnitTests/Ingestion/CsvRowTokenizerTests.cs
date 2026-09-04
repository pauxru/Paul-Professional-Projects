using System.Text;
using ReconEngine.Application.Ingestion;
using ReconEngine.Infrastructure.Ingestion;

namespace ReconEngine.UnitTests.Ingestion;

/// <summary>Edge cases for the streaming RFC-4180 CSV reader.</summary>
public sealed class CsvRowTokenizerTests
{
    private static async Task<List<TokenizedRow>> Tokenize(byte[] bytes, char delimiter = ',')
    {
        var profile = BuiltInProfiles.InternalCsv with { Delimiter = delimiter };
        using var ms = new MemoryStream(bytes);
        var rows = new List<TokenizedRow>();
        await foreach (var r in new CsvRowTokenizer().TokenizeAsync(ms, profile))
            rows.Add(r);
        return rows;
    }

    private static Task<List<TokenizedRow>> Tokenize(string text, char delimiter = ',') =>
        Tokenize(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text), delimiter);

    [Fact]
    public async Task Quoted_delimiters_are_kept_inside_the_field()
    {
        var rows = await Tokenize("\"a,b\",c");
        var row = Assert.Single(rows);
        Assert.Equal(new[] { "a,b", "c" }, row.Fields);
    }

    [Fact]
    public async Task Escaped_quotes_collapse_to_a_single_quote()
    {
        var rows = await Tokenize("\"a\"\"b\",c");
        var row = Assert.Single(rows);
        Assert.Equal("a\"b", row.Fields[0]);
        Assert.Equal("c", row.Fields[1]);
    }

    [Fact]
    public async Task Newlines_inside_quotes_do_not_split_the_row()
    {
        var rows = await Tokenize("\"line1\nline2\",c");
        var row = Assert.Single(rows);
        Assert.Equal("line1\nline2", row.Fields[0]);
    }

    [Fact]
    public async Task Blank_lines_are_skipped()
    {
        var rows = await Tokenize("x,y\r\n\r\nz,w\r\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "x", "y" }, rows[0].Fields);
        Assert.Equal(new[] { "z", "w" }, rows[1].Fields);
    }

    [Fact]
    public async Task Both_crlf_and_lf_terminate_rows()
    {
        var rows = await Tokenize("a,b\r\nc,d\ne,f");
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "e", "f" }, rows[2].Fields);
    }

    [Fact]
    public async Task A_leading_utf8_bom_is_stripped_from_the_first_field()
    {
        // The BOM character encodes to the EF BB BF preamble at the head of the stream.
        var rows = await Tokenize("\uFEFFa,b");
        var row = Assert.Single(rows);
        Assert.Equal("a", row.Fields[0]); // not "\uFEFFa"
    }

    [Fact]
    public async Task A_trailing_record_without_a_newline_is_flushed()
    {
        var rows = await Tokenize("a,b");
        Assert.Single(rows);
    }

    [Fact]
    public async Task Physical_start_line_is_reported_per_row()
    {
        var rows = await Tokenize("a,b\r\nc,d");
        Assert.Equal(1, rows[0].LineNumber);
        Assert.Equal(2, rows[1].LineNumber);
    }
}
