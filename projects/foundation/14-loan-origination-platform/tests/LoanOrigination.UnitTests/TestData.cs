using LoanOrigination.Domain.Models;
using LoanOrigination.Domain.Rules;

namespace LoanOrigination.UnitTests;

public static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    public static ApplicantFacts Facts(
        decimal income = 100_000m,
        decimal debt = 10_000m,
        decimal requested = 100_000m,
        int arrears = 0,
        string grade = "B") =>
        new(35, income, 30_000m, debt, 1, 36, 24, arrears, 150_000m, requested, 12, grade, "Passed", false);

    public static RuleSetDefinition Ruleset(params RuleDefinition[] rules) =>
        new("test-policy", 1, "Test policy", Now, rules.Length == 0
            ? [new RuleDefinition("pass", 1, 1, ConditionDefinition.Compare(nameof(ApplicantFacts.MonthlyNetIncome), ComparisonOperator.GreaterThan, 0m), new RuleOutcome(RuleOutcomeType.Pass), "Positive income.")]
            : rules);

    public static LoanProductVersion Product(IReadOnlyList<ProductDocumentRequirement>? requirements = null) =>
        new(
            Guid.NewGuid(),
            "TEST-LOAN",
            1,
            "Test Loan",
            1_000m,
            1_000_000m,
            1,
            36,
            12m,
            InterestRateMethod.ReducingBalance,
            "KES",
            "test-policy",
            1,
            [],
            requirements ?? [new ProductDocumentRequirement("NationalId", true, 1_000_000)],
            false,
            Now);

    public static CustomerProfile Customer(string syntheticId = "synthetic-pass-bureau-b") =>
        new(
            Guid.NewGuid(),
            CustomerKind.Individual,
            "Test Applicant",
            new DateOnly(1990, 1, 1),
            null,
            syntheticId,
            new ContactDetails("test@example.test", "+254700000000", "Synthetic Street"),
            new IncomeDeclaration(100_000m, 30_000m, 24, "Salary"),
            [new ExistingObligation("Example Bank", 10_000m, 50_000m, 0)],
            1,
            KycStatus.NotStarted,
            Now);

    public static LoanApplication Application(
        ApplicationStage stage = ApplicationStage.Draft,
        KycStatus kycStatus = KycStatus.NotStarted,
        ApplicantFacts? facts = null,
        int version = 0) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "TEST-LOAN",
            1,
            "test-policy",
            1,
            facts ?? Facts(),
            100_000m,
            12,
            "KES",
            stage,
            Now,
            Now,
            null,
            [],
            [],
            kycStatus,
            null,
            null,
            null,
            null,
            version);
}
