using Idp.Domain.Audit;

namespace Idp.Application.Abstractions;

public interface IAuditRepository
{
    void Add(AuditEntry entry);
    Task<IReadOnlyList<AuditEntry>> RecentAsync(int limit, CancellationToken ct = default);
}
