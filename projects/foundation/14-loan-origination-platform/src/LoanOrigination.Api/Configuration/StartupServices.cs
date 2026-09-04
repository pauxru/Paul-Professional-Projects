using LoanOrigination.Application.Ports;
using LoanOrigination.Domain.Models;
using LoanOrigination.Domain.Rules;
using LoanOrigination.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LoanOrigination.Api.Configuration;

public sealed class SqliteReadinessHealthCheck(LoanDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await dbContext.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy("SQLite database is reachable.")
            : HealthCheckResult.Unhealthy("SQLite database is unavailable.");
}

public static class DemoDataSeeder
{
    public static async Task SeedAsync(ILoanRepository repository, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if ((await repository.ListRulesetsAsync(cancellationToken)).Count > 0)
        {
            return;
        }

        var ruleset = new RuleSetDefinition(
            "SME-CREDIT-POLICY",
            1,
            "SME and personal lending baseline policy",
            now,
            [
                new RuleDefinition(
                    "arrears-hard-stop",
                    1,
                    10,
                    ConditionDefinition.Compare(nameof(ApplicantFacts.PastArrearsCount), ComparisonOperator.GreaterThanOrEqual, 3),
                    new RuleOutcome(RuleOutcomeType.Fail, Detail: "Repeated arrears"),
                    "Decline applications with three or more historical arrears."),
                new RuleDefinition(
                    "stress-dsr-refer",
                    1,
                    20,
                    ConditionDefinition.Compare(nameof(ApplicantFacts.DebtServiceRatio), ComparisonOperator.GreaterThan, 0.45m),
                    new RuleOutcome(RuleOutcomeType.Refer, Detail: "Elevated existing DSR"),
                    "Refer applicants whose existing debt service exceeds 45% of net income."),
                new RuleDefinition(
                    "large-unsecured-document",
                    1,
                    30,
                    ConditionDefinition.All(
                        ConditionDefinition.Compare(nameof(ApplicantFacts.RequestedPrincipal), ComparisonOperator.GreaterThan, 500_000m),
                        ConditionDefinition.Compare(nameof(ApplicantFacts.CollateralValue), ComparisonOperator.LessThan, 500_000m)),
                    new RuleOutcome(RuleOutcomeType.RequireDocument, DocumentType: "CollateralEvidence"),
                    "Require collateral evidence for large unsecured exposure."),
                new RuleDefinition(
                    "new-business-limit",
                    1,
                    40,
                    ConditionDefinition.All(
                        ConditionDefinition.Compare(nameof(ApplicantFacts.IsSme), ComparisonOperator.Equals, true),
                        ConditionDefinition.Compare(nameof(ApplicantFacts.AgeOfBusinessMonths), ComparisonOperator.LessThan, 12)),
                    new RuleOutcome(RuleOutcomeType.AdjustLimit, Amount: 250_000m),
                    "Limit SMEs trading for less than twelve months to KES 250,000."),
                new RuleDefinition(
                    "bureau-rate-adjustment",
                    1,
                    50,
                    ConditionDefinition.Compare(nameof(ApplicantFacts.BureauGrade), ComparisonOperator.In, new[] { "C", "D" }),
                    new RuleOutcome(RuleOutcomeType.AdjustRate, Amount: 2.5m),
                    "Apply a transparent 2.5 percentage point risk-rate adjustment for bureau grades C and D."),
                new RuleDefinition(
                    "baseline-pass",
                    1,
                    100,
                    ConditionDefinition.Compare(nameof(ApplicantFacts.MonthlyNetIncome), ComparisonOperator.GreaterThan, 0m),
                    new RuleOutcome(RuleOutcomeType.Pass),
                    "Record the baseline positive-income check.")
            ]);
        await repository.AddRulesetAsync(ruleset, cancellationToken);

        var commonDocuments = new[]
        {
            new ProductDocumentRequirement("NationalId", true, 5_000_000),
            new ProductDocumentRequirement("BankStatement", true, 10_000_000, 90)
        };
        await repository.AddProductAsync(new LoanProductVersion(
            Guid.NewGuid(),
            "SME-FLEX",
            1,
            "SME Flex Working Capital (fictional)",
            50_000m,
            2_000_000m,
            3,
            36,
            18m,
            InterestRateMethod.ReducingBalance,
            "KES",
            ruleset.Id,
            ruleset.Version,
            [
                new ProductFee("ARRANGEMENT", 2m, true, FeeTreatment.Deducted, "Arrangement fee"),
                new ProductFee("INSURANCE", 1m, true, FeeTreatment.Capitalized, "Credit insurance")
            ],
            commonDocuments,
            false,
            now), cancellationToken);
        await repository.AddProductAsync(new LoanProductVersion(
            Guid.NewGuid(),
            "TRADE-USD",
            1,
            "USD Trade Finance Advance (fictional)",
            1_000m,
            50_000m,
            3,
            24,
            11m,
            InterestRateMethod.Flat,
            "USD",
            ruleset.Id,
            ruleset.Version,
            [new ProductFee("ARRANGEMENT", 1.5m, true, FeeTreatment.Deducted, "Arrangement fee")],
            [
                new ProductDocumentRequirement("BusinessRegistration", true, 5_000_000),
                new ProductDocumentRequirement("Invoice", true, 10_000_000)
            ],
            true,
            now), cancellationToken);

        await repository.AddCustomerAsync(new CustomerProfile(
            Guid.NewGuid(),
            CustomerKind.Sme,
            "Jua Kali Manufacturing Ltd (fictional)",
            null,
            "CPR-001-FAKE",
            "synthetic-bureau-b-pass",
            new ContactDetails("jua.kali@example.test", "+254700000014", "Nairobi, Kenya (synthetic)"),
            new IncomeDeclaration(180_000m, 65_000m, 42, "Manufacturing sales declaration"),
            [new ExistingObligation("Example Bank", 15_000m, 300_000m, 0)],
            0,
            KycStatus.NotStarted,
            now), cancellationToken);
        await repository.AddCustomerAsync(new CustomerProfile(
            Guid.NewGuid(),
            CustomerKind.Individual,
            "Nairobi Demo Clinic Owner (fictional)",
            new DateOnly(1988, 5, 12),
            null,
            "synthetic-refer-bureau-c",
            new ContactDetails("clinic.owner@example.test", "+254700000114", "Nairobi, Kenya (synthetic)"),
            new IncomeDeclaration(120_000m, 42_000m, 30, "Professional income declaration"),
            [],
            2,
            KycStatus.NotStarted,
            now), cancellationToken);
    }
}
