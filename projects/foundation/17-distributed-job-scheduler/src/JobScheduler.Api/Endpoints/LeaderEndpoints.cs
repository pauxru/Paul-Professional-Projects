using JobScheduler.Api.Auth;
using JobScheduler.Api.Contracts;
using JobScheduler.Application.Abstractions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace JobScheduler.Api.Endpoints;

public static class LeaderEndpoints
{
    public static void MapLeaderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/leader", GetAsync).WithTags("Leader").RequireAuthorization(AuthConstants.PolicyRead);
    }

    private static async Task<Ok<LeaderDto>> GetAsync(ILeaderElectionStore election, IClock clock, CancellationToken ct)
    {
        var view = await election.GetAsync(clock.UtcNow, ct);
        return TypedResults.Ok(view.ToDto());
    }
}
