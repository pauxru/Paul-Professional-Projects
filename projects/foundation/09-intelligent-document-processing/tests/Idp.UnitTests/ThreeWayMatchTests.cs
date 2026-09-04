using Idp.Application.Extraction;
using Idp.Application.Validation;
using Idp.Domain.Documents;

namespace Idp.UnitTests;

/// <summary>Three-way match: clean, over-billing, partial/over delivery, total variance, missing PO.</summary>
public class ThreeWayMatchTests
{
    private const string Item = "Steel Bolts M10";

    private static ValidationContext Ctx(Document invoice, Document? po, Document? dn) =>
        new() { Document = invoice, MatchingPurchaseOrder = po, MatchingDeliveryNote = dn };

    private static Document Invoice(decimal qty, decimal total) =>
        new DocBuilder(DocumentType.Invoice)
            .Field(FieldKeys.PoReference, "PO-5000")
            .Field(FieldKeys.Total, total.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Line(1, Item, qty, 12.5m, decimal.Round(qty * 12.5m, 2)).Build();

    private static Document Po(decimal qty, decimal total) =>
        new DocBuilder(DocumentType.PurchaseOrder)
            .Field(FieldKeys.Total, total.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Line(1, Item, qty, 12.5m, decimal.Round(qty * 12.5m, 2)).Build();

    private static Document Dn(decimal qty) =>
        new DocBuilder(DocumentType.DeliveryNote).Line(1, Item, qty, 12.5m, decimal.Round(qty * 12.5m, 2)).Build();

    private static ValidationOutcome Outcome(IEnumerable<DocumentValidation> r) =>
        r.Select(x => x.Outcome).DefaultIfEmpty(ValidationOutcome.Pass).Max();

    [Fact]
    public void Clean_three_way_match_passes()
    {
        var results = new ThreeWayMatchRule().Evaluate(Ctx(Invoice(10, 125m), Po(10, 125m), Dn(10))).ToList();
        Assert.Equal(ValidationOutcome.Pass, Outcome(results));
    }

    [Fact]
    public void Over_billing_beyond_ordered_fails()
    {
        var results = new ThreeWayMatchRule().Evaluate(Ctx(Invoice(20, 125m), Po(10, 125m), Dn(10))).ToList();
        Assert.Equal(ValidationOutcome.Fail, Outcome(results));
        Assert.Contains(results, r => r.Outcome == ValidationOutcome.Fail && r.Message.Contains("ordered"));
    }

    [Fact]
    public void Invoice_total_exceeding_po_total_fails()
    {
        var results = new ThreeWayMatchRule().Evaluate(Ctx(Invoice(10, 200m), Po(10, 125m), Dn(10))).ToList();
        Assert.Contains(results, r => r.Outcome == ValidationOutcome.Fail && r.Message.Contains("PO total"));
    }

    [Fact]
    public void Partial_delivery_is_a_warning_not_a_failure()
    {
        var results = new ThreeWayMatchRule().Evaluate(Ctx(Invoice(5, 62.5m), Po(10, 125m), Dn(5))).ToList();
        Assert.Equal(ValidationOutcome.Warn, Outcome(results));
        Assert.Contains(results, r => r.Message.Contains("partial delivery"));
    }

    [Fact]
    public void Over_delivery_is_a_warning()
    {
        var results = new ThreeWayMatchRule().Evaluate(Ctx(Invoice(10, 125m), Po(10, 125m), Dn(12))).ToList();
        Assert.Contains(results, r => r.Outcome == ValidationOutcome.Warn && r.Message.Contains("over-delivery"));
        Assert.DoesNotContain(results, r => r.Outcome == ValidationOutcome.Fail);
    }

    [Fact]
    public void Billing_more_than_delivered_fails()
    {
        var results = new ThreeWayMatchRule().Evaluate(Ctx(Invoice(10, 125m), Po(10, 125m), Dn(6))).ToList();
        Assert.Contains(results, r => r.Outcome == ValidationOutcome.Fail && r.Message.Contains("delivered"));
    }

    [Fact]
    public void Missing_purchase_order_warns()
    {
        var results = new ThreeWayMatchRule().Evaluate(Ctx(Invoice(10, 125m), po: null, dn: null)).ToList();
        Assert.Equal(ValidationOutcome.Warn, Outcome(results));
        Assert.Contains(results, r => r.Message.Contains("no matching purchase order"));
    }

    [Fact]
    public void No_po_reference_produces_no_findings()
    {
        var invoice = new DocBuilder(DocumentType.Invoice).Line(1, Item, 10, 12.5m, 125m).Build();
        var results = new ThreeWayMatchRule().Evaluate(Ctx(invoice, Po(10, 125m), Dn(10))).ToList();
        Assert.Empty(results);
    }
}
