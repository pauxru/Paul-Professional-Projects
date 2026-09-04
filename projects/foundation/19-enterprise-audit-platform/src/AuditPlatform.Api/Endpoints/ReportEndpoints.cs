using AuditPlatform.Api.Auth;
using AuditPlatform.Application.Reports;

namespace AuditPlatform.Api.Endpoints;

public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/reports/privileged-access", (
            HttpContext ctx,
            PrivilegedAccessReportService svc,
            DateTimeOffset? from,
            DateTimeOffset? to) =>
        {
            var reader = ctx.User.ToReader(ctx);
            var report = svc.Build(reader.TenantId, from ?? DateTimeOffset.UtcNow.AddDays(-30), to ?? DateTimeOffset.UtcNow);
            return Results.Ok(report);
        })
        .RequireAuthorization("audit:read")
        .WithTags("reports");

        return app;
    }
}
