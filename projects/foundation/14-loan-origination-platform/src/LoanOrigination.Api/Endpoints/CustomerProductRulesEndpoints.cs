using LoanOrigination.Api.Configuration;
using LoanOrigination.Application.Contracts;
using LoanOrigination.Application.Services;
using LoanOrigination.Domain.Rules;

namespace LoanOrigination.Api.Endpoints;

public static class CustomerProductRulesEndpoints
{
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/customers")
            .RequireAuthorization(ScopePolicies.Apply)
            .RequireRateLimiting("api")
            .WithTags("Customers");

        group.MapPost("/", async (
            CreateCustomerRequest request,
            CustomerService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.LegalName))
            {
                return ApiValidation.Invalid("legalName", "Legal name is required.");
            }

            var customer = await service.CreateAsync(
                request,
                RequestMetadata.Actor(context),
                RequestMetadata.CorrelationId(context),
                RequestMetadata.SourceIp(context),
                RequestMetadata.UserAgent(context),
                cancellationToken);
            return Results.Created($"/api/v1/customers/{customer.Id}", customer);
        });

        group.MapGet("/", async (int page, int pageSize, CustomerService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(page, pageSize, cancellationToken)));
        return app;
    }

    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/products")
            .RequireRateLimiting("api")
            .WithTags("Products");

        group.MapPost("/", async (
            CreateProductRequest request,
            ProductService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (request.MinimumPrincipal <= 0m)
            {
                return ApiValidation.Invalid("minimumPrincipal", "Minimum principal must be greater than zero.");
            }

            var product = await service.CreateVersionAsync(
                request,
                RequestMetadata.Actor(context),
                RequestMetadata.CorrelationId(context),
                RequestMetadata.SourceIp(context),
                RequestMetadata.UserAgent(context),
                cancellationToken);
            return Results.Created($"/api/v1/products/{product.ProductCode}/versions/{product.Version}", product);
        }).RequireAuthorization(ScopePolicies.Admin);

        group.MapGet("/", async (int page, int pageSize, ProductService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(page, pageSize, cancellationToken)))
            .RequireAuthorization(ScopePolicies.Apply);
        return app;
    }

    public static IEndpointRouteBuilder MapRulesetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/rulesets")
            .RequireAuthorization(ScopePolicies.Admin)
            .RequireRateLimiting("api")
            .WithTags("Versioned rulesets");

        group.MapPost("/", async (
            RuleSetDefinition request,
            RulesetService service,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var ruleset = await service.CreateAsync(
                request,
                RequestMetadata.Actor(context),
                RequestMetadata.CorrelationId(context),
                RequestMetadata.SourceIp(context),
                RequestMetadata.UserAgent(context),
                cancellationToken);
            return Results.Created($"/api/v1/rulesets/{ruleset.Id}/versions/{ruleset.Version}", ruleset);
        });

        group.MapGet("/", async (int page, int pageSize, RulesetService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(page, pageSize, cancellationToken)));

        group.MapPost("/simulate", async (WhatIfRequest request, RulesetService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.SimulateAsync(request, cancellationToken)));
        group.MapPost("/what-if", async (WhatIfRequest request, RulesetService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.SimulateAsync(request, cancellationToken)));
        return app;
    }
}
