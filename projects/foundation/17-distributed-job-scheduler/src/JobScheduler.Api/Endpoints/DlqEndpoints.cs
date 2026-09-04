using JobScheduler.Api.Auth;
using JobScheduler.Api.Contracts;
using JobScheduler.Application.Abstractions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace JobScheduler.Api.Endpoints;

public static class DlqEndpoints
{
    public static void MapDlqEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/dlq").WithTags("DeadLetter");

        group.MapGet("/", ListAsync).RequireAuthorization(AuthConstants.PolicyRead);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AuthConstants.PolicyRead);
        group.MapPost("/{id:guid}/replay", ReplayAsync).RequireAuthorization(AuthConstants.PolicyManage);
    }

    private static async Task<Ok<PagedResponse<DeadLetterDto>>> ListAsync(
        IDeadLetterStore store, int? page, int? pageSize, bool? includeReplayed, CancellationToken ct)
    {
        var result = await store.ListAsync(PageRequest.Of(page, pageSize), includeReplayed ?? false, ct);
        return TypedResults.Ok(result.ToResponse(e => e.ToDto()));
    }

    private static async Task<Results<Ok<DeadLetterDto>, NotFound>> GetAsync(Guid id, IDeadLetterStore store, CancellationToken ct)
    {
        var entry = await store.GetAsync(id, ct);
        return entry is null ? TypedResults.NotFound() : TypedResults.Ok(entry.ToDto());
    }

    private static async Task<Results<Ok<JobRunDto>, NotFound, ProblemHttpResult>> ReplayAsync(
        Guid id, IDeadLetterStore dlq, IJobRunStore runs, IClock clock, CancellationToken ct)
    {
        var entry = await dlq.GetAsync(id, ct);
        if (entry is null)
        {
            return TypedResults.NotFound();
        }
        if (entry.Replayed)
        {
            return TypedResults.Problem("This dead-letter entry has already been replayed.", statusCode: StatusCodes.Status409Conflict);
        }

        var now = clock.UtcNow;
        var run = await runs.ReplayResetAsync(entry.JobRunId, now, ct);
        if (run is null)
        {
            return TypedResults.Problem("The original run no longer exists and cannot be replayed.", statusCode: StatusCodes.Status410Gone);
        }

        entry.MarkReplayed(run.Id, now);
        await dlq.SaveChangesAsync(ct);
        return TypedResults.Ok(run.ToDto());
    }
}
