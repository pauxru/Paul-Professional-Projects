using Northstar.Iga.Application;
using Northstar.Iga.Domain;

namespace Northstar.Iga.Api;

public static class ProvisioningReportEndpoints
{
    public static IEndpointRouteBuilder MapProvisioningAndReportEndpoints(this IEndpointRouteBuilder app)
    {
        var provisioning = app.MapGroup("/api/v1/provisioning").RequireRateLimiting("api").WithTags("Provisioning");
        provisioning.MapPost("/{connectorKey}/users/{userId:guid}", async (
            string connectorKey,
            Guid userId,
            ProvisioningCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ProvisionUserAsync(
                userId,
                connectorKey,
                command.Operation,
                context.Actor(),
                context.CorrelationId(),
                cancellationToken)))
            .RequireAuthorization(ApiPolicies.Admin);
        provisioning.MapPost("/reconcile", async (
            ReconcileCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ReconcileAsync(
                command.ConnectorKey,
                context.Actor(),
                context.CorrelationId(),
                cancellationToken)))
            .RequireAuthorization(ApiPolicies.Admin);
        provisioning.MapPost("/hr/import", async (
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                imported = await service.ImportHrIdentitiesAsync(
                    context.Actor(), context.CorrelationId(), cancellationToken)
            })).RequireAuthorization(ApiPolicies.Admin);
        provisioning.MapGet("/quarantine", async (IIgaService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetQuarantineAsync(cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);

        var reports = app.MapGroup("/api/v1/reports").RequireRateLimiting("api").WithTags("Reports");
        reports.MapGet("/entitlement-holders", async (IIgaService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetEntitlementHoldersReportAsync(cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        reports.MapGet("/users/{userId:guid}/access-profile", async (
            Guid userId,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetUserAccessProfileAsync(userId, cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        reports.MapGet("/privileged-accounts", async (IIgaService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetPrivilegedInventoryReportAsync(cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        reports.MapGet("/dormant-accounts", async (
            int? days,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetDormantAccountsReportAsync(days ?? 90, cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        reports.MapGet("/sod-violations", async (IIgaService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ScanSoDAsync(cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        reports.MapGet("/orphaned-accounts", async (IIgaService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetOrphanAccountsReportAsync(cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        reports.MapGet("/certification-status", async (IIgaService service, CancellationToken cancellationToken) =>
        {
            var campaigns = await service.GetCampaignsAsync(cancellationToken);
            var rows = new List<CampaignProgress>();
            foreach (var campaign in campaigns)
            {
                rows.Add(await service.GetCampaignProgressAsync(campaign.Id, cancellationToken));
            }
            return Results.Ok(rows);
        }).RequireAuthorization(ApiPolicies.Read);

        app.MapGet("/api/v1/audit", async (
            int? page,
            int? pageSize,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                items = await service.GetAuditAsync(page ?? 1, pageSize ?? 50, cancellationToken),
                page = page ?? 1,
                pageSize = Math.Clamp(pageSize ?? 50, 1, 200)
            }))
            .RequireAuthorization(ApiPolicies.Read)
            .RequireRateLimiting("api")
            .WithTags("Audit");

        return app;
    }
}

public sealed record ProvisioningCommand(ProvisioningOperation Operation);
public sealed record ReconcileCommand(string? ConnectorKey);
