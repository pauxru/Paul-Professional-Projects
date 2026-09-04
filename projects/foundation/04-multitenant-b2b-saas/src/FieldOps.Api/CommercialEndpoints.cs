using System.Security.Claims;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using FieldOps.Application;
using FieldOps.Domain;
using FieldOps.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Api;

public sealed record UpsertFlagRequest(bool Enabled, int RolloutPercentage, bool KillSwitch);
public sealed record FlagOverrideRequest(bool Enabled);
public sealed record SubscribeRequest(SubscriptionPlan Plan, string Currency);
public sealed record ProrationRequest(SubscriptionPlan CurrentPlan, SubscriptionPlan TargetPlan, string Currency);
public sealed record ChangePlanRequest(string ProviderSubscriptionId, SubscriptionPlan TargetPlan, string Currency);
public sealed record SimulateWebhookRequest(BillingEventType Type, string? EventId);
public sealed record UpdateOrganizationSettingsRequest(JsonElement Settings);

public static class CommercialEndpoints
{
    public static IEndpointRouteBuilder MapCommercialEndpoints(this IEndpointRouteBuilder app)
    {
        MapOrganization(app);
        MapUsage(app);
        MapFlags(app);
        MapBilling(app);
        MapAudit(app);
        return app;
    }

    private static void MapOrganization(IEndpointRouteBuilder app)
    {
        var organization = app.MapGroup("/api/v1/organization").WithTags("Organization").RequireAuthorization();
        organization.MapGet("/", async (
            ITenantContext tenant,
            IOrganizationRepository organizations,
            CancellationToken cancellationToken) =>
            Results.Ok(await organizations.FindByIdAsync(tenant.RequiredTenantId, cancellationToken)
                ?? throw new KeyNotFoundException("Organization was not found.")));

        organization.MapPut("/settings", async (
            UpdateOrganizationSettingsRequest request,
            ITenantContext tenant,
            IOrganizationRepository organizations,
            ClaimsPrincipal principal,
            HttpContext context,
            IAuditWriter audit,
            CancellationToken cancellationToken) =>
        {
            var value = await organizations.FindByIdAsync(tenant.RequiredTenantId, cancellationToken)
                ?? throw new KeyNotFoundException("Organization was not found.");
            var before = value.SettingsJson;
            value.UpdateSettings(request.Settings.GetRawText());
            await organizations.SaveAsync(cancellationToken);
            var actor = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                ?? "unknown";
            await audit.WriteAsync(
                actor,
                "organization.settings-changed",
                $"organization/{value.Id}",
                before,
                value.SettingsJson,
                context.TraceIdentifier,
                context.Connection.RemoteIpAddress?.ToString(),
                context.Request.Headers.UserAgent.FirstOrDefault(),
                cancellationToken);
            return Results.Ok(value);
        }).RequireAuthorization(Permissions.BillingManage);
    }

    private static void MapUsage(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/usage", async (
            ITenantContext tenant,
            IOrganizationRepository organizations,
            UsageQuotaService quota,
            IEntitlementService entitlements,
            TenantRequestMeter requestMeter,
            CancellationToken cancellationToken) =>
        {
            var organization = await organizations.FindByIdAsync(tenant.RequiredTenantId, cancellationToken)
                ?? throw new KeyNotFoundException("Organization was not found.");
            var plan = entitlements.GetPlan(organization.Plan);
            var jobs = await quota.GetMonthlyAsync(tenant.RequiredTenantId, "jobs-created", cancellationToken);
            return Results.Ok(new
            {
                tenantId = tenant.RequiredTenantId,
                plan = organization.Plan.ToString(),
                periodStart = quota.CurrentMonthlyPeriod,
                usage = new
                {
                    jobsCreated = jobs,
                    jobsSoftLimit = (long)Math.Floor(plan.MaxJobsPerMonth * 0.8m),
                    jobsHardLimit = plan.MaxJobsPerMonth,
                    plan.MaxAssets,
                    plan.MaxUsers,
                    plan.ApiRequestsPerMinute,
                    apiRequestsCurrentMinute = requestMeter.Current(tenant.RequiredTenantId),
                    plan.RetentionDays
                }
            });
        }).RequireAuthorization().WithTags("Usage");
    }

    private static void MapFlags(IEndpointRouteBuilder app)
    {
        var flags = app.MapGroup("/api/v1/feature-flags").WithTags("Feature flags").RequireAuthorization();
        flags.MapGet("/", async (IFeatureFlagStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.ListAsync(cancellationToken)));

        flags.MapGet("/{key}/evaluate", async (
            string key,
            Guid? userId,
            ClaimsPrincipal principal,
            FeatureFlagService service,
            CancellationToken cancellationToken) =>
        {
            var subject = userId ?? Guid.Parse(
                principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
            return Results.Ok(await service.EvaluateAsync(key, subject, cancellationToken));
        });

        flags.MapPut("/{key}", async (
            string key,
            UpsertFlagRequest request,
            ClaimsPrincipal principal,
            HttpContext context,
            FeatureFlagService service,
            CancellationToken cancellationToken) =>
        {
            var actor = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                ?? "unknown";
            return Results.Ok(await service.UpsertAsync(
                key,
                request.Enabled,
                request.RolloutPercentage,
                request.KillSwitch,
                actor,
                context.TraceIdentifier,
                cancellationToken));
        }).RequireAuthorization(Permissions.BillingManage);

        flags.MapPut("/{key}/users/{userId:guid}", async (
            string key,
            Guid userId,
            FlagOverrideRequest request,
            ClaimsPrincipal principal,
            HttpContext context,
            FeatureFlagService service,
            CancellationToken cancellationToken) =>
        {
            var actor = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                ?? "unknown";
            return Results.Ok(await service.SetUserOverrideAsync(
                key, userId, request.Enabled, actor, context.TraceIdentifier, cancellationToken));
        }).RequireAuthorization(Permissions.BillingManage);
    }

    private static void MapBilling(IEndpointRouteBuilder app)
    {
        var billing = app.MapGroup("/api/v1/billing").WithTags("Billing");
        billing.MapPost("/subscriptions", async (
            SubscribeRequest request,
            BillingService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.SubscribeAsync(request.Plan, request.Currency, cancellationToken);
            return Results.Created("/api/v1/billing/subscriptions/current", result);
        }).RequireAuthorization(Permissions.BillingManage);

        billing.MapPost("/proration-preview", async (
            ProrationRequest request,
            BillingService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.PreviewAsync(
                request.CurrentPlan, request.TargetPlan, request.Currency, cancellationToken)))
            .RequireAuthorization(Permissions.BillingManage);

        billing.MapPost("/change-plan", async (
            ChangePlanRequest request,
            BillingService service,
            CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                plan = request.TargetPlan,
                proration = await service.ChangePlanAsync(
                    request.ProviderSubscriptionId,
                    request.TargetPlan,
                    request.Currency,
                    cancellationToken)
            }))
            .RequireAuthorization(Permissions.BillingManage);

        billing.MapPost("/simulator/webhook", (
            SimulateWebhookRequest request,
            ITenantContext tenant,
            IBillingProvider provider,
            IClock clock) =>
        {
            var message = new BillingWebhookMessage(
                request.EventId ?? $"evt_sim_{Guid.NewGuid():N}",
                request.Type,
                tenant.RequiredTenantId);
            return Results.Ok(provider.CreateSignedWebhook(message, clock.UtcNow));
        }).RequireAuthorization(Permissions.BillingManage);

        billing.MapPost("/webhooks/simulator", async (
            HttpRequest request,
            BillingWebhookProcessor processor,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var signature = request.Headers["X-FieldOps-Signature"].FirstOrDefault()
                ?? throw new ForbiddenOperationException("Webhook signature is required.");
            var processed = await processor.ProcessAsync(body, signature, cancellationToken);
            return Results.Ok(new { processed, duplicate = !processed });
        }).AllowAnonymous().DisableAntiforgery();
    }

    private static void MapAudit(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/audit", async (
            string? action,
            string? actor,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? page,
            int? pageSize,
            ITenantContext tenant,
            IOrganizationRepository organizations,
            IEntitlementService entitlements,
            IClock clock,
            FieldOpsDbContext db,
            CancellationToken cancellationToken) =>
        {
            var paging = Paging.Normalize(page, pageSize);
            var query = db.AuditEntries.AsNoTracking();
            var organization = await organizations.FindByIdAsync(tenant.RequiredTenantId, cancellationToken)
                ?? throw new KeyNotFoundException("Organization was not found.");
            var retentionCutoff = clock.UtcNow.AddDays(-entitlements.GetPlan(organization.Plan).RetentionDays);
            query = query.Where(x => x.OccurredAt >= retentionCutoff);
            if (!string.IsNullOrWhiteSpace(action)) query = query.Where(x => x.Action == action);
            if (!string.IsNullOrWhiteSpace(actor)) query = query.Where(x => x.ActorId == actor);
            if (from.HasValue) query = query.Where(x => x.OccurredAt >= from);
            if (to.HasValue) query = query.Where(x => x.OccurredAt <= to);
            var total = await query.CountAsync(cancellationToken);
            var items = await query.OrderByDescending(x => x.OccurredAt)
                .Skip((paging.Page - 1) * paging.PageSize)
                .Take(paging.PageSize)
                .ToListAsync(cancellationToken);
            return Results.Ok(new PageResponse<AuditEntry>(
                items, paging.Page, paging.PageSize, total, Paging.TotalPages(total, paging.PageSize)));
        }).RequireAuthorization(Permissions.AuditRead).WithTags("Audit");
    }
}
