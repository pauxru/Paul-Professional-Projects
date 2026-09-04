using System.Security.Claims;
using ReconEngine.Api.Contracts;
using ReconEngine.Application.RuleSets;
using ReconEngine.Domain.ValueObjects;

namespace ReconEngine.Api.Endpoints;

public static class RuleSetsEndpoints
{
    public static IEndpointRouteBuilder MapRuleSetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/rulesets").WithTags("RuleSets").RequireAuthorization();

        group.MapGet("/", async (RuleSetService svc, CancellationToken ct) =>
        {
            var all = await svc.ListAsync(ct);
            return Results.Ok(all.Select(r => r.ToResponse()));
        })
        .WithName("ListRuleSets");

        group.MapGet("/active", async (RuleSetService svc, CancellationToken ct) =>
        {
            var active = await svc.GetActiveAsync(ct);
            return active is null ? Results.NotFound() : Results.Ok(active.ToResponse());
        })
        .WithName("GetActiveRuleSet");

        group.MapGet("/{id:guid}", async (Guid id, RuleSetService svc, CancellationToken ct) =>
        {
            var rs = await svc.GetAsync(id, ct);
            return rs is null ? Results.NotFound() : Results.Ok(rs.ToResponse());
        })
        .WithName("GetRuleSet");

        group.MapPost("/", async (CreateRuleSetRequest request, ClaimsPrincipal user, RuleSetService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Ruleset name is required." });

            var definition = request.Definition ?? MatchingRuleSetDefinition.Default;
            var created = await svc.CreateVersionAsync(
                request.Name, definition, request.Description ?? string.Empty,
                user.CurrentUser(), request.Activate, ct);

            return Results.Created($"/api/v1/rulesets/{created.Id}", created.ToResponse());
        })
        .WithName("CreateRuleSet")
        .WithSummary("Create the next version of a named ruleset (optionally activating it).");

        group.MapPost("/{id:guid}/activate", async (Guid id, RuleSetService svc, CancellationToken ct) =>
        {
            var rs = await svc.ActivateAsync(id, ct);
            return Results.Ok(rs.ToResponse());
        })
        .WithName("ActivateRuleSet");

        return app;
    }
}
