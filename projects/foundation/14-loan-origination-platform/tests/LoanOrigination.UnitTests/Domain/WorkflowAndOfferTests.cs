using LoanOrigination.Domain.Models;
using LoanOrigination.Domain.Workflow;

namespace LoanOrigination.UnitTests.Domain;

public sealed class WorkflowAndOfferTests
{
    [Theory]
    [InlineData(ApplicationStage.Draft, ApplicationStage.Submitted, true)]
    [InlineData(ApplicationStage.Draft, ApplicationStage.Offered, false)]
    [InlineData(ApplicationStage.DocumentsPending, ApplicationStage.KycInProgress, true)]
    [InlineData(ApplicationStage.Screening, ApplicationStage.Declined, true)]
    [InlineData(ApplicationStage.Accepted, ApplicationStage.Disbursed, true)]
    [InlineData(ApplicationStage.Disbursed, ApplicationStage.Offered, false)]
    public void CanTransition_WorkflowMatrix_EnforcesAllowedEdges(ApplicationStage from, ApplicationStage to, bool expected)
    {
        Assert.Equal(expected, ApplicationWorkflow.CanTransition(from, to));
    }

    [Fact]
    public void Transition_IllegalTransition_Throws()
    {
        var application = TestData.Application(ApplicationStage.Draft);

        Assert.Throws<DomainException>(() =>
            ApplicationWorkflow.Transition(application, ApplicationStage.Offered, "actor", "invalid", "corr", TestData.Now));
    }

    [Fact]
    public void Transition_RecordsFullEventAndStageSla()
    {
        var application = TestData.Application(ApplicationStage.Submitted);

        var next = ApplicationWorkflow.Transition(application, ApplicationStage.DocumentsPending, "analyst", "Need statements", "corr-1", TestData.Now);

        Assert.Equal(ApplicationStage.DocumentsPending, next.Stage);
        Assert.Equal(TestData.Now.AddHours(24), next.SlaDueAt);
        var eventEntry = Assert.Single(next.Events);
        Assert.Equal("analyst", eventEntry.Actor);
        Assert.Equal("corr-1", eventEntry.CorrelationId);
    }

    [Fact]
    public void DocumentChecklist_UnverifiedMandatoryDocument_BlocksProgression()
    {
        var product = TestData.Product();
        var document = new LoanDocument(
            Guid.NewGuid(), "NationalId", "id.pdf", "application/pdf", 100, "key",
            DocumentVerificationStatus.Pending, null, null, null, TestData.Now);

        var complete = DocumentChecklist.MandatoryDocumentsVerified(product, [document], DateOnly.FromDateTime(TestData.Now.UtcDateTime));

        Assert.False(complete);
        Assert.Equal("Pending", Assert.Single(DocumentChecklist.Build(product, [document], DateOnly.FromDateTime(TestData.Now.UtcDateTime))).Status);
    }

    [Fact]
    public void DocumentChecklist_ExpiredVerifiedDocument_BlocksProgression()
    {
        var product = TestData.Product();
        var document = new LoanDocument(
            Guid.NewGuid(), "NationalId", "id.pdf", "application/pdf", 100, "key",
            DocumentVerificationStatus.Verified, "reviewer", "ok", new DateOnly(2026, 8, 31), TestData.Now);

        Assert.False(DocumentChecklist.MandatoryDocumentsVerified(product, [document], new DateOnly(2026, 9, 1)));
    }

    [Fact]
    public void ValidateUpload_UnsupportedContentType_RejectsDocument()
    {
        var requirement = new ProductDocumentRequirement("NationalId", true, 100);

        Assert.Throws<DomainException>(() => DocumentChecklist.ValidateUpload(requirement, "application/x-msdownload", 10));
    }

    [Fact]
    public void ValidateApproval_AboveThresholdWithoutDistinctSecondApprover_Rejects()
    {
        Assert.Throws<DomainException>(() =>
            UnderwritingAuthority.ValidateApproval(600_000m, "alice", UnderwriterRole.Senior, "alice", UnderwriterRole.Senior));
    }

    [Fact]
    public void ValidateApproval_AboveAuthorityLimit_Rejects()
    {
        Assert.Throws<DomainException>(() =>
            UnderwritingAuthority.ValidateApproval(300_000m, "alice", UnderwriterRole.Junior, null, null));
    }

    [Fact]
    public void ValidateApproval_ThresholdWithTwoAuthorizedPeople_Allows()
    {
        UnderwritingAuthority.ValidateApproval(600_000m, "alice", UnderwriterRole.Senior, "bob", UnderwriterRole.Senior);
    }

    [Fact]
    public void Claim_ActiveLockOwnedByOtherUnderwriter_Rejects()
    {
        var item = new UnderwritingQueueItem(Guid.NewGuid(), 100_000m, 10, "B", TestData.Now, TestData.Now.AddHours(2), "alice", TestData.Now.AddMinutes(10));

        Assert.Throws<DomainException>(() => UnderwritingAuthority.Claim(item, "bob", TestData.Now, TimeSpan.FromMinutes(20)));
    }

    [Fact]
    public void Offer_Acceptance_MakesTermsImmutable()
    {
        var offer = OfferLifecycle.Create(Guid.NewGuid(), 1, 100_000m, 12m, 12, InterestRateMethod.ReducingBalance, "KES", [], TestData.Now, TimeSpan.FromDays(7));

        var accepted = OfferLifecycle.Accept(offer, "applicant", TestData.Now);

        Assert.Equal(OfferStatus.Accepted, accepted.Status);
        Assert.Throws<DomainException>(() => OfferLifecycle.Counter(accepted, 90_000m, 11m, 12, TestData.Now));
    }

    [Fact]
    public void Offer_AfterExpiry_CannotBeAccepted()
    {
        var offer = OfferLifecycle.Create(Guid.NewGuid(), 1, 100_000m, 12m, 12, InterestRateMethod.ReducingBalance, "KES", [], TestData.Now, TimeSpan.FromDays(1));

        Assert.Throws<DomainException>(() => OfferLifecycle.Accept(offer, "applicant", TestData.Now.AddDays(2)));
    }
}
