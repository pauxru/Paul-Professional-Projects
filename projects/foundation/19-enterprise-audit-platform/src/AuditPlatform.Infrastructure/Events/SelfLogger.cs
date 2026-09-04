using AuditPlatform.Application.Events;
using AuditPlatform.Application.Retention;
using Microsoft.Extensions.DependencyInjection;

namespace AuditPlatform.Infrastructure.Events;

/// <summary>Self-logger used by the retention pruner and the query service so the audit log
/// audits itself. The recursion loop is broken by <see cref="Application.Security.ReaderContext.IsMetaAuditor"/>
/// which the QueryService checks before emitting a meta-audit event.</summary>
public sealed class SelfLogger : IAuditSelfLogger
{
    private readonly IServiceScopeFactory _scopeFactory;

    public SelfLogger(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task LogAsync(IngestEventRequest request, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var ingest = scope.ServiceProvider.GetRequiredService<AuditIngestService>();
        await ingest.IngestAsync(request, ct);
    }
}
