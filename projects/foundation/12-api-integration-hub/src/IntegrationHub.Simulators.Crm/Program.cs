using System.Collections.Concurrent;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddSingleton<CrmState>();
builder.Services.AddSingleton<FaultState>();
var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health"))
    {
        await next(context);
        return;
    }
    if (!string.Equals(context.Request.Headers["X-Api-Key"], "dev-only-simulator-key", StringComparison.Ordinal))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { title = "Invalid API key", status = 401 });
        return;
    }
    var faults = context.RequestServices.GetRequiredService<FaultState>();
    if (!context.Request.Path.StartsWithSegments("/admin") && Interlocked.CompareExchange(ref faults.ThrottleNext, 0, 0) > 0)
    {
        Interlocked.Decrement(ref faults.ThrottleNext);
        context.Response.StatusCode = 429;
        context.Response.Headers.RetryAfter = faults.RetryAfterSeconds.ToString();
        await context.Response.WriteAsJsonAsync(new { title = "CRM rate limit", status = 429 });
        return;
    }
    if (!context.Request.Path.StartsWithSegments("/admin") && Interlocked.CompareExchange(ref faults.FailNext, 0, 0) > 0)
    {
        Interlocked.Decrement(ref faults.FailNext);
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new { title = "CRM simulated outage", status = 503 });
        return;
    }
    await next(context);
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", system = "Contoso CRM" }));
app.MapGet("/api/contacts", (int page, int pageSize, CrmState state) =>
{
    page = Math.Max(page, 1);
    pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
    var source = state.Contacts.Values.OrderBy(x => x.Id).ToArray();
    var items = source.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
    return Results.Ok(new { items, page, pageSize, totalCount = source.Length });
});
app.MapGet("/api/contacts/{id}", (string id, CrmState state, HttpContext context) =>
{
    if (!state.Contacts.TryGetValue(id, out var contact))
    {
        return Results.NotFound();
    }
    context.Response.Headers.ETag = $"\"{contact.Version}\"";
    return Results.Ok(contact);
});
app.MapPost("/api/contacts", (ContactRequest request, CrmState state, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.Id)
        || string.IsNullOrWhiteSpace(request.DisplayName)
        || string.IsNullOrWhiteSpace(request.Email)
        || !request.Email.Contains('@'))
    {
        return Results.Problem("id, displayName and a valid email are required.", statusCode: 422);
    }
    var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
    if (!string.IsNullOrWhiteSpace(idempotencyKey)
        && state.Idempotency.TryGetValue(idempotencyKey, out var existing))
    {
        context.Response.Headers["X-Idempotent-Replay"] = "true";
        return Results.Ok(existing);
    }
    var contact = new Contact(request.Id, request.DisplayName.Trim(), request.Email.Trim(), request.Currency ?? "KES", 1);
    state.Contacts[contact.Id] = contact;
    if (!string.IsNullOrWhiteSpace(idempotencyKey))
    {
        state.Idempotency[idempotencyKey] = contact;
    }
    context.Response.Headers.ETag = "\"1\"";
    return Results.Created($"/api/contacts/{contact.Id}", contact);
});
app.MapPut("/api/contacts/{id}", (string id, ContactRequest request, CrmState state, HttpContext context) =>
{
    if (!state.Contacts.TryGetValue(id, out var current))
    {
        return Results.NotFound();
    }
    if (context.Request.Headers.IfMatch.ToString() != $"\"{current.Version}\"")
    {
        return Results.Problem("ETag does not match.", statusCode: 412);
    }
    var updated = current with
    {
        DisplayName = request.DisplayName.Trim(),
        Email = request.Email.Trim(),
        Currency = request.Currency ?? current.Currency,
        Version = current.Version + 1
    };
    state.Contacts[id] = updated;
    context.Response.Headers.ETag = $"\"{updated.Version}\"";
    return Results.Ok(updated);
});
app.MapGet("/api/accounts", (int offset, int limit, CrmState state) =>
{
    limit = Math.Clamp(limit == 0 ? 50 : limit, 1, 200);
    var source = state.Accounts.OrderBy(x => x.Id).ToArray();
    return Results.Ok(new { items = source.Skip(Math.Max(offset, 0)).Take(limit), totalCount = source.Length });
});
app.MapGet("/api/opportunities", (string? cursor, int? pageSize, CrmState state, HttpContext context) =>
{
    var selectedPageSize = Math.Clamp(pageSize is null or 0 ? 2 : pageSize.Value, 1, 100);
    var offset = int.TryParse(cursor, out var parsed) ? parsed : 0;
    var items = state.Opportunities.Skip(offset).Take(selectedPageSize).ToArray();
    if (offset + items.Length < state.Opportunities.Count)
    {
        context.Response.Headers["X-Next-Cursor"] = (offset + items.Length).ToString();
    }
    return Results.Ok(new { items });
});
app.MapPost("/admin/faults", (FaultRequest request, FaultState state) =>
{
    state.FailNext = Math.Max(0, request.FailNext);
    state.ThrottleNext = Math.Max(0, request.ThrottleNext);
    state.RetryAfterSeconds = Math.Clamp(request.RetryAfterSeconds, 0, 60);
    return Results.Ok(state);
});
app.MapOpenApi();
app.Run();

public sealed class CrmState
{
    public ConcurrentDictionary<string, Contact> Contacts { get; } = new(new[]
    {
        new KeyValuePair<string, Contact>("crm-001", new("crm-001", "Savanna Logistics Ltd (fictional)", "ops@savanna.example", "KES", 1)),
        new KeyValuePair<string, Contact>("crm-002", new("crm-002", "Jua Kali Manufacturing Ltd (fictional)", "finance@juakali.example", "KES", 1)),
        new KeyValuePair<string, Contact>("crm-003", new("crm-003", "Contoso Retail", "buyer@contoso.example", "USD", 1)),
        new KeyValuePair<string, Contact>("crm-004", new("crm-004", "Northstar Logistics", "team@northstar.example", "USD", 1))
    });
    public List<NamedRecord> Accounts { get; } =
    [
        new("acct-001", "Acme Manufacturing"), new("acct-002", "Contoso Retail"),
        new("acct-003", "Northstar Logistics"), new("acct-004", "Example Bank")
    ];
    public List<Opportunity> Opportunities { get; } =
    [
        new("opp-001", "acct-001", 125000m, "USD"), new("opp-002", "acct-002", 480000m, "KES"),
        new("opp-003", "acct-003", 82000m, "USD"), new("opp-004", "acct-004", 250000m, "KES")
    ];
    public ConcurrentDictionary<string, Contact> Idempotency { get; } = new();
}

public sealed class FaultState
{
    public int FailNext;
    public int ThrottleNext;
    public int RetryAfterSeconds = 1;
}

public sealed record Contact(string Id, string DisplayName, string Email, string Currency, int Version);
public sealed record ContactRequest(string Id, string DisplayName, string Email, string? Currency);
public sealed record NamedRecord(string Id, string Name);
public sealed record Opportunity(string Id, string AccountId, decimal Amount, string Currency);
public sealed record FaultRequest(int FailNext, int ThrottleNext, int RetryAfterSeconds);

namespace IntegrationHub.Simulators.Crm
{
    public sealed class CrmApiMarker;
}
