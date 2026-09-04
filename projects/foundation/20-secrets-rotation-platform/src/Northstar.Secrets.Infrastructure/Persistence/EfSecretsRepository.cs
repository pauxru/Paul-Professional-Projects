using Microsoft.EntityFrameworkCore;
using Northstar.Secrets.Application;
using Northstar.Secrets.Domain;

namespace Northstar.Secrets.Infrastructure.Persistence;

public sealed class EfSecretsRepository(SecretsDbContext dbContext) : ISecretsRepository
{
    public Task<bool> AnySecretsAsync(CancellationToken cancellationToken) =>
        dbContext.Secrets.AnyAsync(cancellationToken);

    public async Task AddSecretAsync(SecretRecord secret, CancellationToken cancellationToken) =>
        await dbContext.Secrets.AddAsync(secret, cancellationToken);

    public async Task AddSecretVersionAsync(
        SecretVersion version,
        CancellationToken cancellationToken) =>
        await dbContext.SecretVersions.AddAsync(version, cancellationToken);

    public Task<SecretRecord?> GetSecretAsync(
        string normalizedName,
        CancellationToken cancellationToken) =>
        SecretQuery().SingleOrDefaultAsync(x => x.Name == normalizedName, cancellationToken);

    public Task<SecretRecord?> GetSecretByIdAsync(
        Guid secretId,
        CancellationToken cancellationToken) =>
        SecretQuery().SingleOrDefaultAsync(x => x.Id == secretId, cancellationToken);

    public async Task<IReadOnlyList<SecretRecord>> ListSecretsAsync(
        CancellationToken cancellationToken) =>
        await SecretQuery().OrderBy(x => x.Name).ToListAsync(cancellationToken);

    public async Task AddConsumerAsync(Consumer consumer, CancellationToken cancellationToken) =>
        await dbContext.Consumers.AddAsync(consumer, cancellationToken);

    public Task<Consumer?> GetConsumerAsync(Guid consumerId, CancellationToken cancellationToken) =>
        dbContext.Consumers
            .Include(x => x.SecretLinks)
            .SingleOrDefaultAsync(x => x.Id == consumerId, cancellationToken);

    public async Task<IReadOnlyList<Consumer>> ListConsumersAsync(
        CancellationToken cancellationToken) =>
        await dbContext.Consumers
            .Include(x => x.SecretLinks)
            .OrderBy(x => x.Application)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

    public async Task AddRotationAsync(
        RotationOperation rotation,
        CancellationToken cancellationToken) =>
        await dbContext.Rotations.AddAsync(rotation, cancellationToken);

    public async Task AddConsumerAcknowledgementAsync(
        ConsumerAcknowledgement acknowledgement,
        CancellationToken cancellationToken) =>
        await dbContext.ConsumerAcknowledgements.AddAsync(acknowledgement, cancellationToken);

    public Task<RotationOperation?> GetRotationAsync(
        Guid rotationId,
        CancellationToken cancellationToken) =>
        dbContext.Rotations
            .Include(x => x.Acknowledgements)
            .SingleOrDefaultAsync(x => x.Id == rotationId, cancellationToken);

    public Task<RotationOperation?> FindRotationByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        dbContext.Rotations
            .Include(x => x.Acknowledgements)
            .SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<IReadOnlyList<RotationOperation>> ListRotationsAsync(
        CancellationToken cancellationToken)
    {
        var rotations = await dbContext.Rotations
            .Include(x => x.Acknowledgements)
            .ToListAsync(cancellationToken);
        return rotations.OrderByDescending(x => x.RequestedAt).ToArray();
    }

    public async Task<IReadOnlyList<RotationOperation>> ListPendingRotationsForConsumerAsync(
        Guid consumerId,
        CancellationToken cancellationToken)
    {
        var rotations = await dbContext.Rotations
            .Include(x => x.Acknowledgements)
            .Where(x => x.Acknowledgements.Any(a =>
                a.ConsumerId == consumerId &&
                a.Status == ConsumerAcknowledgementStatus.Pending))
            .ToListAsync(cancellationToken);
        return rotations.OrderBy(x => x.AcknowledgementDeadline).ToArray();
    }

    public async Task AddAccessPolicyAsync(
        AccessPolicy policy,
        CancellationToken cancellationToken) =>
        await dbContext.AccessPolicies.AddAsync(policy, cancellationToken);

    public async Task<IReadOnlyList<AccessPolicy>> ListAccessPoliciesAsync(
        string? subject,
        CancellationToken cancellationToken)
    {
        var query = dbContext.AccessPolicies.AsQueryable();
        if (!string.IsNullOrWhiteSpace(subject))
        {
            query = query.Where(x => x.Subject == "*" || x.Subject == subject);
        }

        return await query.OrderBy(x => x.Subject).ThenBy(x => x.PathPattern)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAuditAsync(AuditRecord auditRecord, CancellationToken cancellationToken) =>
        await dbContext.AuditRecords.AddAsync(auditRecord, cancellationToken);

    public async Task<IReadOnlyList<AuditRecord>> ListAuditsAsync(
        Guid? secretId,
        DateTimeOffset? since,
        CancellationToken cancellationToken)
    {
        var query = dbContext.AuditRecords.AsQueryable();
        if (secretId is not null)
        {
            query = query.Where(x => x.SecretId == secretId);
        }

        var records = await query.ToListAsync(cancellationToken);
        return records
            .Where(x => since is null || x.OccurredAt >= since)
            .OrderByDescending(x => x.OccurredAt)
            .ToArray();
    }

    public async Task AddApprovalAsync(
        ApprovalRequest approval,
        CancellationToken cancellationToken) =>
        await dbContext.ApprovalRequests.AddAsync(approval, cancellationToken);

    public Task<ApprovalRequest?> GetApprovalAsync(
        Guid approvalId,
        CancellationToken cancellationToken) =>
        dbContext.ApprovalRequests.SingleOrDefaultAsync(x => x.Id == approvalId, cancellationToken);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            var entries = string.Join(
                ", ",
                exception.Entries.Select(x => $"{x.Metadata.ClrType.Name}:{x.State}"));
            throw new InvalidOperationException($"Concurrency conflict entries: {entries}", exception);
        }
    }

    private IQueryable<SecretRecord> SecretQuery() =>
        dbContext.Secrets
            .Include(x => x.Versions)
            .Include(x => x.Tags)
            .Include(x => x.Consumers)
            .ThenInclude(x => x.Consumer)
            .AsSplitQuery();
}
