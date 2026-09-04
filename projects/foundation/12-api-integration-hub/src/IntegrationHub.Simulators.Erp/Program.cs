using System.Collections.Concurrent;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddSingleton<ErpState>();
builder.Services.AddSingleton<ErpFaultState>();
var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health"))
    {
        await next(context);
        return;
    }
    var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("demo:dev-only-simulator-password"));
    if (!string.Equals(context.Request.Headers.Authorization, expected, StringComparison.Ordinal))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { title = "Basic authentication required", status = 401 });
        return;
    }
    var faults = context.RequestServices.GetRequiredService<ErpFaultState>();
    if (!context.Request.Path.StartsWithSegments("/admin") && Interlocked.CompareExchange(ref faults.ThrottleNext, 0, 0) > 0)
    {
        Interlocked.Decrement(ref faults.ThrottleNext);
        context.Response.StatusCode = 429;
        context.Response.Headers.RetryAfter = faults.RetryAfterSeconds.ToString();
        await context.Response.WriteAsJsonAsync(new { title = "ERP rate limit", status = 429 });
        return;
    }
    if (!context.Request.Path.StartsWithSegments("/admin") && Interlocked.CompareExchange(ref faults.FailNext, 0, 0) > 0)
    {
        Interlocked.Decrement(ref faults.FailNext);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { title = "ERP simulated fault", status = 500 });
        return;
    }
    if (!context.Request.Path.StartsWithSegments("/admin") && context.Request.Method != "GET"
        && Interlocked.CompareExchange(ref faults.RejectNext, 0, 0) > 0)
    {
        Interlocked.Decrement(ref faults.RejectNext);
        context.Response.StatusCode = 422;
        await context.Response.WriteAsJsonAsync(new { title = "ERP simulated poison record", status = 422 });
        return;
    }
    await next(context);
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", system = "Acme ERP" }));
app.MapGet("/api/customers", (int offset, int limit, ErpState state) =>
{
    limit = Math.Clamp(limit == 0 ? 50 : limit, 1, 200);
    var source = state.Customers.Values.OrderBy(x => x.Id).ToArray();
    return Results.Ok(new { items = source.Skip(Math.Max(0, offset)).Take(limit), totalCount = source.Length });
});
app.MapPost("/api/customers", (CustomerRequest request, ErpState state, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Name)
        || request.Currency is not ("KES" or "USD" or "EUR"))
    {
        return Results.Problem("id, name and an ISO currency (KES/USD/EUR) are required.", statusCode: 422);
    }
    var key = context.Request.Headers["Idempotency-Key"].ToString();
    if (!string.IsNullOrWhiteSpace(key) && state.Idempotency.TryGetValue(key, out var cached))
    {
        context.Response.Headers["X-Idempotent-Replay"] = "true";
        return Results.Ok(cached);
    }
    var customer = state.Customers.AddOrUpdate(
        request.Id,
        _ => new Customer(request.Id, request.Name, request.Currency, 1),
        (_, current) => current with { Name = request.Name, Currency = request.Currency, Version = current.Version + 1 });
    if (!string.IsNullOrWhiteSpace(key))
    {
        state.Idempotency[key] = customer;
    }
    context.Response.Headers.ETag = $"\"{customer.Version}\"";
    return Results.Ok(customer);
});
app.MapPut("/api/customers/{id}", (string id, CustomerRequest request, ErpState state, HttpContext context) =>
{
    if (!state.Customers.TryGetValue(id, out var current))
    {
        return Results.NotFound();
    }
    if (context.Request.Headers.IfMatch.ToString() != $"\"{current.Version}\"")
    {
        return Results.Problem("Optimistic concurrency conflict.", statusCode: 412);
    }
    var updated = current with { Name = request.Name, Currency = request.Currency, Version = current.Version + 1 };
    state.Customers[id] = updated;
    context.Response.Headers.ETag = $"\"{updated.Version}\"";
    return Results.Ok(updated);
});
app.MapGet("/api/products", (int? page, int? pageSize, ErpState state, HttpContext context) =>
{
    var selectedPage = Math.Max(1, page ?? 1);
    var selectedPageSize = Math.Clamp(page is null || pageSize is null or 0 ? 2 : pageSize.Value, 1, 100);
    var items = state.Products.Skip((selectedPage - 1) * selectedPageSize).Take(selectedPageSize).ToArray();
    if (selectedPage * selectedPageSize < state.Products.Count)
    {
        context.Response.Headers.Link = $"<http://localhost:5212/api/products?page={selectedPage + 1}&pageSize={selectedPageSize}>; rel=\"next\"";
    }
    return Results.Ok(new { items });
});
app.MapPost("/api/sales-orders", (SalesOrderRequest request, ErpState state, HttpContext context) =>
{
    if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Any(x => x.Quantity <= 0))
    {
        return Results.Problem("At least one positive-quantity line is required.", statusCode: 422);
    }
    var key = context.Request.Headers["Idempotency-Key"].ToString();
    if (!string.IsNullOrWhiteSpace(key) && state.OrderIdempotency.TryGetValue(key, out var existing))
    {
        return Results.Ok(existing);
    }
    var order = new SalesOrder($"so-{Interlocked.Increment(ref state.OrderSequence):0000}", request.CustomerId, request.Lines, 1);
    state.Orders[order.Id] = order;
    if (!string.IsNullOrWhiteSpace(key))
    {
        state.OrderIdempotency[key] = order;
    }
    return Results.Created($"/api/sales-orders/{order.Id}", order);
});
app.MapGet("/api/invoices", (int page, int pageSize, ErpState state) =>
{
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
    var source = state.Invoices.ToArray();
    return Results.Ok(new { items = source.Skip((page - 1) * pageSize).Take(pageSize), totalCount = source.Length });
});
app.MapPost("/admin/faults", (ErpFaultRequest request, ErpFaultState state) =>
{
    state.FailNext = Math.Max(0, request.FailNext);
    state.ThrottleNext = Math.Max(0, request.ThrottleNext);
    state.RejectNext = Math.Max(0, request.RejectNext);
    state.RetryAfterSeconds = Math.Clamp(request.RetryAfterSeconds, 0, 60);
    return Results.Ok(state);
});
app.MapOpenApi();
app.Run();

public sealed class ErpState
{
    public ConcurrentDictionary<string, Customer> Customers { get; } = new();
    public List<Product> Products { get; } =
    [
        new("prod-001", "Industrial Pump", 2500m, "USD"), new("prod-002", "Safety Helmet", 1800m, "KES"),
        new("prod-003", "Sensor Kit", 750m, "USD"), new("prod-004", "Maintenance Plan", 120000m, "KES")
    ];
    public List<Invoice> Invoices { get; } =
    [
        new("inv-001", "crm-001", 85000m, "KES"), new("inv-002", "crm-002", 125000m, "KES")
    ];
    public ConcurrentDictionary<string, Customer> Idempotency { get; } = new();
    public ConcurrentDictionary<string, SalesOrder> Orders { get; } = new();
    public ConcurrentDictionary<string, SalesOrder> OrderIdempotency { get; } = new();
    public int OrderSequence;
}

public sealed class ErpFaultState
{
    public int FailNext;
    public int ThrottleNext;
    public int RejectNext;
    public int RetryAfterSeconds = 1;
}

public sealed record Customer(string Id, string Name, string Currency, int Version);
public sealed record CustomerRequest(string Id, string Name, string Currency);
public sealed record Product(string Id, string Name, decimal UnitPrice, string Currency);
public sealed record Invoice(string Id, string CustomerId, decimal Amount, string Currency);
public sealed record SalesOrder(string Id, string CustomerId, IReadOnlyList<SalesOrderLine> Lines, int Version);
public sealed record SalesOrderLine(string ProductId, int Quantity, decimal UnitPrice);
public sealed record SalesOrderRequest(string CustomerId, IReadOnlyList<SalesOrderLine> Lines);
public sealed record ErpFaultRequest(int FailNext, int ThrottleNext, int RejectNext, int RetryAfterSeconds);

namespace IntegrationHub.Simulators.Erp
{
    public sealed class ErpApiMarker;
}
