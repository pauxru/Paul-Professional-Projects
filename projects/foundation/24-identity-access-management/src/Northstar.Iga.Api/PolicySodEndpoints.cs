using Northstar.Iga.Application;

namespace Northstar.Iga.Api;

public static class PolicySodEndpoints
{
    public static IEndpointRouteBuilder MapPolicyAndSodEndpoints(this IEndpointRouteBuilder app)
    {
        var policies = app.MapGroup("/api/v1/policies").RequireRateLimiting("api").WithTags("Policies");
        policies.MapGet("/", async (IIgaService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetPoliciesAsync(cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        policies.MapPost("/", async (
            CreatePolicyCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(command.Name) || string.IsNullOrWhiteSpace(command.PermissionPattern))
            {
                return ValidationResults.Required(
                    ("name", command.Name),
                    ("permissionPattern", command.PermissionPattern));
            }
            var created = await service.CreatePolicyAsync(
                command, context.Actor(), context.CorrelationId(), cancellationToken);
            return Results.Created($"/api/v1/policies/{created.Id}", created);
        }).RequireAuthorization(ApiPolicies.Admin);
        policies.MapPost("/simulate", async (
            PolicySimulationCommand command,
            IIgaService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.SimulatePolicyAsync(command, cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);

        app.MapPost("/api/v1/authz/evaluate", async (
            AuthorizationEvaluationCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(command.Permission))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["permission"] = ["Permission is required."]
                });
            }
            return Results.Ok(await service.EvaluateAsync(
                command, context.Actor(), context.CorrelationId(), cancellationToken));
        }).RequireAuthorization(ApiPolicies.Read).RequireRateLimiting("api").WithTags("Authorization");

        var sod = app.MapGroup("/api/v1/sod").RequireRateLimiting("api").WithTags("Separation of Duties");
        sod.MapGet("/violations", async (IIgaService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ScanSoDAsync(cancellationToken)))
            .RequireAuthorization(ApiPolicies.Read);
        sod.MapPost("/rules", async (
            CreateSoDRuleCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            var created = await service.CreateSoDRuleAsync(
                command, context.Actor(), context.CorrelationId(), cancellationToken);
            return Results.Created($"/api/v1/sod/rules/{created.Id}", created);
        }).RequireAuthorization(ApiPolicies.Admin);
        sod.MapPost("/exceptions", async (
            CreateSoDExceptionCommand command,
            HttpContext context,
            IIgaService service,
            CancellationToken cancellationToken) =>
        {
            command = command with { ApprovedBy = context.Actor() };
            var created = await service.CreateSoDExceptionAsync(
                command, context.Actor(), context.CorrelationId(), cancellationToken);
            return Results.Created($"/api/v1/sod/exceptions/{created.Id}", created);
        }).RequireAuthorization(ApiPolicies.Approve);

        return app;
    }
}
