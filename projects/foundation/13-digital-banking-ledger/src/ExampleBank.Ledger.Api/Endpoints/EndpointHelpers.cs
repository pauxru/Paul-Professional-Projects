using ExampleBank.Ledger.Application.Common;

namespace ExampleBank.Ledger.Api.Endpoints;

/// <summary>Helpers for reading ledger request context (idempotency key, correlation id, paging) at the edge.</summary>
public static class EndpointHelpers
{
    public const string IdempotencyHeader = "Idempotency-Key";

    public static string? IdempotencyKey(this HttpContext context) =>
        context.Request.Headers.TryGetValue(IdempotencyHeader, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : null;

    public static string CorrelationId(this HttpContext context) =>
        context.Items.TryGetValue("CorrelationId", out var value) && value is string s ? s : string.Empty;

    public static PageRequest Paging(this HttpContext context)
    {
        int page = TryInt(context, "page") ?? 1;
        int size = TryInt(context, "pageSize") ?? 50;
        return new PageRequest(page, size);
    }

    private static int? TryInt(HttpContext context, string key) =>
        context.Request.Query.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : null;
}
