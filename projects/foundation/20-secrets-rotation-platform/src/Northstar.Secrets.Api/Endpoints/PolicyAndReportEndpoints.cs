using System.Security.Cryptography;
using Northstar.Secrets.Api.Auth;
using Northstar.Secrets.Api.Middleware;
using Northstar.Secrets.Application;
using Northstar.Secrets.Domain;
using Northstar.Secrets.Infrastructure.Notifications;

namespace Northstar.Secrets.Api.Endpoints;

public static class PolicyAndReportEndpoints
{
    public sealed record CreatePolicyRequest(
        string Subject,
        string PathPattern,
        bool CanManageMetadata,
        bool CanReadValues,
        bool CanOperateRotations,
        bool CanBreakGlass);

    public static IEndpointRouteBuilder MapPolicyAndReportEndpoints(this IEndpointRouteBuilder app)
    {
        var policies = app.MapGroup("/api/v1/policies")
            .WithTags("Policies")
            .RequireAuthorization(ScopePolicies.ManageSecrets);
        policies.MapGet("/", ListPoliciesAsync);
        policies.MapPost("/", CreatePolicyAsync);
        policies.MapPost("/master-key/rotate", RotateMasterKeyAsync);

        var reports = app.MapGroup("/api/v1/reports")
            .WithTags("Reports")
            .RequireAuthorization(ScopePolicies.ManageSecrets);
        reports.MapGet("/upcoming-expiries", UpcomingExpiriesAsync);
        reports.MapGet("/access/{secretName}", AccessReportAsync);
        reports.MapGet("/anomalies", AnomaliesAsync);
        reports.MapGet("/notification-dead-letters", DeadLetters);
        return app;
    }

    private static async Task<IResult> ListPoliciesAsync(
        ISecretsRepository repository,
        CancellationToken cancellationToken) =>
        Results.Ok(await repository.ListAccessPoliciesAsync(null, cancellationToken));

    private static async Task<IResult> CreatePolicyAsync(
        CreatePolicyRequest request,
        ISecretsRepository repository,
        CancellationToken cancellationToken)
    {
        var policy = new AccessPolicy(
            Guid.NewGuid(),
            request.Subject,
            request.PathPattern,
            request.CanManageMetadata,
            request.CanReadValues,
            request.CanOperateRotations,
            request.CanBreakGlass);
        await repository.AddAccessPolicyAsync(policy, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/policies/{policy.Id}", policy);
    }

    private static async Task<IResult> RotateMasterKeyAsync(
        SecretLifecycleService lifecycle,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var version = $"local-{clock.UtcNow:yyyyMMddHHmmss}";
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var count = await lifecycle.RotateMasterKeyAsync(version, key, cancellationToken);
            return Results.Ok(new { keyVersion = version, rewrappedVersionCount = count });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task<IResult> UpcomingExpiriesAsync(
        int horizonHours,
        ReportingService reporting,
        CancellationToken cancellationToken)
    {
        var horizon = TimeSpan.FromHours(Math.Clamp(horizonHours == 0 ? 720 : horizonHours, 1, 8760));
        return Results.Ok(await reporting.UpcomingExpiryAsync(horizon, cancellationToken));
    }

    private static async Task<IResult> AccessReportAsync(
        string secretName,
        DateTimeOffset? since,
        ReportingService reporting,
        CancellationToken cancellationToken) =>
        Results.Ok(await reporting.AccessReportAsync(
            SecretNameCodec.Decode(secretName), since, cancellationToken));

    private static async Task<IResult> AnomaliesAsync(
        DateTimeOffset? since,
        ReportingService reporting,
        CancellationToken cancellationToken) =>
        Results.Ok(await reporting.AccessAnomaliesAsync(since, cancellationToken));

    private static IResult DeadLetters(IDeadLetterStore store) => Results.Ok(store.List());
}
