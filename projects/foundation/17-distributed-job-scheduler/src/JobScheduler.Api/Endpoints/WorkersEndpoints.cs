using JobScheduler.Api.Auth;
using JobScheduler.Api.Contracts;
using JobScheduler.Application.Abstractions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace JobScheduler.Api.Endpoints;

public static class WorkersEndpoints
{
    public static void MapWorkersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/workers").WithTags("Workers");

        group.MapGet("/", ListAsync).RequireAuthorization(AuthConstants.PolicyRead);
        group.MapGet("/{nodeId}", GetAsync).RequireAuthorization(AuthConstants.PolicyRead);
        group.MapPost("/{nodeId}/drain", DrainAsync).RequireAuthorization(AuthConstants.PolicyAdmin);
    }

    private static async Task<Ok<IReadOnlyList<WorkerNodeDto>>> ListAsync(IWorkerRegistry registry, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var nodes = await registry.ListAsync(ct);
        return TypedResults.Ok<IReadOnlyList<WorkerNodeDto>>(nodes.Select(n => n.ToDto(now)).ToList());
    }

    private static async Task<Results<Ok<WorkerNodeDto>, NotFound>> GetAsync(string nodeId, IWorkerRegistry registry, IClock clock, CancellationToken ct)
    {
        var node = await registry.GetAsync(nodeId, ct);
        return node is null ? TypedResults.NotFound() : TypedResults.Ok(node.ToDto(clock.UtcNow));
    }

    private static async Task<Results<Ok<WorkerNodeDto>, NotFound>> DrainAsync(string nodeId, IWorkerRegistry registry, IClock clock, CancellationToken ct)
    {
        var node = await registry.GetAsync(nodeId, ct);
        if (node is null)
        {
            return TypedResults.NotFound();
        }
        await registry.BeginDrainAsync(nodeId, clock.UtcNow, ct);
        var updated = await registry.GetAsync(nodeId, ct);
        return updated is null ? TypedResults.NotFound() : TypedResults.Ok(updated.ToDto(clock.UtcNow));
    }
}
