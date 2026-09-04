namespace Idp.Domain.Documents;

/// <summary>A detected table line item (invoice/PO/delivery-note rows).</summary>
public sealed class LineItem
{
    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public int LineNumber { get; private set; }
    public string? Description { get; private set; }
    public decimal? Quantity { get; private set; }
    public decimal? UnitPrice { get; private set; }
    public decimal? LineTotal { get; private set; }
    public decimal? TaxRate { get; private set; }
    public double Confidence { get; private set; }

    private LineItem() { }

    public LineItem(
        int lineNumber,
        string? description,
        decimal? quantity,
        decimal? unitPrice,
        decimal? lineTotal,
        decimal? taxRate,
        double confidence)
    {
        Id = Guid.NewGuid();
        LineNumber = lineNumber;
        Description = description;
        Quantity = quantity;
        UnitPrice = unitPrice;
        LineTotal = lineTotal;
        TaxRate = taxRate;
        Confidence = ConfidenceScoring.Clamp01(confidence);
    }

    /// <summary>Expected line total from quantity x unit price, when both are present.</summary>
    public decimal? ComputedLineTotal =>
        Quantity is { } q && UnitPrice is { } u ? decimal.Round(q * u, 2) : null;

    internal void AttachTo(Guid documentId) => DocumentId = documentId;
}
