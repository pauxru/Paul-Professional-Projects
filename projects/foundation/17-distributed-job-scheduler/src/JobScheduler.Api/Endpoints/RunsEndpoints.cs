using JobScheduler.Api.Auth;
using JobScheduler.Api.Contracts;
using JobScheduler.Application.Abstractions;
using JobScheduler.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace JobScheduler.Api.Endpoints;

public static class RunsEndpoints
{
    public static void MapRunsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/runs").WithTags("Runs");

        group.MapGet("/", ListAsync).RequireAuthorization(AuthConstants.PolicyRead);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AuthConstants.PolicyRead);
        group.MapGet("/{id:guid}/logs", LogsAsync).RequireAuthorization(AuthConstants.PolicyRead);
        group.MapPost("/{id:guid}/cancel", CancelAsync).RequireAuthorization(AuthConstants.PolicyTrigger);
    }

    private static async Task<Ok<PagedResponse<JobRunDto>>> ListAsync(
        IJobRunStore store, Guid? jobDefinitionId, string? state, string? queue, string? correlationId, string? search,
        int? page, int? pageSize, CancellationToken ct)
    {
        RunState? parsedState = Enum.TryParse<RunState>(state, ignoreCase: true, out var s) ? s : null;
        var query = new RunQuery(jobDefinitionId, parsedState, queue, correlationId, search);
        var result = await store.ListAsync(query, PageRequest.Of(page, pageSize), ct);
        return TypedResults.Ok(result.ToResponse(r => r.ToDto()));
    }

    private static async Task<Results<Ok<JobRunDto>, NotFound>> GetAsync(Guid id, IJobRunStore store, CancellationToken ct)
    {
        var run = await store.GetAsync(id, ct);
        return run is null ? TypedResults.NotFound() : TypedResults.Ok(run.ToDto());
    }

    private static async Task<Results<Ok<IReadOnlyList<RunLogDto>>, NotFound>> LogsAsync(
        Guid id, IJobRunStore runs, IRunLogStore logs, CancellationToken ct)
    {
        if (await runs.GetAsync(id, ct) is null)
        {
            return TypedResults.NotFound();
        }
        var lines = await logs.ForRunAsync(id, ct);
        return TypedResults.Ok<IReadOnlyList<RunLogDto>>(lines.Select(l => l.ToDto()).ToList());
    }

    private static async Task<Results<Ok<JobRunDto>, NotFound>> CancelAsync(
        Guid id, IJobRunStore store, IClock clock, CancellationToken ct)
    {
        var cancelled = await store.RequestCancelAsync(id, clock.UtcNow, ct);
        if (!cancelled)
        {
            return TypedResults.NotFound();
        }
        var run = await store.GetAsync(id, ct);
        return run is null ? TypedResults.NotFound() : TypedResults.Ok(run.ToDto());
    }
}
