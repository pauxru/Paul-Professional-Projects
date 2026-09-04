using JobScheduler.Application.Abstractions;
using JobScheduler.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace JobScheduler.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => TypedResults.Ok(new { status = "live" })).WithTags("Health").AllowAnonymous();
        app.MapGet("/health/ready", ReadyAsync).WithTags("Health").AllowAnonymous();
    }

    private static async Task<Results<Ok<object>, ProblemHttpResult>> ReadyAsync(
        IJobRunStore runs, ILeaderElectionStore election, IClock clock, CancellationToken ct)
    {
        try
        {
            var now = clock.UtcNow;
            int pending = await runs.CountByStateAsync(RunState.Pending, ct);
            int running = await runs.CountByStateAsync(RunState.Running, ct);
            var leader = await election.GetAsync(now, ct);
            object body = new { status = "ready", pending, running, leader = leader.Owner, leaderHeld = leader.IsHeld };
            return TypedResults.Ok(body);
        }
        catch (Exception ex)
        {
            return TypedResults.Problem($"Not ready: {ex.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
