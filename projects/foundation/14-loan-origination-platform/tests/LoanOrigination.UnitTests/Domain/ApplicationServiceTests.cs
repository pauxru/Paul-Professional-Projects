using LoanOrigination.Application.Contracts;
using LoanOrigination.Application.Services;
using LoanOrigination.Domain.Models;
using LoanOrigination.Domain.Rules;
using LoanOrigination.Infrastructure.Providers;

namespace LoanOrigination.UnitTests.Domain;

public sealed class ApplicationServiceTests
{
    [Fact]
    public async Task CreateCustomer_FuzzyNameAndSameDob_RejectsPotentialDuplicate()
    {
        var repository = new InMemoryLoanRepository();
        var audit = new CollectingAuditWriter();
        var service = new CustomerService(repository, new FakeClock(TestData.Now), audit);
        var request = new CreateCustomerRequest(
            CustomerKind.Individual, "Amina Njoroge", new DateOnly(1992, 2, 2), null, "synthetic-pass",
            "amina@example.test", "+254700000001", "Synthetic address", 100_000m, 30_000m, 12, "Salary", [], 1);
        await service.CreateAsync(request, "agent", "corr", "127.0.0.1", "test", CancellationToken.None);

        var duplicate = request with { LegalName = "Amina Njorgee", Email = "another@example.test" };

        await Assert.ThrowsAsync<DuplicateCustomerException>(() =>
            service.CreateAsync(duplicate, "agent", "corr", "127.0.0.1", "test", CancellationToken.None));
    }

    [Fact]
    public async Task ContinueAfterDocuments_MissingMandatoryDocument_BlocksKycProgression()
    {
        var fixture = await BuildApplicationFixtureAsync(ApplicationStage.DocumentsPending);

        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.ContinueAfterDocumentsAsync(
            fixture.Application.Id, "applicant", "corr", "127.0.0.1", "test", CancellationToken.None));
    }

    [Fact]
    public async Task EvaluateDecision_PersistsTraceScoreAndDecisionRecord()
    {
        var fixture = await BuildApplicationFixtureAsync(ApplicationStage.Screening);

        var application = await fixture.Service.EvaluateDecisionAsync(
            fixture.Application.Id, "underwriter", "corr", "127.0.0.1", "test", CancellationToken.None);

        Assert.Equal(ApplicationStage.Underwriting, application.Stage);
        Assert.NotNull(application.DecisionTrace);
        Assert.NotNull(application.RiskAssessment);
        Assert.NotNull(await fixture.Repository.GetDecisionRecordAsync(application.Id, CancellationToken.None));
        Assert.NotEmpty(application.DecisionTrace!.Rules);
    }

    [Fact]
    public async Task CreateOffer_MismatchedApprovedTerms_RejectsMutationOfApprovedDecision()
    {
        var repository = new InMemoryLoanRepository();
        var audit = new CollectingAuditWriter();
        var application = TestData.Application(ApplicationStage.Offered) with
        {
            UnderwritingDecision = new UnderwritingDecision(
                "APPROVE", 100_000m, 18m, 12, "Approved", "senior", UnderwriterRole.Senior, null, TestData.Now)
        };
        await repository.AddApplicationAsync(application, CancellationToken.None);
        await repository.AddProductAsync(TestData.Product([]), CancellationToken.None);
        var service = new OfferService(repository, new FakeClock(TestData.Now), audit);

        await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(
            new CreateOfferRequest(application.Id, 90_000m, 18m, 12),
            "underwriter",
            "corr",
            "127.0.0.1",
            "test",
            CancellationToken.None));
    }

    private static async Task<(LoanApplicationService Service, LoanApplication Application, InMemoryLoanRepository Repository)> BuildApplicationFixtureAsync(ApplicationStage stage)
    {
        var repository = new InMemoryLoanRepository();
        var clock = new FakeClock(TestData.Now);
        var audit = new CollectingAuditWriter();
        var customer = TestData.Customer();
        var product = TestData.Product();
        var ruleset = TestData.Ruleset();
        var application = TestData.Application(stage, stage == ApplicationStage.Screening ? KycStatus.Passed : KycStatus.NotStarted) with { CustomerId = customer.Id };
        await repository.AddCustomerAsync(customer, CancellationToken.None);
        await repository.AddProductAsync(product, CancellationToken.None);
        await repository.AddRulesetAsync(ruleset, CancellationToken.None);
        await repository.AddApplicationAsync(application, CancellationToken.None);
        var service = new LoanApplicationService(
            repository,
            new InMemoryObjectStore(),
            new DeterministicKycProvider(),
            new DeterministicBureauProvider(),
            new WeightedRiskScorer(),
            new DeclarativeRulesEngine(),
            clock,
            audit);
        return (service, application, repository);
    }
}
