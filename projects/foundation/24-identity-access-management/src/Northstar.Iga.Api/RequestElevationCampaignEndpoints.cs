using Northstar.Iga.Application;

namespace Northstar.Iga.Api;

public static class RequestElevationCampaignEndpoints
{
    public static IEndpointRouteBuilder MapRequestElevationCampaignEndpoints(this IEndpointRouteBuilder app)
    {
        var requests = app.MapGroup("/api/v1/requests").RequireRateLimiting("api").WithTags("Access Requests");
        requests.MapGet("/", async (IIgaService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetRequestsAsync(cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        requests.MapGet("/{requestId:guid}/steps", async (
            Guid requestId,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetApprovalStepsAsync(requestId, cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        requests.MapPost("/", async (
            CreateAccessRequestCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(command.Justification))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["justification"] = ["Justification is required."]
                });
            }
            if (Guid.TryParse(context.Actor(), out var authenticatedRequester) &&
                authenticatedRequester != command.RequesterId)
            {
                return ValidationResults.ActorMismatch();
            }
            var created = await service.CreateAccessRequestAsync(
                command, context.Actor(), context.CorrelationId(), cancellationToken);
            return Results.Created($"/api/v1/requests/{created.Id}", created);
        }).RequireAuthorization(ApiPolicies.Read);
        requests.MapPost("/{requestId:guid}/steps/{stepId:guid}/approve", async (
            Guid requestId,
            Guid stepId,
            ApprovalDecisionCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            if (!context.ActorMatches(command.ActorId)) return ValidationResults.ActorMismatch();
            return Results.Ok(await service.ApproveRequestAsync(
                requestId, stepId, command, context.Actor(), context.CorrelationId(), cancellationToken));
        })
            .RequireAuthorization(ApiPolicies.Approve);
        requests.MapPost("/{requestId:guid}/steps/{stepId:guid}/reject", async (
            Guid requestId,
            Guid stepId,
            ApprovalDecisionCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            if (!context.ActorMatches(command.ActorId)) return ValidationResults.ActorMismatch();
            return Results.Ok(await service.RejectRequestAsync(
                requestId, stepId, command, context.Actor(), context.CorrelationId(), cancellationToken));
        })
            .RequireAuthorization(ApiPolicies.Approve);
        requests.MapPost("/{requestId:guid}/steps/{stepId:guid}/delegate", async (
            Guid requestId,
            Guid stepId,
            DelegateApprovalCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            if (!context.ActorMatches(command.ActorId)) return ValidationResults.ActorMismatch();
            await service.DelegateApprovalAsync(
                requestId, stepId, command, context.Actor(), context.CorrelationId(), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(ApiPolicies.Approve);
        requests.MapPost("/escalate", async (
            EscalationRequest request,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                escalated = await service.EscalateOverdueApprovalsAsync(
                    request.EscalationApproverId,
                    context.Actor(),
                    context.CorrelationId(),
                    cancellationToken)
            })).RequireAuthorization(ApiPolicies.Admin);

        var elevations = app.MapGroup("/api/v1/elevations").RequireRateLimiting("api").WithTags("JIT Elevations");
        elevations.MapGet("/", async (IIgaService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetElevationsAsync(cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        elevations.MapPost("/", async (
            CreateElevationCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(command.Justification) ||
                string.IsNullOrWhiteSpace(command.TicketReference))
            {
                return ValidationResults.Required(
                    ("justification", command.Justification),
                    ("ticketReference", command.TicketReference));
            }
            var created = await service.CreateElevationAsync(
                command, context.Actor(), context.CorrelationId(), cancellationToken);
            return Results.Created($"/api/v1/elevations/{created.Id}", created);
        }).RequireAuthorization(ApiPolicies.Admin);
        elevations.MapPost("/{elevationId:guid}/approve", async (
            Guid elevationId,
            ElevationApprovalRequest request,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            if (!context.ActorMatches(request.ApproverId)) return ValidationResults.ActorMismatch();
            return Results.Ok(await service.ApproveElevationAsync(
                elevationId,
                request.ApproverId,
                context.Actor(),
                context.CorrelationId(),
                cancellationToken));
        })
            .RequireAuthorization(ApiPolicies.Approve);
        elevations.MapPost("/{elevationId:guid}/revoke", async (
            Guid elevationId,
            ElevationRevocationRequest request,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.RevokeElevationAsync(
                elevationId,
                request.Reason,
                context.Actor(),
                context.CorrelationId(),
                cancellationToken)))
            .RequireAuthorization(ApiPolicies.Admin);
        elevations.MapPost("/expire", async (
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                expired = await service.ExpireElevationsAsync(
                    context.Actor(), context.CorrelationId(), cancellationToken)
            })).RequireAuthorization(ApiPolicies.Admin);

        var campaigns = app.MapGroup("/api/v1/campaigns").RequireRateLimiting("api").WithTags("Certification");
        campaigns.MapGet("/", async (IIgaService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetCampaignsAsync(cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        campaigns.MapPost("/", async (
            CreateCampaignCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(command.Name) ||
                string.IsNullOrWhiteSpace(command.ScopeType) ||
                string.IsNullOrWhiteSpace(command.ScopeValue))
            {
                return ValidationResults.Required(
                    ("name", command.Name),
                    ("scopeType", command.ScopeType),
                    ("scopeValue", command.ScopeValue));
            }
            var created = await service.CreateCampaignAsync(
                command, context.Actor(), context.CorrelationId(), cancellationToken);
            return Results.Created($"/api/v1/campaigns/{created.Id}", created);
        }).RequireAuthorization(ApiPolicies.Admin);
        campaigns.MapGet("/{campaignId:guid}/items", async (
            Guid campaignId,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetCampaignItemsAsync(campaignId, cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        campaigns.MapGet("/{campaignId:guid}/progress", async (
            Guid campaignId,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetCampaignProgressAsync(campaignId, cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        campaigns.MapPost("/{campaignId:guid}/certify", async (
            Guid campaignId,
            CertificationCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            if (!context.ActorMatches(command.ReviewerId)) return ValidationResults.ActorMismatch();
            return Results.Ok(await service.CertifyItemsAsync(
                campaignId, command, context.Actor(), context.CorrelationId(), cancellationToken));
        })
            .RequireAuthorization(ApiPolicies.Approve);
        campaigns.MapPost("/auto-revoke-overdue", async (
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                autoRevoked = await service.AutoRevokeOverdueCampaignsAsync(
                    context.Actor(), context.CorrelationId(), cancellationToken)
            })).RequireAuthorization(ApiPolicies.Admin);

        return app;
    }
}

public sealed record EscalationRequest(Guid EscalationApproverId);
public sealed record ElevationApprovalRequest(Guid ApproverId);
public sealed record ElevationRevocationRequest(string Reason);
