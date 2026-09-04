using Idp.Application.Documents;
using Idp.Application.Extraction;
using Idp.Domain.Documents;
using Idp.Infrastructure.Extraction;

namespace Idp.UnitTests;

/// <summary>Spatial table detection groups words into rows/columns and stops at the totals block.</summary>
public class SpatialTableTests
{
    private static DocumentContent TableContent() => new OcrBuilder()
        .Row(("Description", 40), ("Qty", 380), ("UnitPrice", 470), ("LineTotal", 720))
        .Row(("Steel", 40), ("Bolts", 80), ("M10", 120), ("3", 380), ("12.50", 470), ("37.50", 720))
        .Row(("Hex", 40), ("Nuts", 80), ("M10", 120), ("10", 380), ("3.20", 470), ("32.00", 720))
        .Row(("Subtotal:", 40), ("69.50", 470))
        .Build();

    [Fact]
    public void Detects_columns_and_rows()
    {
        var table = new SpatialTableDetector().Detect(TableContent());
        Assert.NotNull(table);
        Assert.True(table!.Columns.Count >= 4);
        Assert.NotNull(table.FindColumn(h => h.Contains("desc")));
        Assert.NotNull(table.FindColumn(h => h.Contains("qty")));
        Assert.NotNull(table.FindColumn(h => h.Contains("price")));
        Assert.NotNull(table.FindColumn(h => h.Contains("total")));
    }

    [Fact]
    public void Stops_at_the_totals_block()
    {
        var table = new SpatialTableDetector().Detect(TableContent());
        Assert.Equal(2, table!.Rows.Count); // the Subtotal row is excluded
    }

    [Fact]
    public void Assigns_cells_to_the_correct_columns()
    {
        var table = new SpatialTableDetector().Detect(TableContent())!;
        var qtyCol = table.FindColumn(h => h.Contains("qty"))!.Value;
        var firstQty = table.Rows[0].First(c => c.Column == qtyCol).Text;
        Assert.Equal("3", firstQty);
    }

    [Fact]
    public void Extractor_reads_line_items_from_the_table()
    {
        var result = new DeterministicFieldExtractor()
            .Extract(DocumentType.Invoice, TableContent(), Array.Empty<ExtractionHint>());
        Assert.Equal(2, result.LineItems.Count);
        var first = result.LineItems[0];
        Assert.Equal(3m, first.Quantity);
        Assert.Equal(12.50m, first.UnitPrice);
        Assert.Equal(37.50m, first.LineTotal);
    }
}
