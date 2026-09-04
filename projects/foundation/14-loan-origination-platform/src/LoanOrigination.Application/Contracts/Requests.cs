using LoanOrigination.Domain.Models;
using LoanOrigination.Domain.Rules;

namespace LoanOrigination.Application.Contracts;

public sealed record CreateCustomerRequest(
    CustomerKind Kind,
    string LegalName,
    DateOnly? DateOfBirth,
    string? RegistrationNumber,
    string SyntheticIdentityNumber,
    string Email,
    string Phone,
    string Address,
    decimal MonthlyNetIncome,
    decimal MonthlyExpenses,
    int IncomeStabilityMonths,
    string IncomeSource,
    IReadOnlyList<ExistingObligation>? ExistingObligations,
    int Dependants);

public sealed record CreateProductRequest(
    string ProductCode,
    string Name,
    decimal MinimumPrincipal,
    decimal MaximumPrincipal,
    int MinimumTermMonths,
    int MaximumTermMonths,
    decimal AnnualInterestRate,
    InterestRateMethod InterestMethod,
    string Currency,
    string RulesetId,
    int RulesetVersion,
    IReadOnlyList<ProductFee>? Fees,
    IReadOnlyList<ProductDocumentRequirement>? RequiredDocuments,
    bool CollateralRequired);

public sealed record CreateApplicationRequest(
    Guid CustomerId,
    string ProductCode,
    int ProductVersion,
    decimal RequestedPrincipal,
    int RequestedTermMonths,
    string Currency,
    decimal? CollateralValue = null);

public sealed record UploadDocumentRequest(
    string DocumentType,
    string FileName,
    string ContentType,
    string Base64Content,
    DateOnly? ExpiresOn = null);

public sealed record VerifyDocumentRequest(DocumentVerificationStatus Status, string Reason);

public sealed record KycOverrideRequest(KycStatus Outcome, string Reason);

public sealed record UnderwritingDecisionRequest(
    string Decision,
    decimal ApprovedPrincipal,
    decimal AnnualRate,
    int TermMonths,
    string Reason,
    UnderwriterRole DecisionMakerRole,
    string? SecondApprover,
    UnderwriterRole? SecondApproverRole);

public sealed record CreateOfferRequest(
    Guid ApplicationId,
    decimal Principal,
    decimal AnnualRate,
    int TermMonths,
    int ValidityDays = 7);

public sealed record CounterOfferRequest(decimal Principal, decimal AnnualRate, int TermMonths);

public sealed record DisbursementRequest(Guid OfferId, string ProviderReference, string Rail);

public sealed record RetryDisbursementRequest(string NewProviderReference);

public sealed record DisbursementCallbackRequest(string ProviderReference, DisbursementStatus Status, decimal Amount, string? Reason);

public sealed record WhatIfRequest(RuleSetDefinition CandidateRuleset);

public sealed record WhatIfApplicationDelta(
    Guid ApplicationId,
    RuleDecision HistoricalDecision,
    RuleDecision CandidateDecision,
    bool Flipped,
    string Summary);

public sealed record WhatIfReport(
    string CandidateRulesetId,
    int CandidateRulesetVersion,
    int EvaluatedApplications,
    int FlippedApplications,
    IReadOnlyList<WhatIfApplicationDelta> Deltas);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);
