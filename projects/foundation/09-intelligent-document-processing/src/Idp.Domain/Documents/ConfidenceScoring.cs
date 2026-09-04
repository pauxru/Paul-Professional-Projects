namespace Idp.Domain.Documents;

/// <summary>
/// Pure document-level confidence aggregation. Kept in the domain because the aggregation method
/// is a core, testable invariant of the guardrail layer (documented in ADR-003).
///
/// Method: the document score is the confidence of the weakest *required* field (a conservative
/// minimum), blended 70/30 with the mean of all required-field confidences, then multiplied by the
/// classifier confidence, then penalised for hard validation failures. Taking the weakest field
/// into account means one badly-extracted critical field cannot be masked by many good ones.
/// </summary>
public static class ConfidenceScoring
{
    public const double ValidationFailurePenalty = 0.35;
    public const double ValidationWarningPenalty = 0.08;

    public static double AggregateDocumentConfidence(
        IReadOnlyCollection<double> requiredFieldConfidences,
        double classificationConfidence,
        int hardFailures,
        int warnings)
    {
        double fieldComponent;
        if (requiredFieldConfidences.Count == 0)
        {
            fieldComponent = 0.0;
        }
        else
        {
            var min = double.MaxValue;
            var sum = 0.0;
            foreach (var c in requiredFieldConfidences)
            {
                var clamped = Clamp01(c);
                if (clamped < min) min = clamped;
                sum += clamped;
            }
            var mean = sum / requiredFieldConfidences.Count;
            fieldComponent = (0.7 * min) + (0.3 * mean);
        }

        var score = fieldComponent * Clamp01(classificationConfidence);
        score -= hardFailures * ValidationFailurePenalty;
        score -= warnings * ValidationWarningPenalty;
        return Clamp01(score);
    }

    public static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
}
