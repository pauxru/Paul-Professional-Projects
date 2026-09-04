using ReconEngine.Api.Auth;
using ReconEngine.Api.Contracts;
using ReconEngine.Api.Observability;
using ReconEngine.Application.Abstractions;
using ReconEngine.Application.Common;
using ReconEngine.Application.Reconciliation;
using ReconEngine.Application.Reporting;
using System.Security.Claims;

namespace ReconEngine.Api.Endpoints;

public static class RunsEndpoints
{
    public static IEndpointRouteBuilder MapRunEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/runs").WithTags("Runs").RequireAuthorization();

        group.MapPost("/", async (
            StartRunRequest request, ClaimsPrincipal user,
            ReconciliationOrchestrator orchestrator, ReconMetrics metrics, CancellationToken ct) =>
        {
            var run = await orchestrator.RunAsync(request.RuleSetId, request.From, request.To, user.CurrentUser(), ct);

            metrics.RecordRun(
                run.InternalRecordCount + run.ExternalRecordCount,
                run.MatchedInternalCount + run.MatchedExternalCount,
                run.DurationMs);
            metrics.SetOpenExceptions(run.ExceptionCount);

            return Results.Created($"/api/v1/runs/{run.Id}", run.ToResponse());
        })
        .RequireAuthorization(ReconScopes.Run)
        .WithName("StartRun")
        .WithSummary("Start a reconciliation run over the current working set (optionally a date window).");

        group.MapGet("/{id:guid}", async (Guid id, IRunStore store, CancellationToken ct) =>
        {
            var run = await store.GetAsync(id, ct);
            return run is null ? Results.NotFound() : Results.Ok(run.ToResponse());
        })
        .WithName("GetRun");

        group.MapGet("/", async (int? page, int? pageSize, IRunStore store, CancellationToken ct) =>
        {
            var (p, size) = EndpointHelpers.NormalizePaging(page, pageSize);
            var runs = await store.ListAsync(p, size, ct);
            var mapped = new PagedResult<RunResponse>(
                runs.Items.Select(r => r.ToResponse()).ToList(), runs.Page, runs.PageSize, runs.TotalCount);
            return Results.Ok(mapped);
        })
        .WithName("ListRuns");

        group.MapGet("/{id:guid}/report", async (Guid id, ReportService reports, CancellationToken ct) =>
        {
            var summary = await reports.RunSummaryAsync(id, ct);
            return Results.Ok(summary);
        })
        .WithName("GetRunReport")
        .WithSummary("The full run summary: counts, per-currency totals, exception breakdown and balance.");

        return app;
    }
}
