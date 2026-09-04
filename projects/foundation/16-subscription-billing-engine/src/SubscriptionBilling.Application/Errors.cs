namespace SubscriptionBilling.Application;

public sealed class ResourceNotFoundException(string message) : InvalidOperationException(message);

public sealed class ConflictException(string message) : InvalidOperationException(message);

public sealed class RequestValidationException(
    string message,
    IReadOnlyDictionary<string, string[]> errors) : InvalidOperationException(message)
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}
