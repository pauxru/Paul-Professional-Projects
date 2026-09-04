namespace ExampleBank.Ledger.Api.Middleware;

/// <summary>
/// Assigns a correlation id to every request (from the <c>X-Correlation-ID</c> header if present,
/// otherwise generated) and echoes it back on the response, so a request can be traced across the
/// ledger's logs, entries and ProblemDetails responses.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        string correlationId = context.Request.Headers.TryGetValue(HeaderName, out var provided)
            && !string.IsNullOrWhiteSpace(provided)
                ? provided.ToString()
                : Guid.NewGuid().ToString("n");

        context.Items["CorrelationId"] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        await _next(context);
    }
}
