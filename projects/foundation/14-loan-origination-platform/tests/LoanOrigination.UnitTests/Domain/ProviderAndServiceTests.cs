using LoanOrigination.Application.Contracts;
using LoanOrigination.Application.Ports;
using LoanOrigination.Application.Services;
using LoanOrigination.Domain.Models;
using LoanOrigination.Domain.Rules;
using LoanOrigination.Domain.Workflow;
using LoanOrigination.Infrastructure.Providers;

namespace LoanOrigination.UnitTests.Domain;

public sealed class ProviderAndServiceTests
{
    [Theory]
    [InlineData("synthetic-pass", KycProviderOutcome.Pass)]
    [InlineData("synthetic-refer", KycProviderOutcome.Refer)]
    [InlineData("synthetic-fail", KycProviderOutcome.Fail)]
    [InlineData("synthetic-pep-hit", KycProviderOutcome.PepHit)]
    [InlineData("synthetic-sanctions-hit", KycProviderOutcome.SanctionsHit)]
    [InlineData("synthetic-provider-timeout", KycProviderOutcome.Timeout)]
    public async Task KycProvider_SyntheticPattern_ReturnsDeterministicOutcome(string identity, KycProviderOutcome expected)
    {
        var result = await new DeterministicKycProvider().ScreenAsync(identity, CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public async Task RunKyc_Timeout_RetriesTwiceAndKeepsApplicationInKyc()
    {
        var fixture = await BuildKycFixtureAsync("synthetic-provider-timeout");

        var result = await fixture.Service.RunKycAsync(fixture.Application.Id, "analyst", "corr", "127.0.0.1", "test", CancellationToken.None);

        Assert.Equal(2, result.Attempts);
        Assert.Equal(ApplicationStage.KycInProgress, result.Application.Stage);
        Assert.Contains("kyc.screened", fixture.Audit.Actions);
    }

    [Fact]
    public async Task OverrideKyc_WithElevatedPermission_TransitionsAndAudits()
    {
        var fixture = await BuildKycFixtureAsync("synthetic-provider-timeout");

        var updated = await fixture.Service.OverrideKycAsync(
            fixture.Application.Id,
            new KycOverrideRequest(KycStatus.Passed, "Verified from controlled fallback evidence."),
            true,
            "admin",
            "corr",
            "127.0.0.1",
            "test",
            CancellationToken.None);

        Assert.Equal(ApplicationStage.Screening, updated.Stage);
        Assert.Equal(KycStatus.ManuallyOverridden, updated.KycStatus);
        Assert.Contains("kyc.manually-overridden", fixture.Audit.Actions);
    }

    [Fact]
    public async Task OverrideKyc_WithoutElevatedPermission_Rejects()
    {
        var fixture = await BuildKycFixtureAsync("synthetic-provider-timeout");

        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.OverrideKycAsync(
            fixture.Application.Id,
            new KycOverrideRequest(KycStatus.Passed, "No authority"),
            false,
            "analyst",
            "corr",
            "127.0.0.1",
            "test",
            CancellationToken.None));
    }

    [Fact]
    public async Task DisbursementProvider_RepeatedReference_ReturnsOriginalResult()
    {
        var provider = new DeterministicDisbursementProvider();

        var first = await provider.SendAsync("transfer-1", 100_000m, "BankTransfer", CancellationToken.None);
        var second = await provider.SendAsync("transfer-1", 99_000m, "BankTransfer", CancellationToken.None);

        Assert.Equal(DisbursementStatus.Succeeded, first.Status);
        Assert.Equal(first, second);
        Assert.Equal(100_000m, second.Amount);
    }

    [Fact]
    public async Task DisbursementProvider_FailureReference_ReturnsFailure()
    {
        var result = await new DeterministicDisbursementProvider().SendAsync("transfer-fail", 100m, "MobileMoney", CancellationToken.None);

        Assert.Equal(DisbursementStatus.Failed, result.Status);
        Assert.Equal(0m, result.Amount);
    }

    [Fact]
    public async Task DisbursementProvider_PendingReference_ReturnsPending()
    {
        var result = await new DeterministicDisbursementProvider().SendAsync("transfer-pending", 100m, "MobileMoney", CancellationToken.None);

        Assert.Equal(DisbursementStatus.Pending, result.Status);
    }

    [Fact]
    public async Task HandleCallback_Reversal_PersistsReversedStatus()
    {
        var repository = new InMemoryLoanRepository();
        var clock = new FakeClock(TestData.Now);
        var audit = new CollectingAuditWriter();
        var application = TestData.Application(ApplicationStage.Disbursed);
        await repository.AddApplicationAsync(application, CancellationToken.None);
        var issued = OfferLifecycle.Create(application.Id, 1, 100_000m, 12m, 12, InterestRateMethod.ReducingBalance, "KES", [], TestData.Now, TimeSpan.FromDays(7));
        var offer = OfferLifecycle.Accept(issued, "applicant", TestData.Now);
        await repository.AddOfferAsync(offer, CancellationToken.None);
        await repository.AddDisbursementAsync(new DisbursementRecord(Guid.NewGuid(), application.Id, offer.Id, "transfer-reversal", 100_000m, 100_000m, "BankTransfer", DisbursementStatus.Succeeded, 1, TestData.Now, TestData.Now), CancellationToken.None);
        var service = new DisbursementService(repository, new DeterministicDisbursementProvider(), clock, audit);

        var record = await service.HandleCallbackAsync(
            new DisbursementCallbackRequest("transfer-reversal", DisbursementStatus.Reversed, 100_000m, "Synthetic reversal"),
            "provider",
            "corr",
            "127.0.0.1",
            "test",
            CancellationToken.None);

        Assert.Equal(DisbursementStatus.Reversed, record.Status);
        Assert.False(record.IsReconciled);
        Assert.Contains("disbursement.callback", audit.Actions);
    }

    private static async Task<(LoanApplicationService Service, LoanApplication Application, CollectingAuditWriter Audit)> BuildKycFixtureAsync(string identity)
    {
        var repository = new InMemoryLoanRepository();
        var clock = new FakeClock(TestData.Now);
        var audit = new CollectingAuditWriter();
        var customer = TestData.Customer(identity);
        var product = TestData.Product([]);
        var ruleset = TestData.Ruleset();
        var application = TestData.Application(ApplicationStage.KycInProgress, KycStatus.InProgress) with { CustomerId = customer.Id };
        await repository.AddCustomerAsync(customer, CancellationToken.None);
        await repository.AddProductAsync(product, CancellationToken.None);
        await repository.AddRulesetAsync(ruleset, CancellationToken.None);
        await repository.AddApplicationAsync(application, CancellationToken.None);
        return (
            new LoanApplicationService(
                repository,
                new InMemoryObjectStore(),
                new DeterministicKycProvider(),
                new DeterministicBureauProvider(),
                new WeightedRiskScorer(),
                new DeclarativeRulesEngine(),
                clock,
                audit),
            application,
            audit);
    }
}
