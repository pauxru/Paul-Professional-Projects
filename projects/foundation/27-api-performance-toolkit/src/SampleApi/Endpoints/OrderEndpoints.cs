using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Models;
using SampleApi.Pathologies;

namespace SampleApi.Endpoints;

public static class OrderEndpoints
{
    // Deliberately shared for LockContention mode.
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    private static readonly List<byte[]> LeakedBuffers = new();
    private static readonly object LeakLock = new();

    public static IEndpointRouteBuilder MapOrders(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/orders").WithTags("orders");

        group.MapPost("/", async (CatalogDbContext db, PathologyState pathology, HttpContext ctx, CreateOrderRequest req, CancellationToken ct) =>
        {
            pathology.OverlayHeaders(ctx.Request);
            if (req.Lines.Count == 0) return Results.BadRequest(new { error = "at least one line required" });

            if (pathology.MemoryPressureLeak && !pathology.Optimised)
            {
                lock (LeakLock)
                {
                    LeakedBuffers.Add(new byte[64 * 1024]);
                }
            }

            if (pathology.LockContention && !pathology.Optimised)
                await WriteGate.WaitAsync(ct);
            try
            {
                var productIds = req.Lines.Select(l => l.ProductId).Distinct().ToArray();
                var products = await db.Products
                    .Where(p => productIds.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id, ct);
                var order = new Order
                {
                    CustomerRef = req.CustomerRef,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Status = "Pending"
                };
                decimal total = 0;
                foreach (var line in req.Lines)
                {
                    if (!products.TryGetValue(line.ProductId, out var product))
                        return Results.BadRequest(new { error = $"unknown product {line.ProductId}" });
                    var subtotal = product.Price * line.Quantity;
                    total += subtotal;
                    order.Lines.Add(new OrderLine
                    {
                        ProductId = product.Id,
                        Quantity = line.Quantity,
                        UnitPrice = product.Price
                    });
                }
                order.Total = total;
                db.Orders.Add(order);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/v1/orders/{order.Id}", new { orderId = order.Id, total });
            }
            finally
            {
                if (pathology.LockContention && !pathology.Optimised) WriteGate.Release();
            }
        });

        group.MapGet("/", async (CatalogDbContext db, PathologyState pathology, HttpContext ctx, int page = 1, int pageSize = 25, CancellationToken ct = default) =>
        {
            pathology.OverlayHeaders(ctx.Request);
            if (pathology.DownstreamLatencyMs > 0 && !pathology.Optimised)
                await Task.Delay(pathology.DownstreamLatencyMs, ct);
            List<OrderSummary> summaries;
            if (pathology.NPlusOneQueries && !pathology.Optimised)
            {
                // Deliberately awful: fetch orders and then hit the DB per order for its lines.
                var orders = await db.Orders
                    .OrderByDescending(o => o.Id)
                    .Skip((page - 1) * pageSize)
                    .Take(Math.Min(pageSize, 100))
                    .ToListAsync(ct);
                summaries = new List<OrderSummary>(orders.Count);
                foreach (var o in orders)
                {
                    var lineCount = await db.OrderLines.CountAsync(l => l.OrderId == o.Id, ct);
                    summaries.Add(new OrderSummary(o.Id, o.CustomerRef, o.Total, o.Status, o.CreatedAt, lineCount));
                }
            }
            else
            {
                summaries = await db.Orders
                    .OrderByDescending(o => o.Id)
                    .Skip((page - 1) * pageSize)
                    .Take(Math.Min(pageSize, 100))
                    .Select(o => new OrderSummary(o.Id, o.CustomerRef, o.Total, o.Status, o.CreatedAt, o.Lines.Count))
                    .ToListAsync(ct);
            }
            var total = await db.Orders.CountAsync(ct);
            return Results.Ok(new { items = summaries, page, pageSize, totalCount = total });
        });

        group.MapGet("/{id:int}", async (CatalogDbContext db, PathologyState pathology, HttpContext ctx, int id, CancellationToken ct) =>
        {
            pathology.OverlayHeaders(ctx.Request);
            if (pathology.DownstreamLatencyMs > 0 && !pathology.Optimised)
                await Task.Delay(pathology.DownstreamLatencyMs, ct);
            var order = await db.Orders
                .Include(o => o.Lines).ThenInclude(l => l.Product)
                .FirstOrDefaultAsync(o => o.Id == id, ct);
            return order is null ? Results.NotFound() : Results.Ok(order);
        });

        return app;
    }

    public static long CurrentLeakedBytes()
    {
        lock (LeakLock)
        {
            long sum = 0;
            foreach (var buf in LeakedBuffers) sum += buf.Length;
            return sum;
        }
    }

    public static void ClearLeaks()
    {
        lock (LeakLock) LeakedBuffers.Clear();
    }
}
