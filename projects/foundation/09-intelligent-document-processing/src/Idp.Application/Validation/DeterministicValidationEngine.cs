using Idp.Domain.Documents;
using Microsoft.Extensions.Logging;

namespace Idp.Application.Validation;

/// <summary>
/// Runs the full set of deterministic validation rules and returns their findings. Rules are pure
/// functions of the <see cref="ValidationContext"/>; a rule that throws is isolated so one broken
/// rule cannot fail the whole guardrail pass.
/// </summary>
public sealed class DeterministicValidationEngine : IValidationEngine
{
    private readonly IReadOnlyList<IValidationRule> _rules;
    private readonly ILogger<DeterministicValidationEngine>? _logger;

    public DeterministicValidationEngine(
        IEnumerable<IValidationRule> rules,
        ILogger<DeterministicValidationEngine>? logger = null)
    {
        _rules = rules.ToList();
        _logger = logger;
    }

    /// <summary>The standard rule set in a fixed, documented order.</summary>
    public static DeterministicValidationEngine CreateDefault() => new(new IValidationRule[]
    {
        new LineItemArithmeticRule(),
        new SubtotalConsistencyRule(),
        new TaxCalculationRule(),
        new TotalConsistencyRule(),
        new DateSanityRule(),
        new DuplicateInvoiceRule(),
        new CurrencyConsistencyRule(),
        new SupplierExistenceRule(),
        new TaxIdFormatRule(),
        new ThreeWayMatchRule()
    });

    public IReadOnlyList<DocumentValidation> Validate(ValidationContext context)
    {
        var results = new List<DocumentValidation>();
        foreach (var rule in _rules)
        {
            try
            {
                results.AddRange(rule.Evaluate(context));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Validation rule {Rule} threw", rule.Name);
                results.Add(new DocumentValidation(rule.Name, ValidationOutcome.Warn,
                    $"Rule '{rule.Name}' could not be evaluated: {ex.Message}"));
            }
        }
        return results;
    }
}
