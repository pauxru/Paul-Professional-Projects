using Idp.Application.Documents;
using Idp.Application.Extraction;
using Idp.Application.Suppliers;
using Idp.Domain.Documents;
using Idp.Domain.Text;

namespace Idp.Application.Validation;

internal static class RuleHelpers
{
    public static DocumentValidation Pass(string rule, string message, params string[] fields) =>
        new(rule, ValidationOutcome.Pass, message, fields);

    public static DocumentValidation Warn(string rule, string message, params string[] fields) =>
        new(rule, ValidationOutcome.Warn, message, fields);

    public static DocumentValidation Fail(string rule, string message, params string[] fields) =>
        new(rule, ValidationOutcome.Fail, message, fields);

    public static bool Within(decimal actual, decimal expected, ValidationTolerances t)
    {
        var allowed = Math.Max(t.ArithmeticAbsolute, Math.Abs(expected) * t.ArithmeticRelative);
        return Math.Abs(actual - expected) <= allowed;
    }
}

/// <summary>Each line total must equal quantity x unit price (within tolerance).</summary>
public sealed class LineItemArithmeticRule : IValidationRule
{
    public string Name => "LineItemArithmetic";

    public IEnumerable<DocumentValidation> Evaluate(ValidationContext ctx)
    {
        var lines = ctx.Document.LineItems;
        if (lines.Count == 0)
        {
            yield return RuleHelpers.Warn(Name, "No line items detected.");
            yield break;
        }

        var anyFail = false;
        foreach (var line in lines)
        {
            if (line.Quantity is not { } q || line.UnitPrice is not { } u ||
                line.LineTotal is not { } total)
                continue;
            var expected = decimal.Round(q * u, 2);
            if (!RuleHelpers.Within(total, expected, ctx.Tolerances))
            {
                anyFail = true;
                yield return RuleHelpers.Fail(Name,
                    $"Line {line.LineNumber}: total {total} != qty {q} x price {u} ({expected}).",
                    "lineItems");
            }
        }
        if (!anyFail)
            yield return RuleHelpers.Pass(Name, "All line totals reconcile.", "lineItems");
    }
}

/// <summary>Subtotal must equal the sum of line totals (within tolerance).</summary>
public sealed class SubtotalConsistencyRule : IValidationRule
{
    public string Name => "SubtotalConsistency";

    public IEnumerable<DocumentValidation> Evaluate(ValidationContext ctx)
    {
        var subtotal = ctx.Document.Amount(FieldKeys.Subtotal);
        if (subtotal is null) yield break;

        var sum = ctx.Document.LineItems
            .Where(l => l.LineTotal is not null)
            .Sum(l => l.LineTotal!.Value);
        if (RuleHelpers.Within(subtotal.Value, decimal.Round(sum, 2), ctx.Tolerances))
            yield return RuleHelpers.Pass(Name, $"Subtotal {subtotal} matches line sum {sum}.",
                FieldKeys.Subtotal);
        else
            yield return RuleHelpers.Fail(Name,
                $"Subtotal {subtotal} != sum of lines {sum}.", FieldKeys.Subtotal, "lineItems");
    }
}

/// <summary>Tax must equal subtotal x tax rate (within tolerance) where a rate is known.</summary>
public sealed class TaxCalculationRule : IValidationRule
{
    public string Name => "TaxCalculation";

    public IEnumerable<DocumentValidation> Evaluate(ValidationContext ctx)
    {
        var subtotal = ctx.Document.Amount(FieldKeys.Subtotal);
        var tax = ctx.Document.Amount(FieldKeys.Tax);
        if (subtotal is null || tax is null) yield break;

        var rate = ctx.Document.LineItems
            .Where(l => l.TaxRate is not null)
            .Select(l => l.TaxRate!.Value)
            .DefaultIfEmpty(0m)
            .GroupBy(r => r)
            .OrderByDescending(g => g.Count())
            .First().Key;

        if (rate <= 0m) yield break;

        var expected = decimal.Round(subtotal.Value * rate, 2);
        if (RuleHelpers.Within(tax.Value, expected, ctx.Tolerances))
            yield return RuleHelpers.Pass(Name,
                $"Tax {tax} matches {rate:P0} of subtotal ({expected}).", FieldKeys.Tax);
        else
            yield return RuleHelpers.Fail(Name,
                $"Tax {tax} != {rate:P0} of subtotal {subtotal} ({expected}).", FieldKeys.Tax);
    }
}

/// <summary>Total must equal subtotal + tax (within tolerance).</summary>
public sealed class TotalConsistencyRule : IValidationRule
{
    public string Name => "TotalConsistency";

    public IEnumerable<DocumentValidation> Evaluate(ValidationContext ctx)
    {
        var subtotal = ctx.Document.Amount(FieldKeys.Subtotal);
        var tax = ctx.Document.Amount(FieldKeys.Tax);
        var total = ctx.Document.Amount(FieldKeys.Total);
        if (total is null) yield break;

        if (subtotal is not null && tax is not null)
        {
            var expected = decimal.Round(subtotal.Value + tax.Value, 2);
            if (RuleHelpers.Within(total.Value, expected, ctx.Tolerances))
                yield return RuleHelpers.Pass(Name, $"Total {total} = subtotal + tax ({expected}).",
                    FieldKeys.Total);
            else
                yield return RuleHelpers.Fail(Name,
                    $"Total {total} != subtotal {subtotal} + tax {tax} ({expected}).",
                    FieldKeys.Total, FieldKeys.Subtotal, FieldKeys.Tax);
        }
        else
        {
            var sum = ctx.Document.LineItems.Where(l => l.LineTotal is not null)
                .Sum(l => l.LineTotal!.Value);
            if (sum > 0 && RuleHelpers.Within(total.Value, decimal.Round(sum, 2), ctx.Tolerances))
                yield return RuleHelpers.Pass(Name, $"Total {total} matches line sum {sum}.",
                    FieldKeys.Total);
        }
    }
}

/// <summary>Invoice date must be on/before the due date and not in the future.</summary>
public sealed class DateSanityRule : IValidationRule
{
    public string Name => "DateSanity";

    public IEnumerable<DocumentValidation> Evaluate(ValidationContext ctx)
    {
        var invoiceDate = ctx.Document.Date(FieldKeys.InvoiceDate);
        var dueDate = ctx.Document.Date(FieldKeys.DueDate);

        if (invoiceDate is null)
        {
            yield return RuleHelpers.Warn(Name, "No invoice date to validate.", FieldKeys.InvoiceDate);
            yield break;
        }

        if (invoiceDate.Value.Date > ctx.NowUtc.Date.AddDays(1))
        {
            yield return RuleHelpers.Fail(Name,
                $"Invoice date {invoiceDate:yyyy-MM-dd} is in the future.", FieldKeys.InvoiceDate);
            yield break;
        }

        if (dueDate is not null && dueDate.Value.Date < invoiceDate.Value.Date)
        {
            yield return RuleHelpers.Fail(Name,
                $"Due date {dueDate:yyyy-MM-dd} precedes invoice date {invoiceDate:yyyy-MM-dd}.",
                FieldKeys.DueDate, FieldKeys.InvoiceDate);
            yield break;
        }

        yield return RuleHelpers.Pass(Name, "Dates are consistent.",
            FieldKeys.InvoiceDate, FieldKeys.DueDate);
    }
}

/// <summary>Same supplier + invoice number must not already exist (duplicate submission).</summary>
public sealed class DuplicateInvoiceRule : IValidationRule
{
    public string Name => "DuplicateInvoice";

    public IEnumerable<DocumentValidation> Evaluate(ValidationContext ctx)
    {
        if (ctx.Document.DocumentType != DocumentType.Invoice) yield break;
        var number = ctx.Document.Text(FieldKeys.InvoiceNumber);
        if (string.IsNullOrWhiteSpace(number)) yield break;

        var supplierKey = SupplierKey(ctx.Document);
        var duplicate = ctx.ExistingInvoices.FirstOrDefault(k =>
            k.DocumentId != ctx.Document.Id &&
            string.Equals(k.InvoiceNumber, number, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(k.SupplierKey, supplierKey, StringComparison.OrdinalIgnoreCase));

        if (duplicate is not null)
            yield return RuleHelpers.Fail(Name,
                $"Duplicate invoice '{number}' for supplier '{supplierKey}' (doc {duplicate.DocumentId}).",
                FieldKeys.InvoiceNumber, FieldKeys.SupplierName);
        else
            yield return RuleHelpers.Pass(Name, $"Invoice '{number}' is not a duplicate.",
                FieldKeys.InvoiceNumber);
    }

    public static string SupplierKey(Document doc) =>
        doc.SupplierId?.ToString()
        ?? SupplierMatching.Normalize(doc.Text(FieldKeys.SupplierName) ?? "unknown");
}

/// <summary>Currency must be present, a recognised code, and consistent with supplier default.</summary>
public sealed class CurrencyConsistencyRule : IValidationRule
{
    public string Name => "CurrencyConsistency";
    private static readonly HashSet<string> Known =
        new(StringComparer.OrdinalIgnoreCase) { "KES", "USD", "EUR", "GBP" };

    public IEnumerable<DocumentValidation> Evaluate(ValidationContext ctx)
    {
        var currency = ctx.Document.Currency ?? ctx.Document.Text(FieldKeys.Currency);
        if (string.IsNullOrWhiteSpace(currency))
        {
            yield return RuleHelpers.Fail(Name, "Currency is missing.", FieldKeys.Currency);
            yield break;
        }
        if (!Known.Contains(currency))
        {
            yield return RuleHelpers.Fail(Name, $"Unrecognised currency '{currency}'.",
                FieldKeys.Currency);
            yield break;
        }

        var supplier = ctx.KnownSuppliers.FirstOrDefault(s => s.Id == ctx.Document.SupplierId);
        if (supplier?.DefaultCurrency is { } def &&
            !string.Equals(def, currency, StringComparison.OrdinalIgnoreCase))
        {
            yield return RuleHelpers.Warn(Name,
                $"Currency '{currency}' differs from supplier default '{def}'.", FieldKeys.Currency);
            yield break;
        }
        yield return RuleHelpers.Pass(Name, $"Currency '{currency}' is consistent.",
            FieldKeys.Currency);
    }
}

/// <summary>Supplier must exist in master data (fuzzy name match above threshold).</summary>
public sealed class SupplierExistenceRule : IValidationRule
{
    public string Name => "SupplierExistence";

    public IEnumerable<DocumentValidation> Evaluate(ValidationContext ctx)
    {
        var name = ctx.Document.Text(FieldKeys.SupplierName);
        if (string.IsNullOrWhiteSpace(name))
        {
            yield return RuleHelpers.Fail(Name, "No supplier name extracted.", FieldKeys.SupplierName);
            yield break;
        }

        var (match, score) = SupplierMatching.BestMatch(name, ctx.KnownSuppliers);
        if (match is not null && score >= ctx.Tolerances.SupplierMatchThreshold)
            yield return RuleHelpers.Pass(Name,
                $"Supplier '{name}' matched '{match.Name}' (score {score:0.00}).",
                FieldKeys.SupplierName);
        else
            yield return RuleHelpers.Fail(Name,
                $"Supplier '{name}' not found in master data (best score {score:0.00}).",
                FieldKeys.SupplierName);
    }
}

/// <summary>Tax id (when present) must satisfy the checksum-style format.</summary>
public sealed class TaxIdFormatRule : IValidationRule
{
    public string Name => "TaxIdFormat";

    public IEnumerable<DocumentValidation> Evaluate(ValidationContext ctx)
    {
        var taxId = ctx.Document.Text(FieldKeys.SupplierTaxId);
        if (string.IsNullOrWhiteSpace(taxId)) yield break;

        if (TaxIdFormat.IsValid(taxId))
            yield return RuleHelpers.Pass(Name, $"Tax id '{taxId}' is well-formed.",
                FieldKeys.SupplierTaxId);
        else
            yield return RuleHelpers.Fail(Name, $"Tax id '{taxId}' fails checksum/format.",
                FieldKeys.SupplierTaxId);
    }
}
