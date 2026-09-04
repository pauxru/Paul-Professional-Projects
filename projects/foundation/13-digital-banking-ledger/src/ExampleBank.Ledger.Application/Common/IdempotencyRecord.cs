namespace ExampleBank.Ledger.Application.Common;

/// <summary>
/// Stores the result of an idempotent command keyed by the client-supplied idempotency key.
/// A unique index on <see cref="Key"/> guarantees at-most-once execution; the stored
/// <see cref="ResponseJson"/> lets a retry replay the original response verbatim.
/// </summary>
public sealed class IdempotencyRecord
{
    private IdempotencyRecord() { } // EF

    public Guid Id { get; private set; }
    public string Key { get; private set; } = null!;
    public string Scope { get; private set; } = null!;
    public string RequestHash { get; private set; } = null!;
    public Guid ResultId { get; private set; }
    public string ResponseJson { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public static IdempotencyRecord Create(
        string key, string scope, string requestHash, Guid resultId, string responseJson, DateTimeOffset createdAt)
        => new()
        {
            Id = Guid.NewGuid(),
            Key = key,
            Scope = scope,
            RequestHash = requestHash,
            ResultId = resultId,
            ResponseJson = responseJson,
            CreatedAt = createdAt,
        };
}
