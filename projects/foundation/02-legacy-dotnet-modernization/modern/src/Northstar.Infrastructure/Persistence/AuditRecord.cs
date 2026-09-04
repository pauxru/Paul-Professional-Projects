namespace Northstar.Infrastructure.Persistence;

public sealed class AuditRecord
{
    public Guid Id { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string? SourceIp { get; set; }
    public string? UserAgent { get; set; }
    public string BeforeHash { get; set; } = string.Empty;
    public string AfterHash { get; set; } = string.Empty;
}
