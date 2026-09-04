using Idp.Application.Documents;
using Idp.Application.Extraction;
using Idp.Application.Suppliers;
using Idp.Application.Validation;
using Idp.Domain.Documents;
using Idp.Domain.Suppliers;

namespace Idp.UnitTests;

/// <summary>Pass, fail and boundary coverage for every deterministic validation rule.</summary>
public class ValidationRuleTests
{
    private static readonly DateTime Now = new(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    private static ValidationContext Ctx(
        Document doc,
        IReadOnlyList<Supplier>? suppliers = null,
        IReadOnlyList<ExistingInvoiceKey>? existing = null,
        Document? po = null,
        Document? dn = null) =>
        new()
        {
            Document = doc,
            NowUtc = Now,
            KnownSuppliers = suppliers ?? Array.Empty<Supplier>(),
            ExistingInvoices = existing ?? Array.Empty<ExistingInvoiceKey>(),
            MatchingPurchaseOrder = po,
            MatchingDeliveryNote = dn
        };

    private static ValidationOutcome Outcome(IEnumerable<DocumentValidation> results) =>
        results.Select(r => r.Outcome).DefaultIfEmpty(ValidationOutcome.Pass).Max();

    // ---- Line item arithmetic ----------------------------------------------------------------

    [Fact]
    public void LineItemArithmetic_passes_when_totals_reconcile()
    {
        var doc = new DocBuilder(DocumentType.Invoice).Line(1, "A", 2, 10m, 20m).Build();
        var results = new LineItemArithmeticRule().Evaluate(Ctx(doc)).ToList();
        Assert.Equal(ValidationOutcome.Pass, Outcome(results));
    }

    [Fact]
    public void LineItemArithmetic_fails_when_total_is_wrong()
    {
        var doc = new DocBuilder(DocumentType.Invoice).Line(1, "A", 2, 10m, 25m).Build();
        var results = new LineItemArithmeticRule().Evaluate(Ctx(doc)).ToList();
        Assert.Equal(ValidationOutcome.Fail, Outcome(results));
    }

    [Fact]
    public void LineItemArithmetic_honours_the_tolerance_boundary()
    {
        // Tolerance = max(0.02, 20 * 0.01) = 0.20. +0.20 passes, +0.25 fails.
        var onBoundary = new DocBuilder(DocumentType.Invoice).Line(1, "A", 2, 10m, 20.20m).Build();
        var justOver = new DocBuilder(DocumentType.Invoice).Line(1, "A", 2, 10m, 20.25m).Build();
        Assert.Equal(ValidationOutcome.Pass, Outcome(new LineItemArithmeticRule().Evaluate(Ctx(onBoundary))));
        Assert.Equal(ValidationOutcome.Fail, Outcome(new LineItemArithmeticRule().Evaluate(Ctx(justOver))));
    }

    [Fact]
    public void LineItemArithmetic_warns_when_no_line_items()
    {
        var doc = new DocBuilder(DocumentType.Invoice).Build();
        var results = new LineItemArithmeticRule().Evaluate(Ctx(doc)).ToList();
        Assert.Equal(ValidationOutcome.Warn, Outcome(results));
    }

    // ---- Subtotal / Tax / Total --------------------------------------------------------------

    [Fact]
    public void Subtotal_passes_and_fails()
    {
        var ok = new DocBuilder(DocumentType.Invoice)
            .Line(1, "A", 2, 10m, 20m).Line(2, "B", 2, 10m, 20m)
            .Field(FieldKeys.Subtotal, "40").Build();
        var bad = new DocBuilder(DocumentType.Invoice)
            .Line(1, "A", 2, 10m, 20m).Line(2, "B", 2, 10m, 20m)
            .Field(FieldKeys.Subtotal, "50").Build();
        Assert.Equal(ValidationOutcome.Pass, Outcome(new SubtotalConsistencyRule().Evaluate(Ctx(ok))));
        Assert.Equal(ValidationOutcome.Fail, Outcome(new SubtotalConsistencyRule().Evaluate(Ctx(bad))));
    }

    [Fact]
    public void TaxCalculation_passes_and_fails()
    {
        var ok = new DocBuilder(DocumentType.Invoice)
            .Line(1, "A", 1, 100m, 100m, taxRate: 0.16m)
            .Field(FieldKeys.Subtotal, "100").Field(FieldKeys.Tax, "16").Build();
        var bad = new DocBuilder(DocumentType.Invoice)
            .Line(1, "A", 1, 100m, 100m, taxRate: 0.16m)
            .Field(FieldKeys.Subtotal, "100").Field(FieldKeys.Tax, "20").Build();
        Assert.Equal(ValidationOutcome.Pass, Outcome(new TaxCalculationRule().Evaluate(Ctx(ok))));
        Assert.Equal(ValidationOutcome.Fail, Outcome(new TaxCalculationRule().Evaluate(Ctx(bad))));
    }

    [Fact]
    public void Total_passes_and_fails()
    {
        var ok = new DocBuilder(DocumentType.Invoice)
            .Field(FieldKeys.Subtotal, "100").Field(FieldKeys.Tax, "16").Field(FieldKeys.Total, "116").Build();
        var bad = new DocBuilder(DocumentType.Invoice)
            .Field(FieldKeys.Subtotal, "100").Field(FieldKeys.Tax, "16").Field(FieldKeys.Total, "130").Build();
        Assert.Equal(ValidationOutcome.Pass, Outcome(new TotalConsistencyRule().Evaluate(Ctx(ok))));
        Assert.Equal(ValidationOutcome.Fail, Outcome(new TotalConsistencyRule().Evaluate(Ctx(bad))));
    }

    // ---- Date sanity -------------------------------------------------------------------------

    [Fact]
    public void DateSanity_passes_for_consistent_dates()
    {
        var doc = new DocBuilder(DocumentType.Invoice)
            .Field(FieldKeys.InvoiceDate, "2024-05-10").Field(FieldKeys.DueDate, "2024-06-10").Build();
        Assert.Equal(ValidationOutcome.Pass, Outcome(new DateSanityRule().Evaluate(Ctx(doc))));
    }

    [Fact]
    public void DateSanity_fails_for_future_invoice_date()
    {
        var doc = new DocBuilder(DocumentType.Invoice).Field(FieldKeys.InvoiceDate, "2024-12-31").Build();
        Assert.Equal(ValidationOutcome.Fail, Outcome(new DateSanityRule().Evaluate(Ctx(doc))));
    }

    [Fact]
    public void DateSanity_fails_when_due_precedes_invoice()
    {
        var doc = new DocBuilder(DocumentType.Invoice)
            .Field(FieldKeys.InvoiceDate, "2024-06-10").Field(FieldKeys.DueDate, "2024-05-10").Build();
        Assert.Equal(ValidationOutcome.Fail, Outcome(new DateSanityRule().Evaluate(Ctx(doc))));
    }

    [Fact]
    public void DateSanity_warns_when_invoice_date_missing()
    {
        var doc = new DocBuilder(DocumentType.Invoice).Build();
        Assert.Equal(ValidationOutcome.Warn, Outcome(new DateSanityRule().Evaluate(Ctx(doc))));
    }

    // ---- Duplicate invoice -------------------------------------------------------------------

    [Fact]
    public void DuplicateInvoice_fails_for_same_supplier_and_number()
    {
        var doc = new DocBuilder(DocumentType.Invoice)
            .Field(FieldKeys.SupplierName, "Rift Valley Supplies Ltd")
            .Field(FieldKeys.InvoiceNumber, "INV-1").Build();
        var key = new ExistingInvoiceKey(
            SupplierMatching.Normalize("Rift Valley Supplies Ltd"), "INV-1", Guid.NewGuid());
        Assert.Equal(ValidationOutcome.Fail,
            Outcome(new DuplicateInvoiceRule().Evaluate(Ctx(doc, existing: new[] { key }))));
    }

    [Fact]
    public void DuplicateInvoice_passes_when_no_prior_invoice()
    {
        var doc = new DocBuilder(DocumentType.Invoice)
            .Field(FieldKeys.SupplierName, "Rift Valley Supplies Ltd")
            .Field(FieldKeys.InvoiceNumber, "INV-1").Build();
        Assert.Equal(ValidationOutcome.Pass, Outcome(new DuplicateInvoiceRule().Evaluate(Ctx(doc))));
    }

    // ---- Currency ----------------------------------------------------------------------------

    [Fact]
    public void Currency_fails_when_missing()
    {
        var doc = new DocBuilder(DocumentType.Invoice).Build();
        Assert.Equal(ValidationOutcome.Fail, Outcome(new CurrencyConsistencyRule().Evaluate(Ctx(doc))));
    }

    [Fact]
    public void Currency_fails_when_unrecognised()
    {
        var doc = new DocBuilder(DocumentType.Invoice).Currency("XYZ").Build();
        Assert.Equal(ValidationOutcome.Fail, Outcome(new CurrencyConsistencyRule().Evaluate(Ctx(doc))));
    }

    [Fact]
    public void Currency_warns_on_supplier_default_mismatch()
    {
        var supplier = DocBuilder.MakeSupplier("Rift Valley Supplies Ltd", currency: "KES");
        var doc = new DocBuilder(DocumentType.Invoice)
            .Supplier(supplier.Id, "Rift Valley Supplies Ltd").Currency("USD").Build();
        Assert.Equal(ValidationOutcome.Warn,
            Outcome(new CurrencyConsistencyRule().Evaluate(Ctx(doc, suppliers: new[] { supplier }))));
    }

    [Fact]
    public void Currency_passes_when_consistent()
    {
        var supplier = DocBuilder.MakeSupplier("Rift Valley Supplies Ltd", currency: "KES");
        var doc = new DocBuilder(DocumentType.Invoice)
            .Supplier(supplier.Id, "Rift Valley Supplies Ltd").Currency("KES").Build();
        Assert.Equal(ValidationOutcome.Pass,
            Outcome(new CurrencyConsistencyRule().Evaluate(Ctx(doc, suppliers: new[] { supplier }))));
    }

    // ---- Supplier existence ------------------------------------------------------------------

    [Fact]
    public void SupplierExistence_passes_for_known_supplier()
    {
        var supplier = DocBuilder.MakeSupplier("Rift Valley Supplies Ltd");
        var doc = new DocBuilder(DocumentType.Invoice)
            .Field(FieldKeys.SupplierName, "Rift Valley Supplies Ltd").Build();
        Assert.Equal(ValidationOutcome.Pass,
            Outcome(new SupplierExistenceRule().Evaluate(Ctx(doc, suppliers: new[] { supplier }))));
    }

    [Fact]
    public void SupplierExistence_fails_for_unknown_supplier()
    {
        var supplier = DocBuilder.MakeSupplier("Rift Valley Supplies Ltd");
        var doc = new DocBuilder(DocumentType.Invoice)
            .Field(FieldKeys.SupplierName, "Totally Unrelated Trading House").Build();
        Assert.Equal(ValidationOutcome.Fail,
            Outcome(new SupplierExistenceRule().Evaluate(Ctx(doc, suppliers: new[] { supplier }))));
    }

    // ---- Tax id format -----------------------------------------------------------------------

    [Fact]
    public void TaxIdFormat_passes_for_wellformed_id()
    {
        var valid = Idp.Domain.Text.TaxIdFormat.Build("KE", "001234571");
        var doc = new DocBuilder(DocumentType.Invoice).Field(FieldKeys.SupplierTaxId, valid).Build();
        Assert.Equal(ValidationOutcome.Pass, Outcome(new TaxIdFormatRule().Evaluate(Ctx(doc))));
    }

    [Fact]
    public void TaxIdFormat_fails_for_bad_checksum()
    {
        var doc = new DocBuilder(DocumentType.Invoice).Field(FieldKeys.SupplierTaxId, "KE001234571Z").Build();
        Assert.Equal(ValidationOutcome.Fail, Outcome(new TaxIdFormatRule().Evaluate(Ctx(doc))));
    }

    [Fact]
    public void FullEngine_produces_findings_for_each_rule_family()
    {
        var doc = new DocBuilder(DocumentType.Invoice)
            .Field(FieldKeys.SupplierName, "Rift Valley Supplies Ltd")
            .Field(FieldKeys.InvoiceNumber, "INV-9").Field(FieldKeys.InvoiceDate, "2024-05-10")
            .Field(FieldKeys.DueDate, "2024-06-10").Currency("KES")
            .Field(FieldKeys.Subtotal, "100").Field(FieldKeys.Tax, "16").Field(FieldKeys.Total, "116")
            .Line(1, "A", 1, 100m, 100m, taxRate: 0.16m).Build();
        var supplier = DocBuilder.MakeSupplier("Rift Valley Supplies Ltd");
        var results = DeterministicValidationEngine.CreateDefault()
            .Validate(Ctx(doc, suppliers: new[] { supplier }));
        Assert.Contains(results, r => r.RuleName == "LineItemArithmetic");
        Assert.Contains(results, r => r.RuleName == "TotalConsistency");
        Assert.DoesNotContain(results, r => r.Outcome == ValidationOutcome.Fail);
    }
}
