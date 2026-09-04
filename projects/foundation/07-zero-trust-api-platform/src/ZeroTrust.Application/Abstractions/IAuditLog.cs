using ZeroTrust.Domain.Audit;

namespace ZeroTrust.Application.Abstractions;

public interface IAuditLog
{
    Task<AuditRecord> AppendAsync(AuditKind kind, string actor, string action, string resource,
        string correlationId, string sourceIp, string userAgent, string detail, bool allowed, CancellationToken ct);

    Task<(bool ok, string? failingRow)> VerifyChainAsync(CancellationToken ct);
}
