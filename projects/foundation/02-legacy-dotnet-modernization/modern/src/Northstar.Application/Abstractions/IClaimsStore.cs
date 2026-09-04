using Northstar.Domain.Claims;
using Northstar.Domain.Policies;

namespace Northstar.Application.Abstractions;

public interface IClaimsStore
{
    Task<Policy?> FindPolicyByNumberAsync(string policyNumber, CancellationToken cancellationToken);
    Task<Policy?> GetPolicyAsync(Guid policyId, CancellationToken cancellationToken);
    Task<Claim?> GetClaimAsync(Guid claimId, CancellationToken cancellationToken);
    Task<bool> ClaimReferenceExistsAsync(string reference, CancellationToken cancellationToken);
    Task<ClaimPage> SearchClaimsAsync(ClaimSearch search, CancellationToken cancellationToken);
    void AddPolicyholder(Policyholder policyholder);
    void AddPolicy(Policy policy);
    void AddClaim(Claim claim);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed record ClaimSearch(string? Policyholder, ClaimStatus? Status, int Page, int PageSize);

public sealed record ClaimPage(IReadOnlyList<Claim> Items, int TotalCount);
