using System.Text;

using Contoso.Payments.Application.Reconciliation;

namespace Contoso.Payments.UnitTests.Application;

public class SettlementFileTests
{
    private static IReadOnlyList<SettlementRow> Base()
    {
        return new List<SettlementRow>
        {
            new("prov-1", Guid.NewGuid(), 1000L, "USD", "Captured"),
            new("prov-2", Guid.NewGuid(), 2500L, "USD", "Captured"),
            new("prov-3", Guid.NewGuid(), 3000L, "USD", "Captured")
        };
    }

    [Fact]
    public void Parser_reads_generated_baseline()
    {
        var baseRows = Base();
        var csv = SettlementFileGenerator.Build(baseRows, MismatchFlags.None);
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var parsed = SettlementFileParser.Parse(s);
        Assert.Equal(baseRows.Count, parsed.Count);
        Assert.Equal(baseRows[0].AmountMinor, parsed[0].AmountMinor);
    }

    [Fact]
    public void Generator_can_omit_one_row_for_MissingInProvider()
    {
        var baseRows = Base();
        var csv = SettlementFileGenerator.Build(baseRows, MismatchFlags.MissingInProvider);
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var parsed = SettlementFileParser.Parse(s);
        Assert.Equal(baseRows.Count - 1, parsed.Count);
    }

    [Fact]
    public void Generator_injects_amount_mismatch()
    {
        var baseRows = Base();
        var csv = SettlementFileGenerator.Build(baseRows, MismatchFlags.AmountMismatch);
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var parsed = SettlementFileParser.Parse(s);
        var providerRefs = baseRows.ToDictionary(r => r.ProviderReference, r => r.AmountMinor);
        Assert.Contains(parsed, p => providerRefs.TryGetValue(p.ProviderReference, out var orig) && orig != p.AmountMinor);
    }

    [Fact]
    public void Generator_duplicates_one_row_for_DuplicateInProvider()
    {
        var baseRows = Base();
        var csv = SettlementFileGenerator.Build(baseRows, MismatchFlags.DuplicateInProvider);
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var parsed = SettlementFileParser.Parse(s);
        var dup = parsed.GroupBy(r => r.ProviderReference).Any(g => g.Count() > 1);
        Assert.True(dup);
    }

    [Fact]
    public void Generator_appends_extra_row_for_MissingInternally()
    {
        var baseRows = Base();
        var csv = SettlementFileGenerator.Build(baseRows, MismatchFlags.MissingInternally);
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var parsed = SettlementFileParser.Parse(s);
        Assert.Equal(baseRows.Count + 1, parsed.Count);
    }
}
