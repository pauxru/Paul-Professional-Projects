using Microsoft.EntityFrameworkCore;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Audit;
using ZeroTrust.Infrastructure.Persistence;

namespace ZeroTrust.Api.Middleware;

/// <summary>
/// Simulated mTLS: instead of terminating TLS with client certificates (which would demand
/// a full PKI in this self-contained demo), we accept a header X-Client-Cert-Thumbprint
/// that a real fronting proxy would populate after mTLS termination, and verify it against
/// the partner registry. This is DOCUMENTED HONESTLY in docs/security/threat-model.md as a
/// simulation.
/// </summary>
public sealed class PartnerPostureMiddleware
{
    public const string ThumbprintHeader = "X-Client-Cert-Thumbprint";
    private readonly RequestDelegate _next;

    public PartnerPostureMiddleware(RequestDelegate next) { _next = next; }

    public async Task InvokeAsync(HttpContext ctx, ZeroTrustDbContext db, IAuditLog audit)
    {
        if (!ctx.Request.Path.StartsWithSegments("/api/v1/partner"))
        {
            await _next(ctx);
            return;
        }
        if (ctx.Request.Path.StartsWithSegments("/api/v1/partner/webhooks"))
        {
            // Inbound webhooks are verified by HMAC, not partner posture.
            await _next(ctx);
            return;
        }
        var user = ctx.User;
        if (!user.Identity?.IsAuthenticated ?? true)
        {
            await _next(ctx);
            return;
        }

        var partnerCode = user.FindFirst("partner_code")?.Value;
        if (string.IsNullOrEmpty(partnerCode))
        {
            await _next(ctx);
            return;
        }

        var partner = await db.Partners.AsNoTracking().FirstOrDefaultAsync(p => p.PartnerCode == partnerCode);
        if (partner is null || !partner.Enabled)
        {
            await Deny(ctx, audit, partnerCode, "partner_disabled_or_missing");
            return;
        }

        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!partner.IsIpAllowed(ip))
        {
            await Deny(ctx, audit, partnerCode, $"ip_not_allowed:{ip}");
            return;
        }

        var thumbprint = ctx.Request.Headers[ThumbprintHeader].ToString();
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            await Deny(ctx, audit, partnerCode, "missing_client_cert_thumbprint");
            return;
        }
        if (!string.Equals(thumbprint.Replace(":", "").Trim(), partner.ClientCertThumbprint,
                StringComparison.OrdinalIgnoreCase))
        {
            await Deny(ctx, audit, partnerCode, "client_cert_thumbprint_mismatch");
            return;
        }

        ctx.Items["PartnerCode"] = partner.PartnerCode;
        ctx.Items["PartnerRateLimit"] = partner.RateLimitPermitsPerMinute;
        await _next(ctx);
    }

    private static async Task Deny(HttpContext ctx, IAuditLog audit, string partner, string reason)
    {
        var cid = ctx.Items[CorrelationIdMiddleware.HeaderName]?.ToString() ?? "-";
        await audit.AppendAsync(AuditKind.AuthorizationDeny, partner, "partner_posture", ctx.Request.Path,
            cid, ctx.Connection.RemoteIpAddress?.ToString() ?? "-", ctx.Request.Headers.UserAgent.ToString(),
            reason, false, ctx.RequestAborted);
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        ctx.Response.ContentType = "application/problem+json";
        await ctx.Response.WriteAsync($@"{{""type"":""about:blank"",""title"":""Partner posture check failed"",""status"":403,""detail"":""{reason}"",""traceId"":""{cid}""}}");
    }
}
