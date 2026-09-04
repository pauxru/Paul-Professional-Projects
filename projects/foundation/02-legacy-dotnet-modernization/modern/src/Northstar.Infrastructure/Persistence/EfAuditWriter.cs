using Northstar.Application.Abstractions;

namespace Northstar.Infrastructure.Persistence;

public sealed class EfAuditWriter(NorthstarDbContext dbContext) : IAuditWriter
{
    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        dbContext.AuditRecords.Add(new AuditRecord
        {
            Id = Guid.NewGuid(),
            Actor = entry.Actor,
            Action = entry.Action,
            Resource = entry.Resource,
            OccurredAt = entry.OccurredAt,
            CorrelationId = entry.CorrelationId,
            SourceIp = entry.SourceIp,
            UserAgent = entry.UserAgent,
            BeforeHash = entry.BeforeHash,
            AfterHash = entry.AfterHash
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
