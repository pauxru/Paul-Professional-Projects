using LoanOrigination.Application.Contracts;
using LoanOrigination.Application.Ports;
using LoanOrigination.Domain.Calculations;
using LoanOrigination.Domain.Models;
using LoanOrigination.Domain.Risk;
using LoanOrigination.Domain.Rules;
using LoanOrigination.Domain.Workflow;

namespace LoanOrigination.Application.Services;

public sealed record KycRunResult(LoanApplication Application, KycProviderResult ProviderResult, int Attempts);

public sealed class LoanApplicationService(
    ILoanRepository repository,
    IObjectStore objectStore,
    IKycProvider kycProvider,
    IBureauProvider bureauProvider,
    IRiskScorer riskScorer,
    DeclarativeRulesEngine rulesEngine,
    IClock clock,
    IAuditWriter auditWriter)
{
    private static readonly AffordabilityPolicy AffordabilityPolicy = new(0.55m, 5m, 2_500m);

    public async Task<LoanApplication> CreateAsync(
        CreateApplicationRequest request,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var customer = await repository.GetCustomerAsync(request.CustomerId, cancellationToken)
            ?? throw new DomainException("Customer was not found.");
        var product = await repository.GetProductAsync(request.ProductCode, request.ProductVersion, cancellationToken)
            ?? throw new DomainException("Product version was not found.");
        product.ValidateRequestedTerms(request.RequestedPrincipal, request.RequestedTermMonths, request.Currency);
        if (product.CollateralRequired && (!request.CollateralValue.HasValue || request.CollateralValue.Value <= 0m))
        {
            throw new DomainException("This product requires declared collateral.");
        }

        var ruleset = await repository.GetRulesetAsync(product.RulesetId, product.RulesetVersion, cancellationToken)
            ?? throw new DomainException("Product's immutable ruleset version was not found.");
        var now = clock.UtcNow;
        var facts = BuildFacts(customer, request, now);
        var application = new LoanApplication(
            Guid.NewGuid(),
            customer.Id,
            product.ProductCode,
            product.Version,
            ruleset.Id,
            ruleset.Version,
            facts,
            request.RequestedPrincipal,
            request.RequestedTermMonths,
            request.Currency.ToUpperInvariant(),
            ApplicationStage.Draft,
            now,
            now,
            WorkflowSla.Default.DueAt(ApplicationStage.Draft, now),
            [],
            [],
            KycStatus.NotStarted,
            null,
            null,
            null,
            null,
            0);
        await repository.AddApplicationAsync(application, cancellationToken);
        await auditWriter.WriteAsync(actor, "application.created", $"applications/{application.Id}", null, application, correlationId, sourceIp, userAgent, cancellationToken);
        return application;
    }

    public async Task<LoanApplication> SubmitAsync(
        Guid applicationId,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var application = await RequireApplication(applicationId, cancellationToken);
        var product = await RequireProduct(application, cancellationToken);
        var now = clock.UtcNow;
        var next = ApplicationWorkflow.Transition(application, ApplicationStage.Submitted, actor, "Applicant submitted the application.", correlationId, now);
        var additionalDocuments = next.DecisionTrace?.RequiredDocuments;
        var documentsComplete = DocumentChecklist.MandatoryDocumentsVerified(product, next.Documents, DateOnly.FromDateTime(now.UtcDateTime), additionalDocuments);
        next = ApplicationWorkflow.Transition(
            next,
            documentsComplete ? ApplicationStage.KycInProgress : ApplicationStage.DocumentsPending,
            actor,
            documentsComplete ? "Required documents are already verified." : "Required documents are pending verification.",
            correlationId,
            now);
        await repository.SaveApplicationAsync(next, application.Version, cancellationToken);
        await auditWriter.WriteAsync(actor, "application.submitted", $"applications/{application.Id}", application, next, correlationId, sourceIp, userAgent, cancellationToken);
        return next;
    }

    public async Task<LoanApplication> UploadDocumentAsync(
        Guid applicationId,
        UploadDocumentRequest request,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var application = await RequireApplication(applicationId, cancellationToken);
        var product = await RequireProduct(application, cancellationToken);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(request.Base64Content);
        }
        catch (FormatException)
        {
            throw new DomainException("Document content must be valid base64.");
        }

        var requirement = product.RequiredDocuments
            .FirstOrDefault(item => string.Equals(item.DocumentType, request.DocumentType, StringComparison.OrdinalIgnoreCase))
            ?? new ProductDocumentRequirement(request.DocumentType, false, 10_000_000);
        DocumentChecklist.ValidateUpload(requirement, request.ContentType, bytes.LongLength);
        var objectKey = $"applications/{application.Id}/{Guid.NewGuid():N}-{SanitizeFileName(request.FileName)}";
        await using var stream = new MemoryStream(bytes, writable: false);
        await objectStore.PutAsync(objectKey, stream, request.ContentType, cancellationToken);
        var document = new LoanDocument(
            Guid.NewGuid(),
            request.DocumentType.Trim(),
            SanitizeFileName(request.FileName),
            request.ContentType,
            bytes.LongLength,
            objectKey,
            DocumentVerificationStatus.Pending,
            null,
            null,
            request.ExpiresOn,
            clock.UtcNow);
        var next = application.WithDocument(document);
        await repository.SaveApplicationAsync(next, application.Version, cancellationToken);
        await auditWriter.WriteAsync(actor, "document.uploaded", $"applications/{application.Id}/documents/{document.Id}", null, document, correlationId, sourceIp, userAgent, cancellationToken);
        return next;
    }

    public async Task<LoanApplication> VerifyDocumentAsync(
        Guid applicationId,
        Guid documentId,
        VerifyDocumentRequest request,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var application = await RequireApplication(applicationId, cancellationToken);
        var document = application.Documents.FirstOrDefault(candidate => candidate.Id == documentId)
            ?? throw new DomainException("Document was not found.");
        if (request.Status is not (DocumentVerificationStatus.Verified or DocumentVerificationStatus.Rejected))
        {
            throw new DomainException("A reviewer can only verify or reject a document.");
        }

        var reviewed = document with { VerificationStatus = request.Status, ReviewedBy = actor, ReviewReason = request.Reason };
        var next = application.ReplaceDocument(reviewed);
        await repository.SaveApplicationAsync(next, application.Version, cancellationToken);
        await auditWriter.WriteAsync(actor, "document.reviewed", $"applications/{application.Id}/documents/{document.Id}", document, reviewed, correlationId, sourceIp, userAgent, cancellationToken);
        return next;
    }

    public async Task<LoanApplication> ContinueAfterDocumentsAsync(
        Guid applicationId,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var application = await RequireApplication(applicationId, cancellationToken);
        var product = await RequireProduct(application, cancellationToken);
        if (application.Stage != ApplicationStage.DocumentsPending)
        {
            throw new DomainException("Documents can only be completed while the application is pending documents.");
        }

        if (!DocumentChecklist.MandatoryDocumentsVerified(product, application.Documents, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), application.DecisionTrace?.RequiredDocuments))
        {
            throw new DomainException("Mandatory documents must be verified and current before KYC.");
        }

        var next = ApplicationWorkflow.Transition(application, ApplicationStage.KycInProgress, actor, "Mandatory documents verified.", correlationId, clock.UtcNow);
        await repository.SaveApplicationAsync(next, application.Version, cancellationToken);
        await auditWriter.WriteAsync(actor, "documents.completed", $"applications/{application.Id}", application, next, correlationId, sourceIp, userAgent, cancellationToken);
        return next;
    }

    public async Task<KycRunResult> RunKycAsync(
        Guid applicationId,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var application = await RequireApplication(applicationId, cancellationToken);
        if (application.Stage != ApplicationStage.KycInProgress)
        {
            throw new DomainException("KYC can only run while the application is in KYC.");
        }

        var customer = await repository.GetCustomerAsync(application.CustomerId, cancellationToken)
            ?? throw new DomainException("Customer was not found.");
        KycProviderResult? result = null;
        var attempts = 0;
        do
        {
            attempts++;
            result = await kycProvider.ScreenAsync(customer.SyntheticIdentityNumber, cancellationToken);
        }
        while (result.Outcome == KycProviderOutcome.Timeout && attempts < 2);

        var now = clock.UtcNow;
        LoanApplication next;
        switch (result.Outcome)
        {
            case KycProviderOutcome.Pass:
                next = application with { KycStatus = KycStatus.Passed, Facts = application.Facts with { KycStatus = KycStatus.Passed.ToString() } };
                next = ApplicationWorkflow.Transition(next, ApplicationStage.Screening, actor, result.Reason, correlationId, now);
                break;
            case KycProviderOutcome.Refer:
            case KycProviderOutcome.PepHit:
                next = application with { KycStatus = KycStatus.Referred, Facts = application.Facts with { KycStatus = KycStatus.Referred.ToString() } };
                next = ApplicationWorkflow.Transition(next, ApplicationStage.Screening, actor, result.Reason, correlationId, now);
                break;
            case KycProviderOutcome.Fail:
            case KycProviderOutcome.SanctionsHit:
                next = application with { KycStatus = KycStatus.Failed, Facts = application.Facts with { KycStatus = KycStatus.Failed.ToString() } };
                next = ApplicationWorkflow.Transition(next, ApplicationStage.Declined, actor, result.Reason, correlationId, now);
                break;
            case KycProviderOutcome.Timeout:
                next = application with { KycStatus = KycStatus.InProgress, Version = application.Version + 1 };
                break;
            default:
                throw new DomainException("Unknown KYC provider outcome.");
        }

        await repository.SaveApplicationAsync(next, application.Version, cancellationToken);
        await repository.SaveCustomerAsync(customer with { KycStatus = next.KycStatus }, cancellationToken);
        await auditWriter.WriteAsync(actor, "kyc.screened", $"applications/{application.Id}", application, new { result, attempts, next.KycStatus }, correlationId, sourceIp, userAgent, cancellationToken);
        return new KycRunResult(next, result, attempts);
    }

    public async Task<LoanApplication> OverrideKycAsync(
        Guid applicationId,
        KycOverrideRequest request,
        bool elevatedPermission,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        if (!elevatedPermission)
        {
            throw new DomainException("Manual KYC override requires elevated permission.");
        }

        var application = await RequireApplication(applicationId, cancellationToken);
        if (application.Stage != ApplicationStage.KycInProgress)
        {
            throw new DomainException("KYC override is only permitted while KYC is in progress.");
        }

        if (request.Outcome is not (KycStatus.Passed or KycStatus.Referred))
        {
            throw new DomainException("Manual override may only set Passed or Referred.");
        }

        var next = application with
        {
            KycStatus = KycStatus.ManuallyOverridden,
            Facts = application.Facts with { KycStatus = KycStatus.ManuallyOverridden.ToString() }
        };
        next = ApplicationWorkflow.Transition(next, ApplicationStage.Screening, actor, $"Manual KYC override: {request.Reason}", correlationId, clock.UtcNow);
        await repository.SaveApplicationAsync(next, application.Version, cancellationToken);
        var customer = await repository.GetCustomerAsync(application.CustomerId, cancellationToken)
            ?? throw new DomainException("Customer was not found.");
        await repository.SaveCustomerAsync(customer with { KycStatus = KycStatus.ManuallyOverridden }, cancellationToken);
        await auditWriter.WriteAsync(actor, "kyc.manually-overridden", $"applications/{application.Id}", application, new { requestedOutcome = request.Outcome, request.Reason, next }, correlationId, sourceIp, userAgent, cancellationToken);
        return next;
    }

    public async Task<LoanApplication> EvaluateDecisionAsync(
        Guid applicationId,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var application = await RequireApplication(applicationId, cancellationToken);
        if (application.Stage != ApplicationStage.Screening)
        {
            throw new DomainException("Rules and affordability can only be evaluated during screening.");
        }

        var customer = await repository.GetCustomerAsync(application.CustomerId, cancellationToken)
            ?? throw new DomainException("Customer was not found.");
        var product = await RequireProduct(application, cancellationToken);
        var ruleset = await repository.GetRulesetAsync(application.RulesetId, application.RulesetVersion, cancellationToken)
            ?? throw new DomainException("Bound ruleset version was not found.");
        var bureau = await bureauProvider.GetReportAsync(customer.SyntheticIdentityNumber, cancellationToken);
        var risk = riskScorer.Score(application.Facts, bureau);
        var trace = rulesEngine.Evaluate(ruleset, application.Facts);
        var affordability = AffordabilityCalculator.Calculate(application.Facts, product.AnnualInterestRate, application.RequestedTermMonths, product.InterestMethod, AffordabilityPolicy);
        var now = clock.UtcNow;
        var next = application with { DecisionTrace = trace, RiskAssessment = risk };
        if (trace.Decision == RuleDecision.Fail)
        {
            next = ApplicationWorkflow.Transition(next, ApplicationStage.Declined, actor, DescribeDecision(trace, affordability, risk), correlationId, now);
        }
        else if (trace.RequiredDocuments.Count > 0 &&
                 !DocumentChecklist.MandatoryDocumentsVerified(product, next.Documents, DateOnly.FromDateTime(now.UtcDateTime), trace.RequiredDocuments))
        {
            next = ApplicationWorkflow.Transition(next, ApplicationStage.DocumentsPending, actor, "Rules evaluation requires additional verified documents before underwriting.", correlationId, now);
        }
        else
        {
            next = ApplicationWorkflow.Transition(next, ApplicationStage.Underwriting, actor, DescribeDecision(trace, affordability, risk), correlationId, now);
            var priority = UnderwritingAuthority.CalculatePriority(next.RequestedPrincipal, risk.Band, next.StageEnteredAt, next.SlaDueAt ?? now, now);
            await repository.UpsertQueueItemAsync(new UnderwritingQueueItem(next.Id, next.RequestedPrincipal, priority, risk.Band, next.StageEnteredAt, next.SlaDueAt ?? now, null, null), cancellationToken);
        }

        await repository.SaveApplicationAsync(next, application.Version, cancellationToken);
        var record = new DecisionRecord(
            Guid.NewGuid(),
            next.Id,
            product.ProductCode,
            product.Version,
            ruleset.Id,
            ruleset.Version,
            risk.ScorecardVersion,
            next.Facts,
            trace,
            risk,
            now);
        await repository.AddDecisionRecordAsync(record, cancellationToken);
        await auditWriter.WriteAsync(actor, "decision.evaluated", $"applications/{application.Id}", application, new { next.Stage, trace, risk, affordability }, correlationId, sourceIp, userAgent, cancellationToken);
        return next;
    }

    public async Task<PagedResult<LoanApplication>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var values = (await repository.ListApplicationsAsync(cancellationToken))
            .OrderByDescending(application => application.CreatedAt)
            .ToArray();
        return Pagination.Page(values, page, pageSize);
    }

    public Task<LoanApplication?> GetAsync(Guid applicationId, CancellationToken cancellationToken) =>
        repository.GetApplicationAsync(applicationId, cancellationToken);

    public async Task<LoanApplication> WithdrawAsync(
        Guid applicationId,
        string reason,
        string actor,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken)
    {
        var application = await RequireApplication(applicationId, cancellationToken);
        var next = ApplicationWorkflow.Transition(
            application,
            ApplicationStage.Withdrawn,
            actor,
            string.IsNullOrWhiteSpace(reason) ? "Applicant withdrew the application." : reason,
            correlationId,
            clock.UtcNow);
        await repository.SaveApplicationAsync(next, application.Version, cancellationToken);
        await auditWriter.WriteAsync(actor, "application.withdrawn", $"applications/{applicationId}", application, next, correlationId, sourceIp, userAgent, cancellationToken);
        return next;
    }

    private async Task<LoanApplication> RequireApplication(Guid id, CancellationToken cancellationToken) =>
        await repository.GetApplicationAsync(id, cancellationToken) ?? throw new DomainException("Application was not found.");

    private async Task<LoanProductVersion> RequireProduct(LoanApplication application, CancellationToken cancellationToken) =>
        await repository.GetProductAsync(application.ProductCode, application.ProductVersion, cancellationToken)
        ?? throw new DomainException("Bound product version was not found.");

    private static ApplicantFacts BuildFacts(CustomerProfile customer, CreateApplicationRequest request, DateTimeOffset now)
    {
        var age = customer.DateOfBirth.HasValue
            ? now.Year - customer.DateOfBirth.Value.Year -
              (DateOnly.FromDateTime(now.UtcDateTime) < customer.DateOfBirth.Value.AddYears(now.Year - customer.DateOfBirth.Value.Year) ? 1 : 0)
            : 35;
        return new ApplicantFacts(
            age,
            customer.Income.MonthlyNetIncome,
            customer.Income.MonthlyExpenses,
            customer.ExistingObligations.Sum(obligation => obligation.MonthlyDebtService),
            customer.Dependants,
            customer.Kind == CustomerKind.Sme ? customer.Income.IncomeStabilityMonths : 0,
            customer.Income.IncomeStabilityMonths,
            customer.ExistingObligations.Sum(obligation => obligation.PastArrearsCount),
            request.CollateralValue ?? 0m,
            request.RequestedPrincipal,
            request.RequestedTermMonths,
            "C",
            customer.KycStatus.ToString(),
            customer.Kind == CustomerKind.Sme,
            request.Currency.ToUpperInvariant());
    }

    private static string DescribeDecision(DecisionTrace trace, AffordabilityResult affordability, RiskAssessment risk) =>
        $"Ruleset {trace.RulesetId} v{trace.RulesetVersion} returned {trace.Decision}; affordability is {(affordability.IsAffordable ? "within" : "outside")} policy; scorecard {risk.ScorecardVersion} scored {risk.Score} ({risk.Band}).";

    private static string SanitizeFileName(string fileName)
    {
        var leaf = Path.GetFileName(fileName);
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(leaf.Select(character => invalid.Contains(character) ? '_' : character));
    }
}
