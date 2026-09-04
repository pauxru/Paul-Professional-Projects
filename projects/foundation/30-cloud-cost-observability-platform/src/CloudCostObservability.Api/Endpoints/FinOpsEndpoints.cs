using System.Security.Claims;
using CloudCostObservability.Api.Auth;
using CloudCostObservability.Application.Contracts;
using CloudCostObservability.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace CloudCostObservability.Api.Endpoints;

public static class FinOpsEndpoints
{
    public static WebApplication MapFinOpsEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/auth/token", (DevTokenRequest request, ITokenIssuer issuer, IHostEnvironment environment) =>
        {
            if (environment.IsProduction()) return Results.NotFound();
            return string.IsNullOrWhiteSpace(request.Subject)
                ? Validation("subject", "A demo token subject is required.")
                : Results.Ok(issuer.Issue(request));
        }).AllowAnonymous().WithTags("Authentication");

        var resources = app.MapGroup("/api/v1/resources").RequireAuthorization("finops:read").WithTags("Resources");
        resources.MapGet("", async ([AsParameters] ResourceQuery query, HttpContext context, IFinOpsService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetResourcesAsync(query, TeamScope(context.User), cancellationToken)));

        var costs = app.MapGroup("/api/v1/costs").RequireAuthorization("finops:read").WithTags("Costs");
        costs.MapGet("", async ([AsParameters] CostQuery query, HttpContext context, IFinOpsService service, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await service.GetCostsAsync(query, TeamScope(context.User), cancellationToken)); }
            catch (ArgumentException exception) { return DomainProblem(exception); }
        });

        var allocations = app.MapGroup("/api/v1/allocations").RequireAuthorization("finops:read").WithTags("Allocation");
        allocations.MapGet("", async ([AsParameters] AllocationQuery query, HttpContext context, IFinOpsService service, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await service.GetAllocationsAsync(query, TeamScope(context.User), cancellationToken)); }
            catch (ArgumentException exception) { return DomainProblem(exception); }
        });
        allocations.MapGet("/rules", async (IFinOpsService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAllocationRulesAsync(cancellationToken)));
        allocations.MapGet("/showback", async (DateOnly? from, DateOnly? to, HttpContext context, IFinOpsService service, CancellationToken cancellationToken) =>
            Results.Ok(new { reportType = "showback", report = await service.GetAllocationsAsync(new AllocationQuery(from, to), TeamScope(context.User), cancellationToken) }));
        allocations.MapGet("/chargeback", async (DateOnly? from, DateOnly? to, HttpContext context, IFinOpsService service, CancellationToken cancellationToken) =>
            Results.Ok(new { reportType = "chargeback", report = await service.GetAllocationsAsync(new AllocationQuery(from, to), TeamScope(context.User), cancellationToken) }));
        allocations.MapGet("/audit/{costRecordId}", async (string costRecordId, DateOnly? from, DateOnly? to, HttpContext context, IFinOpsService service, CancellationToken cancellationToken) =>
        {
            var report = await service.GetAllocationsAsync(new AllocationQuery(from, to, null, "amortized", 1, 10_000), TeamScope(context.User), cancellationToken);
            var lines = report.Lines.Items.Where(line => string.Equals(line.CostRecordId, costRecordId, StringComparison.Ordinal)).ToList();
            return lines.Count == 0 ? Results.NotFound() : Results.Ok(new { costRecordId, lines, explanation = "Each line records the ordered rule and attribution explanation for the source charge." });
        });
        allocations.MapPut("/rules", async (List<AllocationRuleRequest> request, IFinOpsService service, CancellationToken cancellationToken) =>
        {
            if (request.Count == 0) return Validation("rules", "At least one ordered allocation rule is required.");
            try { return Results.Ok(await service.ReplaceAllocationRulesAsync(request, cancellationToken)); }
            catch (ArgumentException exception) { return DomainProblem(exception); }
        }).RequireAuthorization("finops:manage");

        var budgets = app.MapGroup("/api/v1/budgets").RequireAuthorization("finops:read").WithTags("Budgets");
        budgets.MapGet("", async (IFinOpsService service, CancellationToken cancellationToken) => Results.Ok(await service.GetBudgetsAsync(cancellationToken)));
        budgets.MapGet("/status", async (IFinOpsService service, CancellationToken cancellationToken) => Results.Ok(await service.GetBudgetStatusesAsync(cancellationToken)));
        budgets.MapPost("", async (CreateBudgetRequest request, IFinOpsService service, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Selector) || request.Amount <= 0m || request.PeriodEnd < request.PeriodStart)
                return Validation("budget", "id, selector, a positive amount and an ordered period are required.");
            try
            {
                var budget = await service.CreateBudgetAsync(request, cancellationToken);
                return Results.Created($"/api/v1/budgets/{budget.Id}", budget);
            }
            catch (InvalidOperationException exception) { return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Budget conflict", detail: exception.Message); }
        }).RequireAuthorization("finops:manage");

        app.MapGet("/api/v1/forecasts", async (string? team, HttpContext context, IFinOpsService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetForecastsAsync(TeamScope(context.User) ?? team, cancellationToken)))
            .RequireAuthorization("finops:read").WithTags("Forecasts");

        var anomalies = app.MapGroup("/api/v1/anomalies").RequireAuthorization("finops:read").WithTags("Anomalies");
        anomalies.MapGet("", async (bool includeSuppressed, IFinOpsService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAnomaliesAsync(includeSuppressed, cancellationToken)));
        anomalies.MapGet("/groups", async (IFinOpsService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAnomalyGroupsAsync(cancellationToken)));
        anomalies.MapPost("/{id}/actions", async (string id, AnomalyActionRequest request, IFinOpsService service, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Action)) return Validation("action", "An action is required.");
            try
            {
                var anomaly = await service.UpdateAnomalyAsync(id, request, cancellationToken);
                return anomaly is null ? Results.NotFound() : Results.Ok(anomaly);
            }
            catch (ArgumentException exception) { return DomainProblem(exception); }
        }).RequireAuthorization("finops:manage");

        var recommendations = app.MapGroup("/api/v1/recommendations").RequireAuthorization("finops:read").WithTags("Recommendations");
        recommendations.MapGet("", async (IFinOpsService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetRecommendationsAsync(cancellationToken)));
        recommendations.MapPost("/{id}/actions", async (string id, RecommendationActionRequest request, IFinOpsService service, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Action)) return Validation("action", "An action is required.");
            try
            {
                var recommendation = await service.UpdateRecommendationAsync(id, request, cancellationToken);
                return recommendation is null ? Results.NotFound() : Results.Ok(recommendation);
            }
            catch (ArgumentException exception) { return DomainProblem(exception); }
            catch (InvalidOperationException exception) { return DomainProblem(exception); }
        }).RequireAuthorization("finops:manage");

        app.MapGet("/api/v1/tags/coverage", async (HttpContext context, IFinOpsService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetTagCoverageAsync(TeamScope(context.User), cancellationToken)))
            .RequireAuthorization("finops:read").WithTags("Tag governance");

        app.MapGet("/api/v1/unit-economics", async (HttpContext context, IFinOpsService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetUnitEconomicsAsync(TeamScope(context.User), cancellationToken)))
            .RequireAuthorization("finops:read").WithTags("Unit economics");

        var imports = app.MapGroup("/api/v1/imports").RequireAuthorization("finops:manage").WithTags("Imports");
        imports.MapGet("", async (IFinOpsService service, CancellationToken cancellationToken) => Results.Ok(await service.GetImportsAsync(cancellationToken)));
        imports.MapPost("", async (FileImportRequest request, IFinOpsService service, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.Path)) return Validation("import", "provider and path are required.");
            try { return Results.Ok(await service.ImportFileAsync(request, cancellationToken)); }
            catch (ArgumentException exception) { return DomainProblem(exception); }
            catch (IOException exception) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Import file unavailable", detail: exception.Message); }
        });

        return app;
    }

    private static string? TeamScope(ClaimsPrincipal principal)
    {
        if (HasScope(principal, "finops:admin")) return null;
        return principal.FindFirstValue("team");
    }

    private static bool HasScope(ClaimsPrincipal principal, string scope) =>
        principal.FindAll("scope").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(value => string.Equals(value, scope, StringComparison.OrdinalIgnoreCase));

    private static IResult Validation(string key, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [key] = [message] });

    private static IResult DomainProblem(Exception exception) =>
        Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Domain rule rejected", detail: exception.Message);
}
