using Microsoft.EntityFrameworkCore;
using Northstar.Application.Abstractions;
using Northstar.Application.Claims;
using Northstar.Domain.Claims;
using Northstar.Domain.Policies;

namespace Northstar.Infrastructure.Persistence;

public sealed class EfClaimsStore(NorthstarDbContext dbContext) : IClaimsStore
{
    public Task<Policy?> FindPolicyByNumberAsync(string policyNumber, CancellationToken cancellationToken) =>
        dbContext.Policies
            .Include(policy => policy.Policyholder)
            .SingleOrDefaultAsync(policy => policy.PolicyNumber == policyNumber.Trim().ToUpperInvariant(), cancellationToken);

    public Task<Policy?> GetPolicyAsync(Guid policyId, CancellationToken cancellationToken) =>
        dbContext.Policies
            .Include(policy => policy.Policyholder)
            .SingleOrDefaultAsync(policy => policy.Id == policyId, cancellationToken);

    public Task<Claim?> GetClaimAsync(Guid claimId, CancellationToken cancellationToken) =>
        dbContext.Claims
            .Include(claim => claim.Documents)
            .SingleOrDefaultAsync(claim => claim.Id == claimId, cancellationToken);

    public Task<bool> ClaimReferenceExistsAsync(string reference, CancellationToken cancellationToken) =>
        dbContext.Claims.AnyAsync(claim => claim.Reference == reference, cancellationToken);

    public async Task<ClaimPage> SearchClaimsAsync(ClaimSearch search, CancellationToken cancellationToken)
    {
        IQueryable<Claim> query = dbContext.Claims;
        if (search.Status.HasValue)
        {
            query = query.Where(claim => claim.Status == search.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(search.Policyholder))
        {
            var term = search.Policyholder.Trim();
            query = query.Where(claim => dbContext.Policies.Any(policy =>
                policy.Id == claim.PolicyId &&
                policy.Policyholder!.Name.Contains(term)));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var claims = await query
            .OrderByDescending(claim => claim.CreatedAt)
            .Skip((search.Page - 1) * search.PageSize)
            .Take(search.PageSize)
            .Include(claim => claim.Documents)
            .ToListAsync(cancellationToken);
        return new ClaimPage(claims, totalCount);
    }

    public void AddPolicyholder(Policyholder policyholder) => dbContext.Policyholders.Add(policyholder);

    public void AddPolicy(Policy policy) => dbContext.Policies.Add(policy);

    public void AddClaim(Claim claim) => dbContext.Claims.Add(claim);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException("The claim was changed by another user. Refresh and retry.");
        }
    }
}
