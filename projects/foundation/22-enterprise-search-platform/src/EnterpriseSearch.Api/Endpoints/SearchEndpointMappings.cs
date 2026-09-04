using EnterpriseSearch.Api.Contracts;
using EnterpriseSearch.Application.Search;
using EnterpriseSearch.Domain.Search;

namespace EnterpriseSearch.Api.Endpoints;

public static class SearchEndpointMappings
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var search = app.MapGroup("/api/v1").RequireAuthorization("SearchRead");
        search.MapPost("/search", (SearchRequestDto request, SearchEngine engine, QueryLimits limits, HttpContext context, CancellationToken cancellationToken) => Execute(request, engine, limits, context, cancellationToken));
        search.MapGet("/search", (string? q, string index, RetrievalMode mode, int from, int size, string? searchAfter, bool explain, SearchEngine engine, QueryLimits limits, HttpContext context, CancellationToken cancellationToken) =>
            Execute(new SearchRequestDto(index, q, mode, From: from, Size: size, SearchAfter: searchAfter, Explain: explain), engine, limits, context, cancellationToken));
        search.MapGet("/suggest", (string q, string index, int size, SearchCluster cluster, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(q) || size is < 1 or > 20) return ApiProblems.Validation(context, "q is required and size must be between 1 and 20.");
            try
            {
                var engine = cluster.GetIndex(index).Index;
                return Results.Ok(new { suggestions = engine.Suggest(q, size), didYouMean = engine.DidYouMean(q) });
            }
            catch (KeyNotFoundException exception) { return ApiProblems.NotFound(context, exception.Message); }
        });
        search.MapPost("/analyze", (AnalyzeRequest request, SearchCluster cluster, ITextAnalyzer analyzer, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text)) return ApiProblems.Validation(context, "text is required.");
            try
            {
                AnalyzerDefinition definition;
                if (!string.IsNullOrWhiteSpace(request.Index))
                {
                    var index = cluster.GetIndex(request.Index).Index;
                    definition = !string.IsNullOrWhiteSpace(request.Analyzer) && index.Definition.Analyzers is not null && index.Definition.Analyzers.TryGetValue(request.Analyzer, out var configured)
                        ? configured
                        : index.Definition.GetAnalyzer(request.Field ?? "body");
                }
                else
                {
                    definition = request.Analyzer?.ToLowerInvariant() switch
                    {
                        "whitespace" => BuiltInAnalyzers.Whitespace,
                        "ngram" => new AnalyzerDefinition("ngram", TokenizerKind.NGram, RemoveStopWords: false, Stem: false),
                        "edge" or "edge_ngram" => BuiltInAnalyzers.Edge,
                        null or "standard" => BuiltInAnalyzers.Standard,
                        _ => throw new QueryValidationException($"Unknown analyzer '{request.Analyzer}'.")
                    };
                }
                return Results.Ok(new { analyzer = definition.Name, tokens = analyzer.Analyze(request.Text, definition) });
            }
            catch (QueryValidationException exception) { return ApiProblems.Validation(context, exception.Message); }
            catch (KeyNotFoundException exception) { return ApiProblems.NotFound(context, exception.Message); }
        });
        return app;
    }

    private static IResult Execute(SearchRequestDto request, SearchEngine engine, QueryLimits limits, HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(limits.QueryTimeoutMilliseconds));
            return Results.Ok(engine.Search(request.ToDomain(context.User), timeout.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(statusCode: StatusCodes.Status408RequestTimeout, title: "Search timed out", detail: "The query exceeded the configured execution limit.");
        }
        catch (QueryValidationException exception) { return ApiProblems.Validation(context, exception.Message); }
        catch (KeyNotFoundException exception) { return ApiProblems.NotFound(context, exception.Message); }
    }
}
