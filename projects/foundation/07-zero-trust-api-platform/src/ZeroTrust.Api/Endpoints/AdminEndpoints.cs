using Microsoft.EntityFrameworkCore;
using ZeroTrust.Api.Authorization;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Audit;
using ZeroTrust.Domain.Identity;
using ZeroTrust.Infrastructure.Persistence;
using ZeroTrust.Infrastructure.Security;

namespace ZeroTrust.Api.Endpoints;

public sealed record BreakGlassRequestDto(string Subject, string Requestor, string Approver, string Justification, int MinutesValid);

public sealed record ApiKeyMigrationReportRow(string KeyId, string OwnerPartnerCode, DateTime? LastUsedAtUtc, int UsageCount, DateTime? DeprecatedAfterUtc, bool PastDeprecation);

public sealed record ApiKeyCutoverToggleDto(bool EnforcementActive);

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/admin")
            .RequireRateLimiting("admin-fixed-window");

        g.MapGet("/audit", async (
            int? page, int? pageSize, ZeroTrustDbContext db, CancellationToken ct) =>
        {
            var p = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 25, 1, 200);
            var total = await db.AuditRecords.CountAsync(ct);
            var items = await db.AuditRecords.AsNoTracking()
                .OrderByDescending(a => a.Sequence).Skip((p - 1) * ps).Take(ps)
                .Select(a => new
                {
                    a.Id, a.Sequence, kind = a.Kind.ToString(), a.Actor, a.Action, a.Resource,
                    a.CorrelationId, a.Allowed, a.CreatedAtUtc, a.Hash
                }).ToListAsync(ct);
            return Results.Ok(new { items, page = p, pageSize = ps, totalCount = total });
        })
        .RequireAuthorization("admin.audit")
        .WithName("ListAudit");

        g.MapPost("/audit/verify-chain", async (IAuditLog audit, CancellationToken ct) =>
        {
            var (ok, failing) = await audit.VerifyChainAsync(ct);
            return Results.Ok(new { ok, failingRow = failing });
        })
        .RequireAuthorization("admin.audit")
        .WithName("VerifyAuditChain");

        g.MapPost("/authz/evaluate", async (
            AuthzEvaluationRequest req,
            AuthorizationExplainer explainer,
            HttpContext ctx,
            IAuditLog audit,
            CancellationToken ct) =>
        {
            var decision = await explainer.EvaluateAsync(req, ct);
            await audit.AppendAsync(AuditKind.AdminAction, ctx.User.Subject() ?? "?", "authz_evaluate",
                $"{req.Action}:{req.Resource}", Cid(ctx), Ip(ctx), Ua(ctx),
                $"principal={decision.PrincipalSubject}; allowed={decision.Allowed}; requirement={decision.DecidingRequirement}",
                true, ct);
            return Results.Ok(decision);
        })
        .RequireAuthorization("admin.authz.evaluate")
        .WithName("EvaluateAuthz");

        g.MapPost("/keys/rotate", async (
            IJwksProvider provider, HttpContext ctx, IAuditLog audit, CancellationToken ct) =>
        {
            await provider.RotateAsync(ct);
            await audit.AppendAsync(AuditKind.KeyRotated, ctx.User.Subject() ?? "?", "rotate_signing_keys",
                "signing_keys", Cid(ctx), Ip(ctx), Ua(ctx), "rotation triggered", true, ct);
            var jwks = await provider.GetAsync(ct);
            return Results.Ok(new { rotated = true, keyCount = jwks.Keys.Count });
        })
        .RequireAuthorization("admin.users")
        .WithName("RotateSigningKeys");

        g.MapGet("/api-keys/migration-report", async (
            ZeroTrustDbContext db, IClock clock, CancellationToken ct) =>
        {
            var now = clock.UtcNow;
            var rows = await db.ApiKeys.AsNoTracking()
                .Select(k => new ApiKeyMigrationReportRow(
                    k.KeyId, k.OwnerPartnerCode, k.LastUsedAtUtc, k.UsageCount, k.DeprecatedAfterUtc,
                    k.DeprecatedAfterUtc != null && k.DeprecatedAfterUtc <= now))
                .ToListAsync(ct);
            return Results.Ok(new { generatedAtUtc = now, rows });
        })
        .RequireAuthorization("admin.users")
        .WithName("ApiKeyMigrationReport");

        g.MapPost("/api-keys/cutover", (
            ApiKeyCutoverToggleDto dto, IApiKeyToggle toggle) =>
        {
            toggle.EnforcementActive = dto.EnforcementActive;
            return Results.Ok(new { enforcementActive = toggle.EnforcementActive });
        })
        .RequireAuthorization("admin.users")
        .WithName("ApiKeyCutover");

        // Break-glass: requires an approver, a justification, and is time-boxed.
        g.MapPost("/break-glass/request", async (
            BreakGlassRequestDto dto, IClock clock, ZeroTrustDbContext db, IAuditLog audit,
            HttpContext ctx, CancellationToken ct) =>
        {
            try
            {
                var expires = clock.UtcNow.AddMinutes(Math.Clamp(dto.MinutesValid, 1, 60));
                var grant = new BreakGlassGrant(dto.Subject, dto.Requestor, dto.Approver, dto.Justification, expires);
                db.BreakGlassGrants.Add(grant);
                await db.SaveChangesAsync(ct);
                await audit.AppendAsync(AuditKind.BreakGlassRequested, dto.Requestor, "break_glass_request",
                    $"subject:{dto.Subject}", Cid(ctx), Ip(ctx), Ua(ctx),
                    $"approver={dto.Approver};expires={expires:O}", true, ct);
                await audit.AppendAsync(AuditKind.BreakGlassApproved, dto.Approver, "break_glass_approve",
                    $"grant:{grant.Id}", Cid(ctx), Ip(ctx), Ua(ctx),
                    $"justification_len={dto.Justification?.Length ?? 0}", true, ct);
                return Results.Ok(new { grantId = grant.Id, expiresAtUtc = grant.ExpiresAtUtc });
            }
            catch (Domain.Common.DomainException ex)
            {
                return Results.Problem(title: "invalid_break_glass", detail: ex.Message, statusCode: 422);
            }
        })
        .RequireAuthorization("admin.users")
        .WithName("RequestBreakGlass");

        g.MapPost("/break-glass/use/{grantId:guid}", async (
            Guid grantId, IClock clock, ZeroTrustDbContext db, IAuditLog audit,
            HttpContext ctx, CancellationToken ct) =>
        {
            var grant = await db.BreakGlassGrants.FirstOrDefaultAsync(g => g.Id == grantId, ct);
            if (grant is null) return Results.NotFound();
            if (!grant.IsActive(clock.UtcNow))
            {
                await audit.AppendAsync(AuditKind.AuthorizationDeny, ctx.User.Subject() ?? "?",
                    "break_glass_use", $"grant:{grantId}", Cid(ctx), Ip(ctx), Ua(ctx),
                    "grant expired/used/revoked", false, ct);
                return Results.Problem(title: "grant_inactive", statusCode: 409);
            }
            grant.MarkUsed();
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(AuditKind.BreakGlassUsed, grant.Subject, "break_glass_use",
                $"grant:{grantId}", Cid(ctx), Ip(ctx), Ua(ctx), "used", true, ct);
            return Results.Ok(new { used = true });
        })
        .RequireAuthorization("admin.users")
        .WithName("UseBreakGlass");

        return app;
    }

    private static string Cid(HttpContext ctx) => ctx.Items[Middleware.CorrelationIdMiddleware.HeaderName]?.ToString() ?? "-";
    private static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "-";
    private static string Ua(HttpContext ctx) => ctx.Request.Headers.UserAgent.ToString();
}

/// <summary>Singleton toggle for the API-key cutover phase. Test-visible.</summary>
public sealed class ApiKeyToggle : IApiKeyToggle
{
    public bool EnforcementActive { get; set; }
}

public interface IApiKeyToggle
{
    bool EnforcementActive { get; set; }
}
