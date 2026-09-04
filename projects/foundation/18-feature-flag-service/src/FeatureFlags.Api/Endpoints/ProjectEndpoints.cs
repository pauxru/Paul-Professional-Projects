using FeatureFlags.Api.Contracts;
using FeatureFlags.Api.Security;
using FeatureFlags.Application;
using FeatureFlags.Domain;
using Microsoft.AspNetCore.Mvc;

namespace FeatureFlags.Api.Endpoints;

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization("FlagsRead").RequireRateLimiting("api");

        projects.MapGet("/", async (FlagService service, HttpContext context) => Results.Ok(await service.ListProjectsAsync(context.RequestAborted)));
        projects.MapPost("/", async (CreateProjectRequest request, FlagService service, HttpContext context) =>
        {
            if (ApiValidation.Errors(request) is { } errors) return Results.ValidationProblem(errors);
            var project = await service.CreateProjectAsync(request.Key, request.Name, context.RequestAborted);
            return Results.Created($"/api/v1/projects/{project.Key}", project);
        }).RequireAuthorization("FlagsWrite");

        projects.MapGet("/{projectKey}/environments", async (string projectKey, FlagService service, HttpContext context) =>
            Results.Ok(await service.ListEnvironmentsAsync(projectKey, context.RequestAborted)));
        projects.MapPost("/{projectKey}/environments", async (string projectKey, CreateEnvironmentRequest request, FlagService service, HttpContext context) =>
        {
            if (ApiValidation.Errors(request) is { } errors) return Results.ValidationProblem(errors);
            var environment = await service.CreateEnvironmentAsync(projectKey, request.Key, request.Name, request.ServerSdkKey, request.ClientSdkKey, context.RequestAborted);
            return Results.Created($"/api/v1/projects/{projectKey}/environments/{environment.Environment.Key}", environment.Environment);
        }).RequireAuthorization("FlagsWrite");

        projects.MapGet("/{projectKey}/environments/{environmentKey}/flags", async (string projectKey, string environmentKey, FlagService service, HttpContext context) =>
        {
            var environment = await service.GetEnvironmentAsync(projectKey, environmentKey, context.RequestAborted);
            return Results.Ok(environment.Configuration.Flags);
        });

        projects.MapPut("/{projectKey}/environments/{environmentKey}/flags/{flagKey}", async (string projectKey, string environmentKey, string flagKey, SaveFlagRequest request, FlagService service, HttpContext context) =>
        {
            var errors = ApiValidation.Errors(request);
            if (errors is not null || request.Flag is null) return Results.ValidationProblem(errors ?? new Dictionary<string, string[]> { ["flag"] = ["A flag definition is required."] });
            if (!string.Equals(flagKey, request.Flag.Key, StringComparison.Ordinal)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["flag.key"] = ["The route flag key and body flag key must match."] });
            var flag = await service.SaveFlagAsync(projectKey, environmentKey, request.Flag, ChangeContext(context, request.Comment, request.TicketReference), bypassProductionApproval: false, context.RequestAborted);
            return Results.Ok(flag);
        }).RequireAuthorization("FlagsWrite");

        projects.MapPost("/{projectKey}/environments/{environmentKey}/flags", async (string projectKey, string environmentKey, SaveFlagRequest request, FlagService service, HttpContext context) =>
        {
            var errors = ApiValidation.Errors(request);
            if (errors is not null || request.Flag is null) return Results.ValidationProblem(errors ?? new Dictionary<string, string[]> { ["flag"] = ["A flag definition is required."] });
            var flag = await service.SaveFlagAsync(projectKey, environmentKey, request.Flag, ChangeContext(context, request.Comment, request.TicketReference), bypassProductionApproval: false, context.RequestAborted);
            return Results.Created($"/api/v1/projects/{projectKey}/environments/{environmentKey}/flags/{flag.Key}", flag);
        }).RequireAuthorization("FlagsWrite");

        projects.MapPost("/{projectKey}/environments/{environmentKey}/flags/{flagKey}/kill-switch", async (string projectKey, string environmentKey, string flagKey, KillSwitchRequest request, FlagService service, HttpContext context) =>
        {
            if (ApiValidation.Errors(request) is { } errors) return Results.ValidationProblem(errors);
            var flag = await service.SetKillSwitchAsync(projectKey, environmentKey, flagKey, request.Enabled, ChangeContext(context, request.Comment, request.TicketReference), context.RequestAborted);
            return Results.Ok(flag);
        }).RequireAuthorization("FlagsWrite");

        projects.MapPost("/{projectKey}/environments/{environmentKey}/evaluate", async (string projectKey, string environmentKey, EvaluationRequest request, FlagService service, HttpContext context) =>
        {
            if (ApiValidation.Errors(request) is { } errors) return Results.ValidationProblem(errors);
            var evaluationContext = new EvaluationContext(request.ContextKey, request.Kind, request.Attributes);
            return Results.Ok(await service.EvaluateAsync(projectKey, environmentKey, request.FlagKey, evaluationContext, context.RequestAborted));
        });

        projects.MapPost("/{projectKey}/promotions/preview", async (string projectKey, PromotionRequest request, FlagService service, HttpContext context) =>
        {
            if (ApiValidation.Errors(request) is { } errors) return Results.ValidationProblem(errors);
            return Results.Ok(await service.PreviewPromotionAsync(projectKey, request.SourceEnvironment, request.TargetEnvironment, context.RequestAborted));
        });
        projects.MapPost("/{projectKey}/promotions/apply", async (string projectKey, PromotionRequest request, FlagService service, HttpContext context) =>
        {
            if (ApiValidation.Errors(request) is { } errors) return Results.ValidationProblem(errors);
            await service.PromoteAsync(projectKey, request.SourceEnvironment, request.TargetEnvironment, ChangeContext(context, request.Comment, request.TicketReference), bypassProductionApproval: false, context.RequestAborted);
            return Results.NoContent();
        }).RequireAuthorization("FlagsWrite");
        projects.MapPost("/{projectKey}/promotions/approval", async (string projectKey, PromotionRequest request, FlagService service, HttpContext context) =>
        {
            if (ApiValidation.Errors(request) is { } errors) return Results.ValidationProblem(errors);
            var approval = await service.RequestPromotionAsync(projectKey, request.SourceEnvironment, request.TargetEnvironment, ChangeContext(context, request.Comment, request.TicketReference), context.RequestAborted);
            return Results.Created($"/api/v1/projects/{projectKey}/environments/{request.TargetEnvironment}/approvals/{approval.Id}", approval);
        }).RequireAuthorization("FlagsWrite");

        projects.MapGet("/{projectKey}/environments/{environmentKey}/audit", async (string projectKey, string environmentKey, FlagService service, HttpContext context) =>
            Results.Ok(await service.GetAuditAsync(projectKey, environmentKey, context.RequestAborted)));
        projects.MapPost("/{projectKey}/environments/{environmentKey}/audit/{auditId:guid}/revert", async (Guid auditId, FlagService service, HttpContext context) =>
        {
            await service.RevertAsync(auditId, ChangeContext(context), context.RequestAborted);
            return Results.NoContent();
        }).RequireAuthorization("FlagsWrite");

        projects.MapGet("/{projectKey}/environments/{environmentKey}/stale-flags", async (string projectKey, string environmentKey, int? days, FlagService service, HttpContext context) =>
            Results.Ok(await service.GetStaleFlagsAsync(projectKey, environmentKey, days ?? 30, context.RequestAborted)));

        projects.MapPost("/{projectKey}/environments/{environmentKey}/approvals", async (string projectKey, string environmentKey, SaveFlagRequest request, FlagService service, HttpContext context) =>
        {
            var errors = ApiValidation.Errors(request);
            if (errors is not null || request.Flag is null) return Results.ValidationProblem(errors ?? new Dictionary<string, string[]> { ["flag"] = ["A flag definition is required."] });
            var approval = await service.RequestFlagChangeAsync(projectKey, environmentKey, request.Flag, ChangeContext(context, request.Comment, request.TicketReference), context.RequestAborted);
            return Results.Created($"/api/v1/projects/{projectKey}/environments/{environmentKey}/approvals/{approval.Id}", approval);
        }).RequireAuthorization("FlagsWrite");
        projects.MapGet("/{projectKey}/environments/{environmentKey}/approvals", async (string projectKey, string environmentKey, FlagService service, HttpContext context) =>
            Results.Ok(await service.GetApprovalsAsync(projectKey, environmentKey, context.RequestAborted)));
        projects.MapPost("/{projectKey}/environments/{environmentKey}/approvals/{approvalId:guid}/review", async (Guid approvalId, ReviewApprovalRequest request, FlagService service, HttpContext context) =>
        {
            if (ApiValidation.Errors(request) is { } errors) return Results.ValidationProblem(errors);
            return Results.Ok(await service.ReviewApprovalAsync(approvalId, context.User.Actor(), request.Approve, request.Comment, context.RequestAborted));
        }).RequireAuthorization("FlagsApprove");
        projects.MapPost("/{projectKey}/environments/{environmentKey}/approvals/{approvalId:guid}/apply", async (Guid approvalId, FlagService service, HttpContext context) =>
        {
            await service.ApplyApprovalAsync(approvalId, context.User.Actor(), context.RequestAborted);
            return Results.NoContent();
        }).RequireAuthorization("FlagsApprove");

        return app;
    }

    private static ChangeContext ChangeContext(HttpContext context, string? comment = null, string? ticket = null) => new(
        context.User.Actor(), comment, ticket, context.TraceIdentifier, context.Connection.RemoteIpAddress?.ToString(), context.Request.Headers.UserAgent.ToString());
}
