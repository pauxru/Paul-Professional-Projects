using System.Text;
using Microsoft.EntityFrameworkCore;
using ZeroTrust.Api.Authorization;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Audit;
using ZeroTrust.Domain.Partner;
using ZeroTrust.Infrastructure.Persistence;

namespace ZeroTrust.Api.Endpoints;

public sealed record PaymentInitiationDto(
    string ExternalReference,
    string DebtorAccountNumber,
    string CreditorAccountNumber,
    decimal AmountMinorUnits,
    string Currency);

public sealed record PaymentInitiationResponseDto(
    Guid Id, string ExternalReference, string Status, string CorrelationId);

public sealed record WebhookEventDto(string Type, string Payload);

public static class PartnerEndpoints
{
    public static IEndpointRouteBuilder MapPartnerEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/partner")
            .RequireRateLimiting("partner-token-bucket");

        // Payment initiation — requires partner.payments.initiate scope + partner posture (IP + thumbprint).
        g.MapPost("/payments", async (
            PaymentInitiationDto dto,
            HttpContext ctx,
            ZeroTrustDbContext db,
            IAuditLog audit,
            CancellationToken ct) =>
        {
            var partnerCode = ctx.Items["PartnerCode"]?.ToString() ?? ctx.User.FindFirst("partner_code")?.Value ?? "?";
            var cid = ctx.Items[Middleware.CorrelationIdMiddleware.HeaderName]?.ToString() ?? "-";
            var existing = await db.PaymentInitiations
                .FirstOrDefaultAsync(p => p.PartnerCode == partnerCode && p.ExternalReference == dto.ExternalReference, ct);
            if (existing is not null)
            {
                await audit.AppendAsync(AuditKind.AuthorizationAllow, partnerCode, "initiate_payment_idempotent",
                    $"payment:{existing.Id}", cid, Ip(ctx), Ua(ctx), "returned existing", true, ct);
                return Results.Ok(new PaymentInitiationResponseDto(existing.Id, existing.ExternalReference,
                    existing.Status, existing.CorrelationId));
            }
            if (dto.AmountMinorUnits <= 0)
                return Results.Problem(title: "invalid_amount", detail: "amount must be positive", statusCode: 422);

            var pi = new PaymentInitiationRequest(
                dto.ExternalReference, partnerCode, dto.DebtorAccountNumber, dto.CreditorAccountNumber,
                dto.AmountMinorUnits, dto.Currency ?? "USD", "v1", cid);
            db.PaymentInitiations.Add(pi);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(AuditKind.AuthorizationAllow, partnerCode, "initiate_payment",
                $"payment:{pi.Id}", cid, Ip(ctx), Ua(ctx),
                $"amount={dto.AmountMinorUnits} {dto.Currency}", true, ct);
            return Results.Created($"/api/v1/partner/payments/{pi.Id}", new PaymentInitiationResponseDto(
                pi.Id, pi.ExternalReference, pi.Status, pi.CorrelationId));
        })
        .RequireAuthorization("partner.payments.initiate")
        .WithName("InitiatePayment");

        g.MapGet("/payments/{id:guid}", async (
            Guid id, HttpContext ctx, ZeroTrustDbContext db, CancellationToken ct) =>
        {
            var partnerCode = ctx.Items["PartnerCode"]?.ToString() ?? ctx.User.FindFirst("partner_code")?.Value ?? "?";
            var pi = await db.PaymentInitiations.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id && p.PartnerCode == partnerCode, ct);
            return pi is null
                ? Results.NotFound()
                : Results.Ok(new PaymentInitiationResponseDto(pi.Id, pi.ExternalReference, pi.Status, pi.CorrelationId));
        })
        .RequireAuthorization("partner.payments.read")
        .WithName("GetPartnerPayment");

        // Outbound webhook demo: sign + return signed headers so a partner could integrate.
        g.MapPost("/outbound-webhook/sign-demo", async (
            WebhookEventDto dto, IWebhookSigner signer, IClock clock, CancellationToken ct) =>
        {
            var body = System.Text.Json.JsonSerializer.Serialize(dto);
            var ts = new DateTimeOffset(clock.UtcNow, TimeSpan.Zero).ToUnixTimeSeconds();
            var nonce = Guid.NewGuid().ToString("N");
            var signature = signer.Sign(body, ts, nonce, "partner-webhook-secret-example");
            _ = ct;
            return Results.Ok(new
            {
                url = "https://partner.example/webhooks/ntsf",
                headers = new
                {
                    x_ntsf_signature = signature,
                    x_ntsf_timestamp = ts,
                    x_ntsf_nonce = nonce
                },
                body
            });
        })
        .RequireAuthorization("partner.payments.read")
        .WithName("OutboundWebhookSignDemo");

        // Inbound webhook: HMAC verification.
        app.MapPost("/api/v1/partner/webhooks/inbound", async (
            HttpRequest req,
            IWebhookVerifier verifier,
            IClock clock,
            IAuditLog audit,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(req.Body, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync(ct);
            var sig = req.Headers["X-NTSF-Signature"].ToString();
            var ts = req.Headers["X-NTSF-Timestamp"].ToString();
            var nonce = req.Headers["X-NTSF-Nonce"].ToString();

            var outcome = verifier.Verify(raw, sig, ts, nonce, "partner-webhook-secret-example",
                clock.UtcNow, TimeSpan.FromMinutes(5));

            if (!outcome.IsValid)
            {
                await audit.AppendAsync(AuditKind.WebhookRejected, "external", "inbound_webhook", "webhooks/inbound",
                    "-", "-", "-", outcome.Reason ?? "unknown", false, ct);
                return Results.Problem(title: "invalid_signature", detail: outcome.Reason, statusCode: 400);
            }
            return Results.Ok(new { received = true });
        })
        .AllowAnonymous()
        .WithName("InboundWebhook");

        return app;
    }

    private static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "-";
    private static string Ua(HttpContext ctx) => ctx.Request.Headers.UserAgent.ToString();
}
