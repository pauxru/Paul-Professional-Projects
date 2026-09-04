using Idp.Domain.Documents;
using Idp.Domain.Suppliers;

namespace Idp.Application.Validation;

/// <summary>Tolerances used by the arithmetic and three-way-match rules (bound from config).</summary>
public sealed record ValidationTolerances(
    decimal ArithmeticAbsolute,
    decimal ArithmeticRelative,
    decimal QuantityVariancePercent,
    decimal PriceVariancePercent,
    double SupplierMatchThreshold)
{
    public static ValidationTolerances Default => new(
        ArithmeticAbsolute: 0.02m,
        ArithmeticRelative: 0.01m,
        QuantityVariancePercent: 0.05m,
        PriceVariancePercent: 0.05m,
        SupplierMatchThreshold: 0.86);
}

/// <summary>A known invoice identity used for duplicate detection.</summary>
public sealed record ExistingInvoiceKey(string SupplierKey, string InvoiceNumber, Guid DocumentId);

/// <summary>
/// Everything a validation rule needs, gathered by the pipeline before running the guardrail layer.
/// Rules are pure functions of this context, which keeps them fully unit testable without a database.
/// </summary>
public sealed class ValidationContext
{
    public required Document Document { get; init; }
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<Supplier> KnownSuppliers { get; init; } = Array.Empty<Supplier>();
    public IReadOnlyList<ExistingInvoiceKey> ExistingInvoices { get; init; } =
        Array.Empty<ExistingInvoiceKey>();
    public Document? MatchingPurchaseOrder { get; init; }
    public Document? MatchingDeliveryNote { get; init; }
    public ValidationTolerances Tolerances { get; init; } = ValidationTolerances.Default;
}

/// <summary>A single deterministic validation rule in the guardrail layer.</summary>
public interface IValidationRule
{
    string Name { get; }
    IEnumerable<DocumentValidation> Evaluate(ValidationContext context);
}

/// <summary>Runs all applicable rules and returns their findings.</summary>
public interface IValidationEngine
{
    IReadOnlyList<DocumentValidation> Validate(ValidationContext context);
}
