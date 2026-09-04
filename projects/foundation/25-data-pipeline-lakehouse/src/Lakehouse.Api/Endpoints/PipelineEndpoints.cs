using System.Globalization;
using Lakehouse.Api.Auth;
using Lakehouse.Application.Orchestration;
using Lakehouse.Application.Serving;

namespace Lakehouse.Api.Endpoints;

/// <summary>
/// Pipeline control plane: trigger an incremental run, backfill a date range, partially re-run a task and
/// its downstream, and inspect the DAG and run history. Mutating operations require the operator role;
/// reads require any authenticated user.
/// </summary>
public static class PipelineEndpoints
{
    private static object Project(RunRecord r) => new
    {
        r.RunId,
        r.Window,
        r.Success,
        r.StartedAt,
        r.FinishedAt,
        r.DurationMs,
        r.TotalRowsOut,
        r.Failed,
        r.Blocked,
        Tasks = r.Tasks.Select(t => new
        {
            t.TaskId, State = t.State.ToString(), t.RowsIn, t.RowsOut, t.Quarantined,
            t.Attempts, t.DurationMs, t.Error, t.Note
        })
    };

    public static IEndpointRouteBuilder MapPipelineEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/pipeline").WithTags("Pipeline");

        group.MapGet("/dag", (LakehousePipeline pipeline) =>
        {
            var dag = pipeline.Build();
            var order = dag.TopologicalOrder();
            return Results.Ok(new
            {
                order,
                tasks = order.Select(id => new { id, dependsOn = dag.Tasks[id].DependsOn, maxRetries = dag.Tasks[id].MaxRetries })
            });
        }).RequireAuthorization(AuthPolicies.Reader);

        group.MapGet("/runs", (IRunHistoryStore history, int? limit) =>
            Results.Ok(history.Recent(limit is > 0 and <= 200 ? limit.Value : 20).Select(Project)))
            .RequireAuthorization(AuthPolicies.Reader);

        group.MapPost("/run", (LakehousePipeline pipeline, DagRunner runner, ISqlQueryEngine serving, string? window) =>
            RunAndRebuild(() => runner.Run(pipeline.Build(), new RunContext(NewRunId(window), string.IsNullOrWhiteSpace(window) ? "full" : window!)), serving))
            .RequireAuthorization(AuthPolicies.Operator);

        group.MapPost("/rerun", (LakehousePipeline pipeline, DagRunner runner, ISqlQueryEngine serving, string task, string? window) =>
        {
            if (string.IsNullOrWhiteSpace(task))
                return Results.BadRequest(new { error = "Query parameter 'task' is required." });
            var w = string.IsNullOrWhiteSpace(window) ? "full" : window!;
            return RunAndRebuild(() => runner.Run(pipeline.Build(), new RunContext(NewRunId(w), w), new[] { task }), serving);
        }).RequireAuthorization(AuthPolicies.Operator);

        group.MapPost("/backfill", (LakehousePipeline pipeline, DagRunner runner, ISqlQueryEngine serving, string from, string to) =>
        {
            if (!TryDate(from, out var start) || !TryDate(to, out var end))
                return Results.BadRequest(new { error = "Provide 'from' and 'to' as yyyy-MM-dd." });
            if (end < start)
                return Results.BadRequest(new { error = "'to' must be on or after 'from'." });

            var windows = new List<string>();
            for (var d = start; d <= end; d = d.AddDays(1))
                windows.Add(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            try
            {
                var records = runner.Backfill(pipeline.Build(), windows, w => new RunContext($"backfill-{w}-{Guid.NewGuid():N}".Substring(0, 24), w));
                serving.Rebuild();
                return Results.Ok(records.Select(Project));
            }
            catch (OverlappingRunException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequireAuthorization(AuthPolicies.Operator);

        return app;
    }

    private static IResult RunAndRebuild(Func<RunRecord> run, ISqlQueryEngine serving)
    {
        try
        {
            var record = run();
            if (record.Success) serving.Rebuild();
            return Results.Ok(Project(record));
        }
        catch (OverlappingRunException ex) { return Results.Conflict(new { error = ex.Message }); }
    }

    private static string NewRunId(string? window)
        => $"run-{DateTime.UtcNow:yyyyMMddHHmmss}-{(string.IsNullOrWhiteSpace(window) ? "full" : window)}";

    private static bool TryDate(string s, out DateOnly date)
        => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
