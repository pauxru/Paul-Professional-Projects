using FraudPipeline.Api.Contracts;
using FraudPipeline.Application.Abstractions;
using FraudPipeline.Application.Feedback;
using FraudPipeline.Application.Scoring;
using FraudPipeline.Application.Shadow;
using FraudPipeline.Domain.Abstractions;
using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.Rules;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace FraudPipeline.Api.Endpoints;

public static class RulesetEndpoints
{
    public static IEndpointRouteBuilder MapRulesetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/rulesets").WithTags("Rulesets");

        group.MapGet("/", ListAsync)
             .RequireAuthorization("risk:investigate");

        group.MapPost("/{version}/activate", ActivateAsync)
             .RequireAuthorization("risk:admin");

        group.MapPost("/{version}/shadow", ShadowAsync)
             .RequireAuthorization("risk:admin");

        group.MapPost("/simulate", SimulateAsync)
             .RequireAuthorization("risk:investigate");

        return app;
    }

    private static async Task<Ok<List<RulesetSummary>>> ListAsync(IRulesetRepository repo, CancellationToken ct)
    {
        var items = await repo.ListAsync(ct);
        return TypedResults.Ok(items.Select(r => new RulesetSummary(r.Version, r.Name, r.IsActive, r.IsShadow, r.CreatedAt, r.ActivatedAt)).ToList());
    }

    private static async Task<Results<Ok, NotFound>> ActivateAsync(string version, IRulesetRepository repo, IClock clock, CancellationToken ct)
    {
        var target = await repo.GetByVersionAsync(version, ct);
        if (target is null) return TypedResults.NotFound();
        var current = await repo.GetActiveAsync(ct);
        current?.Deactivate();
        target.Activate(clock.UtcNow);
        await repo.SaveAsync(ct);
        return TypedResults.Ok();
    }

    private static async Task<Results<Ok, NotFound, BadRequest<ProblemDetails>>> ShadowAsync(string version, IRulesetRepository repo, CancellationToken ct)
    {
        var target = await repo.GetByVersionAsync(version, ct);
        if (target is null) return TypedResults.NotFound();
        try
        {
            var currentShadow = await repo.GetShadowAsync(ct);
            currentShadow?.ClearShadow();
            target.MakeShadow();
            await repo.SaveAsync(ct);
            return TypedResults.Ok();
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.BadRequest(new ProblemDetails { Title = "Cannot shadow", Detail = ex.Message, Status = 400 });
        }
    }

    private static async Task<Ok<SimulationResponse>> SimulateAsync(
        [FromBody] SimulateRulesetRequest request,
        IRulesetRepository repo,
        ShadowComparator comparator,
        CancellationToken ct)
    {
        var comparison = await comparator.CompareAsync(request.Limit, ct);
        return TypedResults.Ok(new SimulationResponse(comparison.TotalPairs, comparison.Matches, comparison.Differences, comparison.MatchRate, comparison.DifferenceByDecision));
    }
}

public sealed record RulesetSummary(string Version, string Name, bool IsActive, bool IsShadow, DateTimeOffset CreatedAt, DateTimeOffset? ActivatedAt);
public sealed record SimulationResponse(int TotalPairs, int Matches, int Differences, double MatchRate, IReadOnlyDictionary<string, int> DifferenceByDecision);
