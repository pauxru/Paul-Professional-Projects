using EnterpriseSearch.Api.Contracts;
using EnterpriseSearch.Application.Search;
using EnterpriseSearch.Application.Security;
using EnterpriseSearch.Domain.Search;

namespace EnterpriseSearch.Api.Endpoints;

public static class AnalyticsEndpointMappings
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/events/click", (ClickRequest request, SearchEngine engine, IClock clock, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Index) || string.IsNullOrWhiteSpace(request.Query) || string.IsNullOrWhiteSpace(request.DocumentId) || request.Position is < 1 or > 100) return ApiProblems.Validation(context, "index, query, documentId, and position (1-100) are required.");
            engine.LogClick(new ClickEvent(request.Index, request.Query, request.DocumentId, request.Position, clock.UtcNow));
            return Results.Accepted();
        }).RequireAuthorization("SearchRead");

        var analytics = app.MapGroup("/api/v1/analytics").RequireAuthorization("SearchManage");
        analytics.MapGet("/summary", (SearchEngine engine) => Results.Ok(engine.GetAnalytics()));
        analytics.MapGet("/ctr", (SearchEngine engine) => Results.Ok(engine.GetAnalytics().CtrByPosition));
        analytics.MapGet("/top-queries", (SearchEngine engine) => Results.Ok(engine.GetAnalytics().TopQueries));
        app.MapPost("/api/v1/eval/run", (string? index, RelevanceEvaluationHarness harness, HttpContext context) =>
        {
            try { return Results.Ok(harness.Run(index ?? "catalogue", GoldenEvaluationSet.Create())); }
            catch (KeyNotFoundException exception) { return ApiProblems.NotFound(context, exception.Message); }
        }).RequireAuthorization("SearchManage");
        return app;
    }

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/auth/token", (TokenRequest request, ITokenIssuer issuer, IWebHostEnvironment environment, HttpContext context) =>
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing")) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Subject)) return ApiProblems.Validation(context, "subject is required.");
            var scopes = request.Scopes is { Length: > 0 } ? request.Scopes : ["search.read"];
            var token = issuer.Issue(new TokenIssueRequest(request.Subject, scopes, request.Groups ?? Array.Empty<string>()));
            return Results.Ok(new { accessToken = token, tokenType = "Bearer", expiresIn = 3600 });
        }).AllowAnonymous();
        return app;
    }
}
