using LoanOrigination.Domain.Models;
using LoanOrigination.Domain.Risk;
using LoanOrigination.Domain.Rules;

namespace LoanOrigination.Application.Ports;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IObjectStore
{
    Task<string> PutAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken);
}

public enum KycProviderOutcome
{
    Pass,
    Refer,
    Fail,
    PepHit,
    SanctionsHit,
    Timeout
}

public sealed record KycProviderResult(
    KycProviderOutcome Outcome,
    string ProviderReference,
    string Reason,
    bool IsTransient);

public interface IKycProvider
{
    Task<KycProviderResult> ScreenAsync(string syntheticIdentityNumber, CancellationToken cancellationToken);
}

public interface IBureauProvider
{
    Task<BureauReport> GetReportAsync(string syntheticIdentityNumber, CancellationToken cancellationToken);
}

public interface IRiskScorer
{
    RiskAssessment Score(ApplicantFacts facts, BureauReport bureau);
}

public sealed record ProviderDisbursementResult(
    string ProviderReference,
    DisbursementStatus Status,
    decimal Amount,
    string? Reason = null);

public interface IDisbursementProvider
{
    Task<ProviderDisbursementResult> SendAsync(
        string providerReference,
        decimal amount,
        string rail,
        CancellationToken cancellationToken);
}

public interface ILoanRepository
{
    Task AddCustomerAsync(CustomerProfile customer, CancellationToken cancellationToken);
    Task<CustomerProfile?> GetCustomerAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomerProfile>> ListCustomersAsync(CancellationToken cancellationToken);
    Task SaveCustomerAsync(CustomerProfile customer, CancellationToken cancellationToken);

    Task AddProductAsync(LoanProductVersion product, CancellationToken cancellationToken);
    Task<LoanProductVersion?> GetProductAsync(string productCode, int version, CancellationToken cancellationToken);
    Task<IReadOnlyList<LoanProductVersion>> ListProductsAsync(CancellationToken cancellationToken);

    Task AddRulesetAsync(RuleSetDefinition ruleset, CancellationToken cancellationToken);
    Task<RuleSetDefinition?> GetRulesetAsync(string id, int version, CancellationToken cancellationToken);
    Task<IReadOnlyList<RuleSetDefinition>> ListRulesetsAsync(CancellationToken cancellationToken);

    Task AddApplicationAsync(LoanApplication application, CancellationToken cancellationToken);
    Task<LoanApplication?> GetApplicationAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<LoanApplication>> ListApplicationsAsync(CancellationToken cancellationToken);
    Task SaveApplicationAsync(LoanApplication application, int expectedVersion, CancellationToken cancellationToken);

    Task UpsertQueueItemAsync(UnderwritingQueueItem item, CancellationToken cancellationToken);
    Task<UnderwritingQueueItem?> GetQueueItemAsync(Guid applicationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<UnderwritingQueueItem>> ListQueueItemsAsync(CancellationToken cancellationToken);

    Task AddOfferAsync(LoanOffer offer, CancellationToken cancellationToken);
    Task<LoanOffer?> GetOfferAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<LoanOffer>> ListOffersForApplicationAsync(Guid applicationId, CancellationToken cancellationToken);
    Task SaveOfferAsync(LoanOffer offer, CancellationToken cancellationToken);

    Task AddDisbursementAsync(DisbursementRecord disbursement, CancellationToken cancellationToken);
    Task<DisbursementRecord?> GetDisbursementByReferenceAsync(string providerReference, CancellationToken cancellationToken);
    Task SaveDisbursementAsync(DisbursementRecord disbursement, CancellationToken cancellationToken);

    Task AddAuditAsync(AuditEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditEntry>> ListAuditsAsync(CancellationToken cancellationToken);
    Task AddDecisionRecordAsync(DecisionRecord record, CancellationToken cancellationToken);
    Task<DecisionRecord?> GetDecisionRecordAsync(Guid applicationId, CancellationToken cancellationToken);
}

public interface IAuditWriter
{
    Task WriteAsync(
        string actor,
        string action,
        string resource,
        object? before,
        object after,
        string correlationId,
        string sourceIp,
        string userAgent,
        CancellationToken cancellationToken);
}
