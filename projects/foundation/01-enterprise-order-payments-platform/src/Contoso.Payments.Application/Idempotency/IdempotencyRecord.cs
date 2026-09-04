namespace Contoso.Payments.Application.Idempotency;

/// <summary>
/// One row per (idempotency-key, endpoint) tuple.  Stores the original response so replays
/// return byte-identical output.  The UNIQUE index guarantees at-most-one execution.
/// </summary>
public sealed class IdempotencyRecord
{
    public string Key { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public int ResponseStatus { get; set; }
    public string ResponseBody { get; set; } = string.Empty;
    public string ResponseContentType { get; set; } = "application/json";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}
