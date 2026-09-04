using EnterpriseSearch.Api.Contracts;
using EnterpriseSearch.Application.Search;
using EnterpriseSearch.Domain.Search;

namespace EnterpriseSearch.Api.Endpoints;

public static class IndexEndpointMappings
{
    public static IEndpointRouteBuilder MapIndexEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/indices").RequireAuthorization("SearchManage");
        group.MapGet("", (SearchCluster cluster) => Results.Ok(new { indices = cluster.ListStats(), aliases = cluster.GetAliases() }));
        group.MapPost("", async (CreateIndexRequest request, SearchIndexService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 160 || request.RefreshIntervalMilliseconds is < 50 or > 60_000) return ApiProblems.Validation(context, "A valid index name and refresh interval are required.");
            try
            {
                await service.CreateAsync(request.ToDefinition(), request.Alias, cancellationToken);
                return Results.Created($"/api/v1/indices/{request.Name}", new { name = request.Name, alias = request.Alias });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Index conflict", detail: exception.Message);
            }
        });
        group.MapPost("/aliases/swap", async (AliasSwapRequest request, SearchIndexService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Alias) || string.IsNullOrWhiteSpace(request.ExpectedCurrent) || string.IsNullOrWhiteSpace(request.Next)) return ApiProblems.Validation(context, "alias, expectedCurrent, and next are required.");
            try
            {
                await service.SwapAliasAsync(request.Alias, request.ExpectedCurrent, request.Next, cancellationToken);
                return Results.Ok(new { request.Alias, target = request.Next });
            }
            catch (KeyNotFoundException exception) { return ApiProblems.NotFound(context, exception.Message); }
            catch (InvalidOperationException exception) { return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Alias conflict", detail: exception.Message); }
        });
        group.MapPost("/{name}/alias", async (string name, AliasRequest request, SearchCluster cluster, IIndexStateStore store, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Alias)) return ApiProblems.Validation(context, "alias is required.");
            try
            {
                cluster.SetAlias(request.Alias, name);
                await store.SaveAliasAsync(request.Alias, name, cancellationToken);
                return Results.Ok(new { request.Alias, target = name });
            }
            catch (KeyNotFoundException exception) { return ApiProblems.NotFound(context, exception.Message); }
        });
        group.MapGet("/{name}/stats", (string name, SearchCluster cluster, HttpContext context) =>
        {
            try { return Results.Ok(cluster.GetIndex(name).Index.GetStats()); }
            catch (KeyNotFoundException exception) { return ApiProblems.NotFound(context, exception.Message); }
        });
        group.MapPost("/{name}/refresh", async (string name, SearchIndexService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(new { refreshed = await service.RefreshAsync(name, cancellationToken) }); }
            catch (KeyNotFoundException exception) { return ApiProblems.NotFound(context, exception.Message); }
        });
        group.MapPost("/{name}/compact", async (string name, SearchIndexService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            try { await service.CompactAsync(name, cancellationToken); return Results.Accepted($"/api/v1/indices/{name}/stats"); }
            catch (KeyNotFoundException exception) { return ApiProblems.NotFound(context, exception.Message); }
        });
        group.MapDelete("/{name}", async (string name, SearchIndexService service, HttpContext context, CancellationToken cancellationToken) =>
        {
            try { await service.DropAsync(name, cancellationToken); return Results.NoContent(); }
            catch (InvalidOperationException exception) { return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Index in use", detail: exception.Message); }
        });
        group.MapPost("/{name}/documents", (string name, DocumentRequest request, SearchIndexService service, HttpContext context) =>
        {
            var error = request.Validate();
            if (error is not null) return ApiProblems.Validation(context, error);
            try { service.EnqueueDocument(request.ToDomain(name)); return Results.Accepted($"/api/v1/indices/{name}/documents/{request.Id}", new { queued = 1 }); }
            catch (KeyNotFoundException exception) { return ApiProblems.NotFound(context, exception.Message); }
            catch (IndexingBackpressureException exception) { return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Indexing queue saturated", detail: exception.Message); }
        });
        group.MapPost("/{name}/documents/bulk", (string name, DocumentRequest[] requests, SearchIndexService service, HttpContext context) =>
        {
            if (requests is null || requests.Length == 0 || requests.Length > 5_000) return ApiProblems.Validation(context, "bulk documents must contain 1 to 5,000 items.");
            try
            {
                foreach (var request in requests)
                {
                    var error = request.Validate();
                    if (error is not null) return ApiProblems.Validation(context, error);
                    service.EnqueueDocument(request.ToDomain(name));
                }
                return Results.Accepted($"/api/v1/indices/{name}/refresh", new { queued = requests.Length });
            }
            catch (KeyNotFoundException exception) { return ApiProblems.NotFound(context, exception.Message); }
            catch (IndexingBackpressureException exception) { return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Indexing queue saturated", detail: exception.Message); }
        });
        group.MapPatch("/{name}/documents/{id}", (string name, string id, PatchRequest request, SearchIndexService service, HttpContext context) =>
        {
            try { service.EnqueuePatch(name, id, request.ToDomain()); return Results.Accepted($"/api/v1/indices/{name}/documents/{id}", new { queued = 1 }); }
            catch (KeyNotFoundException exception) { return ApiProblems.NotFound(context, exception.Message); }
            catch (IndexingBackpressureException exception) { return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Indexing queue saturated", detail: exception.Message); }
        });
        group.MapDelete("/{name}/documents/{id}", (string name, string id, SearchIndexService service, HttpContext context) =>
        {
            try { service.EnqueueDelete(name, id); return Results.Accepted($"/api/v1/indices/{name}/refresh", new { queued = 1 }); }
            catch (KeyNotFoundException exception) { return ApiProblems.NotFound(context, exception.Message); }
            catch (IndexingBackpressureException exception) { return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Indexing queue saturated", detail: exception.Message); }
        });
        group.MapDelete("/{name}/documents", (string name, string query, SearchIndexService service, SearchEngine engine, QueryStringParser parser, HttpContext context) =>
        {
            try
            {
                var ids = engine.FindDocumentIds(name, parser.Parse(query));
                foreach (var id in ids) service.EnqueueDelete(name, id);
                return Results.Accepted($"/api/v1/indices/{name}/refresh", new { queued = ids.Count });
            }
            catch (QueryValidationException exception) { return ApiProblems.Validation(context, exception.Message); }
            catch (KeyNotFoundException exception) { return ApiProblems.NotFound(context, exception.Message); }
            catch (IndexingBackpressureException exception) { return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Indexing queue saturated", detail: exception.Message); }
        });
        return app;
    }
}
