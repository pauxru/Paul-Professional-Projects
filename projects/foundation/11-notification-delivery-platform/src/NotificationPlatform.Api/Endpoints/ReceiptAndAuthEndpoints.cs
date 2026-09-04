namespace NotificationPlatform.Api.Endpoints;

using System.IO;
using Microsoft.AspNetCore.Mvc;
using NotificationPlatform.Api.Auth;
using NotificationPlatform.Application.Receipts;

public static class ReceiptEndpoints
{
    public static IEndpointRouteBuilder MapReceiptEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/webhooks/receipts").WithTags("Receipts").AllowAnonymous();
        group.MapPost("/", async (HttpRequest request, IReceiptIngestor ingestor, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var sig = request.Headers["X-Signature"].FirstOrDefault() ?? string.Empty;
            var ts = request.Headers["X-Timestamp"].FirstOrDefault() ?? string.Empty;
            var nonce = request.Headers["X-Nonce"].FirstOrDefault() ?? string.Empty;
            var result = await ingestor.IngestAsync(sig, ts, nonce, body, ct);
            return result.Accepted
                ? Results.Accepted()
                : Results.Problem(title: result.Reason ?? "invalid_receipt", statusCode: 400);
        });
        return app;
    }
}

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Dev-only token endpoint for local demo and integration tests.  Not exposed in Production.
        app.MapPost("/api/v1/auth/token", (
                [FromBody] TokenRequest request,
                Auth.ITokenIssuer issuer) =>
            {
                var scopes = request.Scopes ?? new[] { Policies.SendNotifications, Policies.ManageTemplates, Policies.ManagePreferences, Policies.ManageSuppressions, Policies.ManageDlq, Policies.ViewAnalytics };
                var token = issuer.Issue(request.TenantId, scopes, TimeSpan.FromHours(2));
                return Results.Ok(new { access_token = token, token_type = "Bearer", expires_in = 7200 });
            })
            .WithTags("Auth")
            .AllowAnonymous();
        return app;
    }
}

public sealed record TokenRequest(Guid TenantId, string[]? Scopes);
