using Idp.Api.Auth;
using Idp.Application.Abstractions;
using Idp.Application.Configuration;
using Idp.Infrastructure.Exporting;
using Idp.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Idp.Api.Endpoints;

/// <summary>Health, the dev-only token issuer and the simulated downstream ERP HTTP endpoint.</summary>
public static class SystemEndpoints
{
    public sealed record ErpInvoiceRequest(Guid DocumentId, string? IdempotencyKey, string? Payload);

    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new
        {
            status = "healthy",
            service = "idp-api",
            timeUtc = DateTime.UtcNow
        })).WithTags("System").WithName("Health").AllowAnonymous();

        // Liveness and readiness probes (Kubernetes-style). Both are anonymous and cheap; readiness
        // additionally confirms the database can be reached before declaring the service ready.
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
            .WithTags("System").WithName("HealthLive").AllowAnonymous();

        app.MapGet("/health/ready", async (IdpDbContext db, CancellationToken ct) =>
        {
            var ready = await db.Database.CanConnectAsync(ct);
            return ready
                ? Results.Ok(new { status = "ready" })
                : Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).WithTags("System").WithName("HealthReady").AllowAnonymous();

        // Simulated downstream ERP. Idempotent booking keyed by the idempotency key so a retry after
        // an ambiguous failure never double-books. Anonymous because it models an external system;
        // in production this would be a separate service with its own auth.
        app.MapPost("/simulated-erp/invoices", (
            ErpInvoiceRequest request, HttpContext http, SimulatedErpLedger ledger, IClock clock) =>
        {
            var key = !string.IsNullOrWhiteSpace(request.IdempotencyKey)
                ? request.IdempotencyKey!
                : http.Request.Headers["Idempotency-Key"].FirstOrDefault()
                  ?? request.DocumentId.ToString("N");

            var (booking, alreadyExisted) = ledger.Book(
                key, request.DocumentId, request.Payload ?? string.Empty, clock.UtcNow);

            return Results.Ok(new
            {
                reference = booking.Reference,
                idempotencyKey = booking.IdempotencyKey,
                duplicate = alreadyExisted,
                bookedAtUtc = booking.BookedAtUtc
            });
        }).WithTags("System").WithName("SimulatedErpInvoices").AllowAnonymous();

        // Dev-only token issuer for local use and demos. Never mapped outside Development.
        if (app.Environment.IsDevelopment())
        {
            app.MapPost("/api/v1/dev/token", (
                Contracts.TokenRequest? request, IOptions<JwtOptions> jwt) =>
            {
                var subject = string.IsNullOrWhiteSpace(request?.Subject)
                    ? "dev-user"
                    : request!.Subject!;
                var perms = request?.Permissions is { Length: > 0 }
                    ? request.Permissions!
                    : Permissions.All;
                var token = TokenFactory.Create(jwt.Value, subject, perms);
                return Results.Ok(new
                {
                    access_token = token,
                    token_type = "Bearer",
                    expires_in = 8 * 3600,
                    permissions = perms
                });
            }).WithTags("System").WithName("DevToken").AllowAnonymous();
        }
    }
}
