namespace Northstar.Application.Importing;

public interface ILegacyClaimSource
{
    Task<IReadOnlyList<LegacyClaimRow>> ReadClaimsAsync(CancellationToken cancellationToken);
}

public sealed record LegacyClaimRow(
    long LegacyClaimId,
    string ClaimReference,
    long LegacyPolicyId,
    string PolicyNumber,
    string PolicyholderName,
    string PolicyholderEmail,
    decimal DeductibleAmount,
    decimal PolicyLimitAmount,
    decimal ClaimedAmount,
    decimal ReserveAmount,
    string Currency,
    string Status,
    string? Adjuster,
    DateTimeOffset CreatedAt);

public sealed record LegacyImportRejection(long LegacyClaimId, string ClaimReference, string Reason);

public sealed record LegacyImportReport(
    int SourceRowCount,
    int ImportedRowCount,
    string SourceChecksum,
    string ImportedChecksum,
    IReadOnlyList<LegacyImportRejection> RejectedRows);
