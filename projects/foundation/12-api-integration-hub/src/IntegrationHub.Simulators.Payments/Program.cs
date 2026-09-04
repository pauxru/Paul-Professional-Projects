using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddSingleton<PaymentState>();
builder.Services.AddSingleton<PaymentFaultState>();
var app = builder.Build();

app.MapPost("/oauth/token", async (HttpContext context, PaymentState state) =>
{
    var form = await context.Request.ReadFormAsync();
    if (form["grant_type"] != "client_credentials"
        || form["client_id"] != "demo-client"
        || form["client_secret"] != "dev-only-simulator-secret")
    {
        return Results.Json(new { error = "invalid_client" }, statusCode: 401);
    }
    var token = $"sim-token-{Interlocked.Increment(ref state.TokenSequence)}";
    state.ValidTokens[token] = DateTimeOffset.UtcNow.AddMinutes(5);
    return Results.Ok(new { access_token = token, token_type = "Bearer", expires_in = 300 });
});

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health")
        || context.Request.Path.StartsWithSegments("/oauth"))
    {
        await next(context);
        return;
    }
    var state = context.RequestServices.GetRequiredService<PaymentState>();
    var token = context.Request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase);
    if (!state.ValidTokens.TryGetValue(token, out var expires) || expires <= DateTimeOffset.UtcNow)
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { title = "Invalid bearer token", status = 401 });
        return;
    }
    var faults = context.RequestServices.GetRequiredService<PaymentFaultState>();
    if (!context.Request.Path.StartsWithSegments("/admin") && Interlocked.CompareExchange(ref faults.RejectTokenOnce, 0, 0) > 0)
    {
        Interlocked.Decrement(ref faults.RejectTokenOnce);
        state.ValidTokens.TryRemove(token, out _);
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { title = "Token revoked", status = 401 });
        return;
    }
    if (!context.Request.Path.StartsWithSegments("/admin") && Interlocked.CompareExchange(ref faults.ThrottleNext, 0, 0) > 0)
    {
        Interlocked.Decrement(ref faults.ThrottleNext);
        context.Response.StatusCode = 429;
        context.Response.Headers.RetryAfter = faults.RetryAfterSeconds.ToString();
        await context.Response.WriteAsJsonAsync(new { title = "Payments rate limit", status = 429 });
        return;
    }
    if (!context.Request.Path.StartsWithSegments("/admin") && Interlocked.CompareExchange(ref faults.FailNext, 0, 0) > 0)
    {
        Interlocked.Decrement(ref faults.FailNext);
        context.Response.StatusCode = 502;
        await context.Response.WriteAsJsonAsync(new { title = "Payment upstream unavailable", status = 502 });
        return;
    }
    await next(context);
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", system = "PesaGate Payments (fictional)" }));
app.MapPost("/api/charges", (ChargeRequest request, PaymentState state, HttpContext context) =>
{
    if (request.Amount <= 0 || request.Currency is not ("KES" or "USD" or "EUR"))
    {
        return Results.Problem("A positive amount and supported currency are required.", statusCode: 422);
    }
    if (request.PaymentMethod == "declined")
    {
        return Results.Problem("Payment was declined by the simulator.", statusCode: 402);
    }
    var key = context.Request.Headers["Idempotency-Key"].ToString();
    if (!string.IsNullOrWhiteSpace(key) && state.ChargeIdempotency.TryGetValue(key, out var existing))
    {
        context.Response.Headers["X-Idempotent-Replay"] = "true";
        return Results.Ok(existing);
    }
    var charge = new Charge($"ch-{Interlocked.Increment(ref state.ChargeSequence):0000}", request.Amount, request.Currency, "captured", request.Reference);
    state.Charges[charge.Id] = charge;
    if (!string.IsNullOrWhiteSpace(key))
    {
        state.ChargeIdempotency[key] = charge;
    }
    return Results.Created($"/api/charges/{charge.Id}", charge);
});
app.MapPost("/api/refunds", (RefundRequest request, PaymentState state, HttpContext context) =>
{
    if (!state.Charges.TryGetValue(request.ChargeId, out var charge))
    {
        return Results.NotFound();
    }
    if (request.Amount <= 0 || request.Amount > charge.Amount)
    {
        return Results.Problem("Refund amount is invalid.", statusCode: 422);
    }
    var key = context.Request.Headers["Idempotency-Key"].ToString();
    if (!string.IsNullOrWhiteSpace(key) && state.RefundIdempotency.TryGetValue(key, out var existing))
    {
        return Results.Ok(existing);
    }
    var refund = new Refund($"rf-{Interlocked.Increment(ref state.RefundSequence):0000}", request.ChargeId, request.Amount, charge.Currency, "succeeded");
    state.Refunds[refund.Id] = refund;
    if (!string.IsNullOrWhiteSpace(key))
    {
        state.RefundIdempotency[key] = refund;
    }
    return Results.Created($"/api/refunds/{refund.Id}", refund);
});
app.MapGet("/api/settlements", (string? cursor, int? pageSize, PaymentState state, HttpContext context) =>
{
    var selectedPageSize = Math.Clamp(pageSize is null or 0 ? 2 : pageSize.Value, 1, 100);
    var offset = int.TryParse(cursor, out var parsed) ? parsed : 0;
    var items = state.Settlements.Skip(offset).Take(selectedPageSize).ToArray();
    if (offset + items.Length < state.Settlements.Count)
    {
        context.Response.Headers["X-Next-Cursor"] = (offset + items.Length).ToString();
    }
    return Results.Ok(new { items });
});
app.MapPost("/admin/faults", (PaymentFaultRequest request, PaymentFaultState state) =>
{
    state.FailNext = Math.Max(0, request.FailNext);
    state.ThrottleNext = Math.Max(0, request.ThrottleNext);
    state.RejectTokenOnce = Math.Max(0, request.RejectTokenOnce);
    state.RetryAfterSeconds = Math.Clamp(request.RetryAfterSeconds, 0, 60);
    return Results.Ok(state);
});
app.MapOpenApi();
app.Run();

public sealed class PaymentState
{
    public ConcurrentDictionary<string, DateTimeOffset> ValidTokens { get; } = new();
    public ConcurrentDictionary<string, Charge> Charges { get; } = new();
    public ConcurrentDictionary<string, Refund> Refunds { get; } = new();
    public ConcurrentDictionary<string, Charge> ChargeIdempotency { get; } = new();
    public ConcurrentDictionary<string, Refund> RefundIdempotency { get; } = new();
    public List<Settlement> Settlements { get; } =
    [
        new("st-001", 125000m, "KES", "2026-08-30"), new("st-002", 850m, "USD", "2026-08-31"),
        new("st-003", 225000m, "KES", "2026-09-01"), new("st-004", 1240m, "USD", "2026-09-02")
    ];
    public int TokenSequence;
    public int ChargeSequence;
    public int RefundSequence;
}

public sealed class PaymentFaultState
{
    public int FailNext;
    public int ThrottleNext;
    public int RejectTokenOnce;
    public int RetryAfterSeconds = 1;
}

public sealed record Charge(string Id, decimal Amount, string Currency, string Status, string Reference);
public sealed record ChargeRequest(decimal Amount, string Currency, string PaymentMethod, string Reference);
public sealed record Refund(string Id, string ChargeId, decimal Amount, string Currency, string Status);
public sealed record RefundRequest(string ChargeId, decimal Amount);
public sealed record Settlement(string Id, decimal Amount, string Currency, string SettledOn);
public sealed record PaymentFaultRequest(int FailNext, int ThrottleNext, int RejectTokenOnce, int RetryAfterSeconds);

namespace IntegrationHub.Simulators.Payments
{
    public sealed class PaymentsApiMarker;
}
