using LoanOrigination.Domain.Models;

namespace LoanOrigination.Domain.Risk;

public sealed record BureauReport(string Grade, int ArrearsCount, decimal OutstandingBalance, string Reason);

public sealed record ScoreContribution(
    string Factor,
    decimal Weight,
    decimal NormalizedValue,
    decimal Points,
    string Explanation);

public sealed record RiskAssessment(
    string ScorecardVersion,
    int Score,
    string Band,
    IReadOnlyList<ScoreContribution> Contributions);

public sealed class WeightedScorecard
{
    public const string Version = "scorecard-2026.1";

    public RiskAssessment Evaluate(ApplicantFacts facts, BureauReport bureau)
    {
        var contributions = new[]
        {
            Contribution("Age of business", 15m, BusinessAgeValue(facts), "Business maturity reduces operating-history uncertainty."),
            Contribution("Income stability", 20m, IncomeStabilityValue(facts), "Longer evidenced income stability receives more points."),
            Contribution("Debt service ratio", 25m, DsrValue(facts), "Lower existing debt-service ratio receives more points."),
            Contribution("Past arrears", 20m, ArrearsValue(facts, bureau), "No arrears receives full points; repeated arrears reduces points."),
            Contribution("Collateral coverage", 10m, CollateralValue(facts), "Collateral relative to requested principal reduces loss severity."),
            Contribution("Bureau simulator grade", 10m, BureauValue(bureau), "Deterministic synthetic bureau grade is one transparent factor.")
        };
        var score = (int)decimal.Round(contributions.Sum(contribution => contribution.Points), 0, MidpointRounding.AwayFromZero);
        return new RiskAssessment(Version, score, BandFor(score), contributions);
    }

    public static string BandFor(int score) => score switch
    {
        >= 85 => "A",
        >= 70 => "B",
        >= 55 => "C",
        >= 40 => "D",
        _ => "E"
    };

    private static ScoreContribution Contribution(
        string factor,
        decimal weight,
        decimal normalized,
        string explanation)
    {
        var constrained = decimal.Clamp(normalized, 0m, 1m);
        return new ScoreContribution(
            factor,
            weight,
            constrained,
            decimal.Round(weight * constrained, 2, MidpointRounding.AwayFromZero),
            explanation);
    }

    private static decimal BusinessAgeValue(ApplicantFacts facts) =>
        facts.IsSme ? decimal.Min(1m, facts.AgeOfBusinessMonths / 60m) : 0.8m;

    private static decimal IncomeStabilityValue(ApplicantFacts facts) =>
        decimal.Min(1m, facts.IncomeStabilityMonths / 24m);

    private static decimal DsrValue(ApplicantFacts facts) => facts.DebtServiceRatio switch
    {
        <= 0.20m => 1m,
        <= 0.35m => 0.8m,
        <= 0.50m => 0.5m,
        <= 0.70m => 0.2m,
        _ => 0m
    };

    private static decimal ArrearsValue(ApplicantFacts facts, BureauReport bureau)
    {
        var arrears = Math.Max(facts.PastArrearsCount, bureau.ArrearsCount);
        return arrears switch
        {
            0 => 1m,
            1 => 0.65m,
            2 => 0.35m,
            _ => 0m
        };
    }

    private static decimal CollateralValue(ApplicantFacts facts)
    {
        if (facts.RequestedPrincipal <= 0m)
        {
            return 0m;
        }

        return decimal.Min(1m, facts.CollateralValue / facts.RequestedPrincipal);
    }

    private static decimal BureauValue(BureauReport bureau) => bureau.Grade.ToUpperInvariant() switch
    {
        "A" => 1m,
        "B" => 0.75m,
        "C" => 0.5m,
        "D" => 0.25m,
        _ => 0m
    };
}
