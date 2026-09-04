namespace Northstar.Application.Abstractions;

public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken);
}

public sealed record AuditEntry(
    string Actor,
    string Action,
    string Resource,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    string? SourceIp,
    string? UserAgent,
    string BeforeHash,
    string AfterHash);
