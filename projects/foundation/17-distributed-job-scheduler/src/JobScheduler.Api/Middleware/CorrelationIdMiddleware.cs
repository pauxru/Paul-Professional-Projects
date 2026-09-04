namespace JobScheduler.Api.Middleware;

/// <summary>
/// Ensures every request has a correlation id. Reads <c>X-Correlation-ID</c> if supplied, otherwise
/// generates one; echoes it on the response and stores it in <see cref="HttpContext.Items"/> so it
/// can be stamped onto the runs a request triggers (and thus into their structured logs).
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var provided)
                            && !string.IsNullOrWhiteSpace(provided)
            ? provided.ToString()
            : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object> { [ItemKey] = correlationId }))
        {
            await next(context);
        }
    }
}

/// <summary>Convenience accessor for the current request correlation id.</summary>
public static class CorrelationIdAccessor
{
    public static string Current(this HttpContext context) =>
        context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var v) && v is string s
            ? s
            : Guid.NewGuid().ToString("N");
}
