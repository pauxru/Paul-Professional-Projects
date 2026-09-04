using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using Contoso.Payments.Api.Middleware;
using Contoso.Payments.Application.Catalog;
using Contoso.Payments.Application.Common;

namespace Contoso.Payments.Api.Endpoints;

public static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/products").WithTags("Products");

        group.MapGet("/", async (int? page, int? pageSize, CatalogService svc, CancellationToken ct) =>
        {
            var result = await svc.ListAsync(page ?? 1, pageSize ?? 20, ct);
            return Results.Ok(result);
        }).WithName("ListProducts");

        group.MapPost("/", async (CreateProductRequest req, CatalogService svc, HttpContext ctx, CancellationToken ct) =>
        {
            var result = await svc.CreateAsync(req, ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1/products/{result.Value!.Id}", result.Value)
                : ToProblem(ctx, result);
        }).RequireAuthorization("admin").WithName("CreateProduct");
    }

    internal static IResult ToProblem<T>(HttpContext ctx, AppResult<T> r) => Results.Problem(new ProblemDetails
    {
        Type = $"https://contoso-payments.local/errors/{r.Code}",
        Title = r.Code ?? "error",
        Status = r.HttpStatus ?? 500,
        Detail = r.Message,
        Extensions = { ["traceId"] = ctx.CorrelationId() }
    });
}
