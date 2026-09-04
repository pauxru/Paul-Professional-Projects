namespace Contoso.Payments.Api.Middleware;

public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "CorrelationId";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _log;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> log)
    {
        _next = next;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var incoming = ctx.Request.Headers[HeaderName].ToString();
        var correlationId = string.IsNullOrWhiteSpace(incoming)
            ? Guid.NewGuid().ToString("N")
            : incoming;
        ctx.Items[ItemKey] = correlationId;
        ctx.Response.Headers[HeaderName] = correlationId;

        using (_log.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["Path"] = ctx.Request.Path.ToString()
        }))
        {
            await _next(ctx);
        }
    }
}

public static class HttpContextCorrelationExtensions
{
    public static string CorrelationId(this HttpContext ctx)
        => ctx.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var v) && v is string s
            ? s : "unknown";
}
