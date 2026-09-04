using Northstar.Application.Abstractions;
using Northstar.Domain.Claims;
using Northstar.Domain.Common;
using Northstar.Domain.Policies;
using Northstar.Application.Observability;

namespace Northstar.Application.Claims;

public sealed class ClaimApplicationService(
    IClaimsStore store,
    IDocumentStore documentStore,
    IClock clock,
    SettlementCalculator settlementCalculator)
{
    public async Task<Guid> CreatePolicyholderAsync(CreatePolicyholderCommand command, CancellationToken cancellationToken)
    {
        var policyholder = new Policyholder(Guid.NewGuid(), command.Name, command.Email);
        store.AddPolicyholder(policyholder);
        await store.SaveChangesAsync(cancellationToken);
        return policyholder.Id;
    }

    public async Task<Guid> CreatePolicyAsync(CreatePolicyCommand command, CancellationToken cancellationToken)
    {
        var policy = new Policy(
            Guid.NewGuid(),
            command.PolicyholderId,
            command.PolicyNumber,
            command.DeductibleAmount,
            command.LimitAmount,
            command.Currency);
        store.AddPolicy(policy);
        await store.SaveChangesAsync(cancellationToken);
        return policy.Id;
    }

    public async Task<ClaimResponse> IntakeAsync(IntakeClaimCommand command, CancellationToken cancellationToken)
    {
        using var activity = NorthstarTelemetry.ActivitySource.StartActivity("claim.intake");
        var policy = await store.FindPolicyByNumberAsync(command.PolicyNumber, cancellationToken)
            ?? throw new ResourceNotFoundException($"Policy '{command.PolicyNumber}' was not found.");

        if (!string.Equals(policy.Currency, command.Currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainRuleException("Claim currency must match the policy currency.");
        }

        if (await store.ClaimReferenceExistsAsync(command.Reference.Trim().ToUpperInvariant(), cancellationToken))
        {
            throw new ConcurrencyConflictException($"Claim reference '{command.Reference}' already exists.");
        }

        var claim = Claim.Create(
            Guid.NewGuid(),
            policy.Id,
            command.Reference,
            command.ClaimedAmount,
            command.Currency,
            clock.UtcNow);
        var reserve = settlementCalculator.CalculateAmount(command.ClaimedAmount, policy.DeductibleAmount, policy.LimitAmount);
        claim.SetReserve(reserve);
        store.AddClaim(claim);
        await SaveAsync(cancellationToken);
        NorthstarTelemetry.ClaimsIntakeCounter.Add(1, new KeyValuePair<string, object?>("currency", claim.Currency));
        activity?.SetTag("claim.status", claim.Status.ToString());
        return ToResponse(claim, policy);
    }

    public async Task<ClaimResponse> StartAssessmentAsync(Guid claimId, AssessClaimCommand command, CancellationToken cancellationToken)
    {
        var claim = await GetClaimOrThrowAsync(claimId, cancellationToken);
        EnsureExpectedVersion(claim, command.ExpectedVersion);

        if (claim.Status == ClaimStatus.Submitted)
        {
            claim.StartReview();
        }

        claim.AssignAdjuster(command.Adjuster);
        claim.SetReserve(command.ReserveAmount);
        await SaveAsync(cancellationToken);
        var policy = await GetPolicyForClaimAsync(claim, cancellationToken);
        return ToResponse(claim, policy);
    }

    public async Task<ClaimResponse> TransitionAsync(Guid claimId, TransitionClaimCommand command, CancellationToken cancellationToken)
    {
        var claim = await GetClaimOrThrowAsync(claimId, cancellationToken);
        EnsureExpectedVersion(claim, command.ExpectedVersion);
        claim.TransitionTo(command.TargetStatus);
        await SaveAsync(cancellationToken);
        var policy = await GetPolicyForClaimAsync(claim, cancellationToken);
        return ToResponse(claim, policy);
    }

    public async Task<ClaimResponse> SettleAsync(Guid claimId, int expectedVersion, CancellationToken cancellationToken)
    {
        var claim = await GetClaimOrThrowAsync(claimId, cancellationToken);
        EnsureExpectedVersion(claim, expectedVersion);
        var policy = await GetPolicyForClaimAsync(claim, cancellationToken);
        claim.TransitionTo(ClaimStatus.Settled);
        claim.RecordSettlement(settlementCalculator.CalculateAmount(claim.ClaimedAmount, policy.DeductibleAmount, policy.LimitAmount));
        await SaveAsync(cancellationToken);
        return ToResponse(claim, policy);
    }

    public async Task<ClaimResponse> AttachDocumentAsync(Guid claimId, AttachDocumentCommand command, CancellationToken cancellationToken)
    {
        var claim = await GetClaimOrThrowAsync(claimId, cancellationToken);
        EnsureExpectedVersion(claim, command.ExpectedVersion);
        var stored = await documentStore.SaveAsync(
            new DocumentWrite(command.OriginalName, command.ContentType, command.Content),
            cancellationToken);
        claim.AddDocument(new ClaimDocument(Guid.NewGuid(), claim.Id, command.OriginalName, command.ContentType, stored.StorageKey, clock.UtcNow));
        await SaveAsync(cancellationToken);
        var policy = await GetPolicyForClaimAsync(claim, cancellationToken);
        return ToResponse(claim, policy);
    }

    public async Task<ClaimResponse> GetAsync(Guid claimId, CancellationToken cancellationToken)
    {
        var claim = await GetClaimOrThrowAsync(claimId, cancellationToken);
        var policy = await GetPolicyForClaimAsync(claim, cancellationToken);
        return ToResponse(claim, policy);
    }

    public async Task<ClaimListResponse> ListAsync(
        string? policyholder,
        ClaimStatus? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await store.SearchClaimsAsync(new ClaimSearch(policyholder, status, page, pageSize), cancellationToken);
        var items = new List<ClaimResponse>();
        foreach (var claim in result.Items)
        {
            var policy = await GetPolicyForClaimAsync(claim, cancellationToken);
            items.Add(ToResponse(claim, policy));
        }

        return new ClaimListResponse(items, page, pageSize, result.TotalCount, (int)Math.Ceiling(result.TotalCount / (double)pageSize));
    }

    private async Task<Claim> GetClaimOrThrowAsync(Guid claimId, CancellationToken cancellationToken) =>
        await store.GetClaimAsync(claimId, cancellationToken)
            ?? throw new ResourceNotFoundException($"Claim '{claimId}' was not found.");

    private async Task<Policy> GetPolicyForClaimAsync(Claim claim, CancellationToken cancellationToken)
    {
        var policy = await store.GetPolicyAsync(claim.PolicyId, cancellationToken);
        return policy ?? throw new ResourceNotFoundException($"Policy '{claim.PolicyId}' was not found.");
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await store.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            throw;
        }
        catch (Exception exception) when (exception.GetType().Name == "DbUpdateConcurrencyException")
        {
            throw new ConcurrencyConflictException("The claim was changed by another user. Refresh and retry.");
        }
    }

    private static void EnsureExpectedVersion(Claim claim, int expectedVersion)
    {
        if (claim.Version != expectedVersion)
        {
            throw new ConcurrencyConflictException($"Expected version {expectedVersion}, but claim is version {claim.Version}.");
        }
    }

    private static ClaimResponse ToResponse(Claim claim, Policy policy) => new(
        claim.Id,
        claim.Reference,
        policy.PolicyNumber,
        policy.Policyholder?.Name,
        claim.Status,
        claim.ClaimedAmount,
        claim.ReserveAmount,
        claim.SettlementAmount,
        claim.Currency,
        claim.AssignedAdjuster,
        claim.Version,
        claim.CreatedAt);
}
