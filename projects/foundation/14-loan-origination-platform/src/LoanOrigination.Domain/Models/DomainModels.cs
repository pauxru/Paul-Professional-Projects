namespace LoanOrigination.Domain.Models;

public enum CustomerKind
{
    Individual,
    Sme
}

public enum KycStatus
{
    NotStarted,
    InProgress,
    Passed,
    Referred,
    Failed,
    ManuallyOverridden
}

public enum ApplicationStage
{
    Draft,
    Submitted,
    DocumentsPending,
    KycInProgress,
    Screening,
    Underwriting,
    Offered,
    Accepted,
    Disbursed,
    Declined,
    Withdrawn,
    Expired
}

public enum DocumentVerificationStatus
{
    Pending,
    Verified,
    Rejected
}

public enum InterestRateMethod
{
    ReducingBalance,
    Flat
}

public enum FeeTreatment
{
    Capitalized,
    Deducted
}

public enum RuleOutcomeType
{
    Pass,
    Fail,
    Refer,
    AdjustLimit,
    AdjustRate,
    RequireDocument
}

public enum RuleDecision
{
    Pass,
    Refer,
    Fail
}

public enum DisbursementStatus
{
    Pending,
    Succeeded,
    Failed,
    Reversed
}

public enum OfferStatus
{
    Issued,
    Accepted,
    Expired,
    Superseded
}

public enum UnderwriterRole
{
    Junior,
    Senior,
    CreditCommittee
}

public sealed record Money(decimal Amount, string Currency)
{
    public decimal RoundedAmount => decimal.Round(Amount, 2, MidpointRounding.AwayFromZero);

    public static Money Zero(string currency) => new(0m, currency);

    public Money Add(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Money currencies must match.");
        }

        return new Money(RoundedAmount + other.RoundedAmount, Currency);
    }
}

public sealed record ContactDetails(string Email, string Phone, string Address);

public sealed record IncomeDeclaration(
    decimal MonthlyNetIncome,
    decimal MonthlyExpenses,
    int IncomeStabilityMonths,
    string Source);

public sealed record ExistingObligation(
    string Lender,
    decimal MonthlyDebtService,
    decimal OutstandingBalance,
    int PastArrearsCount);

public sealed record CustomerProfile(
    Guid Id,
    CustomerKind Kind,
    string LegalName,
    DateOnly? DateOfBirth,
    string? RegistrationNumber,
    string SyntheticIdentityNumber,
    ContactDetails Contact,
    IncomeDeclaration Income,
    IReadOnlyList<ExistingObligation> ExistingObligations,
    int Dependants,
    KycStatus KycStatus,
    DateTimeOffset CreatedAt)
{
    public string DeduplicationKey =>
        Kind == CustomerKind.Individual
            ? $"{Normalize(LegalName)}|{DateOfBirth:yyyy-MM-dd}"
            : $"{Normalize(LegalName)}|{RegistrationNumber?.Trim().ToUpperInvariant()}";

    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();
}

public sealed record ApplicantFacts(
    int AgeYears,
    decimal MonthlyNetIncome,
    decimal MonthlyExpenses,
    decimal ExistingMonthlyDebtService,
    int Dependants,
    int AgeOfBusinessMonths,
    int IncomeStabilityMonths,
    int PastArrearsCount,
    decimal CollateralValue,
    decimal RequestedPrincipal,
    int TermMonths,
    string BureauGrade,
    string KycStatus,
    bool IsSme,
    string Currency = "KES")
{
    public decimal DebtServiceRatio =>
        MonthlyNetIncome <= 0m ? decimal.MaxValue : ExistingMonthlyDebtService / MonthlyNetIncome;

    public decimal DebtToIncomeRatio =>
        MonthlyNetIncome <= 0m ? decimal.MaxValue : (ExistingMonthlyDebtService + MonthlyExpenses) / MonthlyNetIncome;

    public decimal DisposableIncome =>
        decimal.Max(0m, MonthlyNetIncome - MonthlyExpenses - ExistingMonthlyDebtService);
}

public sealed record ProductFee(
    string Code,
    decimal Amount,
    bool IsPercentage,
    FeeTreatment Treatment,
    string Description);

public sealed record ProductDocumentRequirement(
    string DocumentType,
    bool Required,
    int MaximumSizeBytes,
    int? MaximumAgeDays = null);

public sealed record LoanProductVersion(
    Guid Id,
    string ProductCode,
    int Version,
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
    IReadOnlyList<ProductFee> Fees,
    IReadOnlyList<ProductDocumentRequirement> RequiredDocuments,
    bool CollateralRequired,
    DateTimeOffset EffectiveFrom,
    bool IsActive = true)
{
    public void ValidateRequestedTerms(decimal principal, int termMonths, string currency)
    {
        if (!string.Equals(Currency, currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException($"Product accepts {Currency}, not {currency}.");
        }

        if (principal < MinimumPrincipal || principal > MaximumPrincipal)
        {
            throw new DomainException($"Principal must be between {MinimumPrincipal} and {MaximumPrincipal} {Currency}.");
        }

        if (termMonths < MinimumTermMonths || termMonths > MaximumTermMonths)
        {
            throw new DomainException($"Term must be between {MinimumTermMonths} and {MaximumTermMonths} months.");
        }
    }
}

public sealed record LoanDocument(
    Guid Id,
    string DocumentType,
    string FileName,
    string ContentType,
    long Length,
    string ObjectKey,
    DocumentVerificationStatus VerificationStatus,
    string? ReviewedBy,
    string? ReviewReason,
    DateOnly? ExpiresOn,
    DateTimeOffset UploadedAt)
{
    public bool IsVerifiedAndCurrent(DateOnly today) =>
        VerificationStatus == DocumentVerificationStatus.Verified &&
        (!ExpiresOn.HasValue || ExpiresOn.Value >= today);
}

public sealed record ApplicationEvent(
    Guid Id,
    ApplicationStage From,
    ApplicationStage To,
    string Actor,
    string Reason,
    DateTimeOffset OccurredAt,
    string CorrelationId);

public sealed record UnderwritingDecision(
    string Decision,
    decimal ApprovedPrincipal,
    decimal AnnualRate,
    int TermMonths,
    string Reason,
    string RecordedBy,
    UnderwriterRole RecordedByRole,
    string? ApprovedBy,
    DateTimeOffset RecordedAt);

public sealed record UnderwritingQueueItem(
    Guid ApplicationId,
    decimal Exposure,
    int Priority,
    string RiskBand,
    DateTimeOffset QueuedAt,
    DateTimeOffset SlaDueAt,
    string? ClaimedBy,
    DateTimeOffset? LockExpiresAt);

public sealed record LoanApplication(
    Guid Id,
    Guid CustomerId,
    string ProductCode,
    int ProductVersion,
    string RulesetId,
    int RulesetVersion,
    ApplicantFacts Facts,
    decimal RequestedPrincipal,
    int RequestedTermMonths,
    string Currency,
    ApplicationStage Stage,
    DateTimeOffset CreatedAt,
    DateTimeOffset StageEnteredAt,
    DateTimeOffset? SlaDueAt,
    IReadOnlyList<ApplicationEvent> Events,
    IReadOnlyList<LoanDocument> Documents,
    KycStatus KycStatus,
    Rules.DecisionTrace? DecisionTrace,
    Risk.RiskAssessment? RiskAssessment,
    UnderwritingDecision? UnderwritingDecision,
    Guid? OfferId,
    int Version)
{
    public bool IsSlaBreached(DateTimeOffset now) => SlaDueAt.HasValue && now > SlaDueAt.Value;

    public LoanApplication WithDocument(LoanDocument document) =>
        this with { Documents = Documents.Append(document).ToArray(), Version = Version + 1 };

    public LoanApplication ReplaceDocument(LoanDocument document) =>
        this with
        {
            Documents = Documents.Select(existing => existing.Id == document.Id ? document : existing).ToArray(),
            Version = Version + 1
        };
}

public sealed record LoanOffer(
    Guid Id,
    Guid ApplicationId,
    int Version,
    decimal Principal,
    decimal AnnualRate,
    int TermMonths,
    InterestRateMethod InterestMethod,
    string Currency,
    IReadOnlyList<ProductFee> Fees,
    IReadOnlyList<Calculations.AmortizationInstallment> Schedule,
    DateTimeOffset ExpiresAt,
    OfferStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AcceptedAt,
    string? AcceptedBy);

public sealed record DisbursementRecord(
    Guid Id,
    Guid ApplicationId,
    Guid OfferId,
    string ProviderReference,
    decimal ApprovedAmount,
    decimal DisbursedAmount,
    string Rail,
    DisbursementStatus Status,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? FailureReason = null)
{
    public bool IsReconciled => Status == DisbursementStatus.Succeeded && ApprovedAmount == DisbursedAmount;
}

public sealed record AuditEntry(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Actor,
    string Action,
    string Resource,
    string CorrelationId,
    string SourceIp,
    string UserAgent,
    string? BeforeHash,
    string AfterHash,
    string PreviousEntryHash);

public sealed record DecisionRecord(
    Guid Id,
    Guid ApplicationId,
    string ProductCode,
    int ProductVersion,
    string RulesetId,
    int RulesetVersion,
    string ScorecardVersion,
    ApplicantFacts Facts,
    Rules.DecisionTrace DecisionTrace,
    Risk.RiskAssessment RiskAssessment,
    DateTimeOffset CreatedAt);

public class DomainException(string message) : InvalidOperationException(message);
