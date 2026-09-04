using Idp.Application.Documents;
using Idp.Application.Extraction;
using Idp.Domain.Documents;

namespace Idp.Application.Validation;

/// <summary>
/// Three-way match: invoice ↔ purchase order ↔ delivery note. Tolerates quantity/price variance and
/// partial deliveries, but fails on over-billing (invoice &gt; PO ordered) and on invoicing more than
/// was delivered. Over-delivery is surfaced as a warning.
/// </summary>
public sealed class ThreeWayMatchRule : IValidationRule
{
    public string Name => "ThreeWayMatch";

    public IEnumerable<DocumentValidation> Evaluate(ValidationContext ctx)
    {
        if (ctx.Document.DocumentType != DocumentType.Invoice) yield break;

        var poRef = ctx.Document.Text(FieldKeys.PoReference);
        if (string.IsNullOrWhiteSpace(poRef)) yield break;

        var po = ctx.MatchingPurchaseOrder;
        if (po is null)
        {
            yield return RuleHelpers.Warn(Name,
                $"Invoice references PO '{poRef}' but no matching purchase order was found.",
                FieldKeys.PoReference);
            yield break;
        }

        var findings = new List<DocumentValidation>();
        var t = ctx.Tolerances;

        // 1. Header total vs PO total (invoice may be lower for a partial bill, not higher).
        var invoiceTotal = ctx.Document.Amount(FieldKeys.Total);
        var poTotal = po.Amount(FieldKeys.Total);
        if (invoiceTotal is { } it && poTotal is { } pt && pt > 0)
        {
            if (it > pt * (1 + t.PriceVariancePercent))
                findings.Add(RuleHelpers.Fail(Name,
                    $"Invoice total {it} exceeds PO total {pt} beyond tolerance.",
                    FieldKeys.Total, FieldKeys.PoReference));
        }

        // 2. Line-level checks against PO (ordered) and DN (delivered).
        var dn = ctx.MatchingDeliveryNote;
        foreach (var invLine in ctx.Document.LineItems)
        {
            if (invLine.Quantity is not { } invQty || invLine.Description is null) continue;
            var poLine = MatchLine(po, invLine.Description);
            var dnLine = dn is null ? null : MatchLine(dn, invLine.Description);

            if (poLine?.Quantity is { } poQty && poQty > 0)
            {
                if (invQty > poQty * (1 + t.QuantityVariancePercent))
                    findings.Add(RuleHelpers.Fail(Name,
                        $"Line '{invLine.Description}': billed {invQty} > ordered {poQty}.",
                        "lineItems"));

                if (dnLine?.Quantity is { } delQty)
                {
                    if (delQty > poQty * (1 + t.QuantityVariancePercent))
                        findings.Add(RuleHelpers.Warn(Name,
                            $"Line '{invLine.Description}': over-delivery {delQty} > ordered {poQty}.",
                            "lineItems"));
                    else if (delQty < poQty * (1 - t.QuantityVariancePercent))
                        findings.Add(RuleHelpers.Warn(Name,
                            $"Line '{invLine.Description}': partial delivery {delQty} of {poQty}.",
                            "lineItems"));

                    if (invQty > delQty * (1 + t.QuantityVariancePercent))
                        findings.Add(RuleHelpers.Fail(Name,
                            $"Line '{invLine.Description}': billed {invQty} > delivered {delQty}.",
                            "lineItems"));
                }
            }
        }

        if (findings.Count == 0)
            yield return RuleHelpers.Pass(Name,
                $"Invoice reconciles with PO '{poRef}'" +
                (dn is null ? " (no delivery note)." : " and delivery note."),
                FieldKeys.PoReference);
        else
            foreach (var f in findings) yield return f;
    }

    private static LineItem? MatchLine(Document document, string description)
    {
        var norm = Normalize(description);
        return document.LineItems.FirstOrDefault(l =>
                   l.Description is not null && Normalize(l.Description) == norm)
               ?? document.LineItems.FirstOrDefault(l =>
                   l.Description is not null &&
                   (Normalize(l.Description).Contains(norm) || norm.Contains(Normalize(l.Description))));
    }

    private static string Normalize(string s) =>
        new string(s.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
