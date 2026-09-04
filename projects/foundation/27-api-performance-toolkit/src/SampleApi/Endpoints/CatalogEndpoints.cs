using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;
using SampleApi.Pathologies;

namespace SampleApi.Endpoints;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalog(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/catalog").WithTags("catalog");

        group.MapGet("/products", async (CatalogDbContext db, PathologyState pathology, HttpContext ctx, string? category, int page = 1, int pageSize = 25) =>
        {
            pathology.OverlayHeaders(ctx.Request);
            var query = db.Products.AsQueryable();
            if (!string.IsNullOrWhiteSpace(category))
            {
                if (pathology.MissingIndex && !pathology.Optimised)
                    query = query.Where(p => EF.Functions.Like(p.Category.ToLower(), "%" + category.ToLower() + "%"));
                else
                    query = query.Where(p => p.Category == category);
            }
            var total = await query.CountAsync();
            var items = await query
                .OrderBy(p => p.Id)
                .Skip((page - 1) * pageSize)
                .Take(Math.Min(pageSize, 100))
                .ToListAsync();
            return Results.Ok(new { items, page, pageSize, totalCount = total });
        });

        group.MapGet("/products/{sku}", async (CatalogDbContext db, PathologyState pathology, HttpContext ctx, string sku) =>
        {
            pathology.OverlayHeaders(ctx.Request);
            if (pathology.DownstreamLatencyMs > 0 && !pathology.Optimised)
                await Task.Delay(pathology.DownstreamLatencyMs, ctx.RequestAborted);
            Product? product;
            if (pathology.MissingIndex && !pathology.Optimised)
            {
                // Force a table scan by using function-in-predicate — SQLite cannot use the index here.
                product = await db.Products.FirstOrDefaultAsync(p => p.Sku.ToLower() == sku.ToLower());
            }
            else
            {
                product = await db.Products.FirstOrDefaultAsync(p => p.Sku == sku);
            }
            return product is null ? Results.NotFound() : Results.Ok(product);
        });

        return app;
    }
}
