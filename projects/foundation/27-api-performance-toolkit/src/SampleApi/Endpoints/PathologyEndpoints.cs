using SampleApi.Pathologies;

namespace SampleApi.Endpoints;

public static class PathologyEndpoints
{
    public static IEndpointRouteBuilder MapPathology(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/pathology").WithTags("pathology");

        group.MapGet("/", (PathologyState state) => Results.Ok(state.Snapshot()));
        group.MapPost("/", (PathologyState state, PathologyPatch patch) =>
        {
            state.NPlusOneQueries = patch.NPlusOneQueries ?? state.NPlusOneQueries;
            state.MissingIndex = patch.MissingIndex ?? state.MissingIndex;
            state.DownstreamLatencyMs = patch.DownstreamLatencyMs ?? state.DownstreamLatencyMs;
            state.LockContention = patch.LockContention ?? state.LockContention;
            state.MemoryPressureLeak = patch.MemoryPressureLeak ?? state.MemoryPressureLeak;
            state.Optimised = patch.Optimised ?? state.Optimised;
            if (patch.MemoryPressureLeak == false) OrderEndpoints.ClearLeaks();
            return Results.Ok(state.Snapshot());
        });
        group.MapDelete("/", (PathologyState state) =>
        {
            state.NPlusOneQueries = false;
            state.MissingIndex = false;
            state.DownstreamLatencyMs = 0;
            state.LockContention = false;
            state.MemoryPressureLeak = false;
            state.Optimised = false;
            OrderEndpoints.ClearLeaks();
            return Results.Ok(state.Snapshot());
        });

        group.MapGet("/leak-bytes", () => Results.Ok(new { leakedBytes = OrderEndpoints.CurrentLeakedBytes() }));

        return app;
    }

    public static void OverlayHeaders(this PathologyState state, HttpRequest request)
    {
        // Non-mutating overlay: request-scoped copies of the state fields via per-request
        // properties would be tidier, but the load tests only ever run a single pathology
        // mix at a time so mutating the shared state is fine here.
        if (request.Headers.TryGetValue(PathologyHeaders.NPlusOne, out var np) && bool.TryParse(np, out var npV)) state.NPlusOneQueries = npV;
        if (request.Headers.TryGetValue(PathologyHeaders.MissingIndex, out var mi) && bool.TryParse(mi, out var miV)) state.MissingIndex = miV;
        if (request.Headers.TryGetValue(PathologyHeaders.DownstreamLatencyMs, out var lat) && int.TryParse(lat, out var latV)) state.DownstreamLatencyMs = latV;
        if (request.Headers.TryGetValue(PathologyHeaders.LockContention, out var lc) && bool.TryParse(lc, out var lcV)) state.LockContention = lcV;
        if (request.Headers.TryGetValue(PathologyHeaders.MemoryLeak, out var ml) && bool.TryParse(ml, out var mlV)) state.MemoryPressureLeak = mlV;
        if (request.Headers.TryGetValue(PathologyHeaders.Optimised, out var opt) && bool.TryParse(opt, out var optV)) state.Optimised = optV;
    }
}
