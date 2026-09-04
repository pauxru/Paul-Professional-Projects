namespace ExampleBank.Ledger.Application.Common;

/// <summary>A requested resource did not exist. Maps to HTTP 404.</summary>
public sealed class NotFoundException(string message) : Exception(message);

/// <summary>A conflicting state prevented the operation (e.g. version conflict). Maps to HTTP 409.</summary>
public sealed class ConflictException(string message) : Exception(message);

/// <summary>Edge-level validation failure. Maps to HTTP 422 with an errors dictionary.</summary>
public sealed class RequestValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("One or more validation errors occurred.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    public static RequestValidationException Single(string field, string message)
        => new(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>
/// A unique constraint (an idempotency key) was violated during commit. The command executor
/// treats this as a signal to replay the original stored response.
/// </summary>
public sealed class DuplicateKeyException(string message) : Exception(message);

/// <summary>An optimistic-concurrency version check failed. Retried by the command executor.</summary>
public sealed class ConcurrencyConflictException(string message) : Exception(message);

/// <summary>A transient storage error (e.g. SQLite busy/locked). Retried by the command executor.</summary>
public sealed class TransientStorageException(string message) : Exception(message);
