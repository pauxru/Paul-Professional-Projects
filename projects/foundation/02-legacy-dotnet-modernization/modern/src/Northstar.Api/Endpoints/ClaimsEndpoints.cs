using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Northstar.Application.Abstractions;
using Northstar.Application.Claims;
using Northstar.Application.Importing;
using Northstar.Domain.Claims;
using Northstar.Infrastructure.Documents;
using Microsoft.Extensions.Options;

namespace Northstar.Api.Endpoints;

public static class ClaimsEndpoints
{
    public static IEndpointRouteBuilder MapClaimsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1")
            .WithTags("Claims")
            .RequireRateLimiting("api");

        group.MapPost("/policyholders", async (CreatePolicyholderRequest request, ClaimApplicationService service, IAuditWriter auditWriter, IClock clock, HttpContext context, CancellationToken cancellationToken) =>
        {
            var errors = Validation.Errors(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, statusCode: StatusCodes.Status400BadRequest);
            }

            var id = await service.CreatePolicyholderAsync(new(request.Name, request.Email), cancellationToken);
            var response = new { id };
            await Audit.WriteAsync(context, auditWriter, clock, "policyholder.created", $"policyholders/{id}", null, response, cancellationToken);
            return Results.Created($"/api/v1/policyholders/{id}", response);
        }).RequireAuthorization("claims:adjust");

        group.MapPost("/policies", async (CreatePolicyRequest request, ClaimApplicationService service, IAuditWriter auditWriter, IClock clock, HttpContext context, CancellationToken cancellationToken) =>
        {
            var errors = Validation.Errors(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, statusCode: StatusCodes.Status400BadRequest);
            }

            var id = await service.CreatePolicyAsync(new(request.PolicyholderId, request.PolicyNumber, request.DeductibleAmount, request.LimitAmount, request.Currency), cancellationToken);
            var response = new { id };
            await Audit.WriteAsync(context, auditWriter, clock, "policy.created", $"policies/{id}", null, response, cancellationToken);
            return Results.Created($"/api/v1/policies/{id}", response);
        }).RequireAuthorization("claims:adjust");

        group.MapPost("/claims", async (IntakeClaimRequest request, ClaimApplicationService service, IAuditWriter auditWriter, IClock clock, HttpContext context, CancellationToken cancellationToken) =>
        {
            var errors = Validation.Errors(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, statusCode: StatusCodes.Status400BadRequest);
            }

            var claim = await service.IntakeAsync(new(request.PolicyNumber, request.Reference, request.ClaimedAmount, request.Currency), cancellationToken);
            await Audit.WriteAsync(context, auditWriter, clock, "claim.intake", $"claims/{claim.Id}", null, claim, cancellationToken);
            return Results.Created($"/api/v1/claims/{claim.Id}", claim);
        }).RequireAuthorization("claims:adjust");

        group.MapGet("/claims", async (string? policyholder, ClaimStatus? status, int? page, int? pageSize, ClaimApplicationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(policyholder, status, page ?? 1, pageSize ?? 25, cancellationToken)))
            .RequireAuthorization("claims:read");

        group.MapGet("/claims/{claimId:guid}", async (Guid claimId, ClaimApplicationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(claimId, cancellationToken)))
            .RequireAuthorization("claims:read");

        group.MapPost("/claims/{claimId:guid}/assessment", async (Guid claimId, AssessmentRequest request, ClaimApplicationService service, IAuditWriter auditWriter, IClock clock, HttpContext context, CancellationToken cancellationToken) =>
        {
            var errors = Validation.Errors(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, statusCode: StatusCodes.Status400BadRequest);
            }

            var claim = await service.StartAssessmentAsync(claimId, new(request.Adjuster, request.ReserveAmount, request.ExpectedVersion), cancellationToken);
            await Audit.WriteAsync(context, auditWriter, clock, "claim.assessed", $"claims/{claimId}", request, claim, cancellationToken);
            return Results.Ok(claim);
        }).RequireAuthorization("claims:adjust");

        group.MapPost("/claims/{claimId:guid}/transition", async (Guid claimId, TransitionRequest request, ClaimApplicationService service, IAuditWriter auditWriter, IClock clock, HttpContext context, CancellationToken cancellationToken) =>
        {
            var claim = await service.TransitionAsync(claimId, new(request.TargetStatus, request.ExpectedVersion), cancellationToken);
            await Audit.WriteAsync(context, auditWriter, clock, "claim.transitioned", $"claims/{claimId}", request, claim, cancellationToken);
            return Results.Ok(claim);
        })
            .RequireAuthorization("claims:approve");

        group.MapPost("/claims/{claimId:guid}/settle", async (Guid claimId, VersionRequest request, ClaimApplicationService service, IAuditWriter auditWriter, IClock clock, HttpContext context, CancellationToken cancellationToken) =>
        {
            var claim = await service.SettleAsync(claimId, request.ExpectedVersion, cancellationToken);
            await Audit.WriteAsync(context, auditWriter, clock, "claim.settled", $"claims/{claimId}", request, claim, cancellationToken);
            return Results.Ok(claim);
        })
            .RequireAuthorization("claims:approve");

        group.MapPost("/claims/{claimId:guid}/documents", async (Guid claimId, int expectedVersion, IFormFile document, ClaimApplicationService service, IAuditWriter auditWriter, IClock clock, IOptions<DocumentStoreOptions> documentOptions, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (document.Length == 0 || document.Length > documentOptions.Value.MaxBytes)
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["document"] = ["Document is empty or exceeds the configured size limit."] },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            await using var stream = document.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            var claim = await service.AttachDocumentAsync(claimId, new(document.FileName, document.ContentType, memory.ToArray(), expectedVersion), cancellationToken);
            await Audit.WriteAsync(context, auditWriter, clock, "claim.document-attached", $"claims/{claimId}", new { expectedVersion, document.FileName }, claim, cancellationToken);
            return Results.Ok(claim);
        }).RequireAuthorization("claims:adjust");

        group.MapPost("/migration/legacy-import", async (LegacyClaimImporter importer, IAuditWriter auditWriter, IClock clock, HttpContext context, CancellationToken cancellationToken) =>
        {
            var report = await importer.ImportAsync(cancellationToken);
            await Audit.WriteAsync(context, auditWriter, clock, "legacy.imported", "migration/legacy-import", null, report, cancellationToken);
            return Results.Ok(report);
        })
            .RequireAuthorization("claims:approve");

        return app;
    }
}

public sealed class CreatePolicyholderRequest
{
    [Required, StringLength(200, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [Required, EmailAddress, StringLength(320)]
    public string Email { get; init; } = string.Empty;
}

public sealed class CreatePolicyRequest
{
    public Guid PolicyholderId { get; init; }

    [Required, StringLength(64, MinimumLength = 3)]
    public string PolicyNumber { get; init; } = string.Empty;

    [Range(typeof(decimal), "0", "999999999")]
    public decimal DeductibleAmount { get; init; }

    [Range(typeof(decimal), "0.01", "999999999")]
    public decimal LimitAmount { get; init; }

    [Required, StringLength(3, MinimumLength = 3)]
    public string Currency { get; init; } = string.Empty;
}

public sealed class IntakeClaimRequest
{
    [Required, StringLength(64, MinimumLength = 3)]
    public string PolicyNumber { get; init; } = string.Empty;

    [Required, StringLength(64, MinimumLength = 3)]
    public string Reference { get; init; } = string.Empty;

    [Range(typeof(decimal), "0.01", "999999999")]
    public decimal ClaimedAmount { get; init; }

    [Required, StringLength(3, MinimumLength = 3)]
    public string Currency { get; init; } = string.Empty;
}

public sealed class AssessmentRequest
{
    [Required, StringLength(200, MinimumLength = 2)]
    public string Adjuster { get; init; } = string.Empty;

    [Range(typeof(decimal), "0", "999999999")]
    public decimal ReserveAmount { get; init; }

    [Range(1, int.MaxValue)]
    public int ExpectedVersion { get; init; }
}

public sealed class TransitionRequest
{
    public ClaimStatus TargetStatus { get; init; }

    [Range(1, int.MaxValue)]
    public int ExpectedVersion { get; init; }
}

public sealed class VersionRequest
{
    [Range(1, int.MaxValue)]
    public int ExpectedVersion { get; init; }
}
