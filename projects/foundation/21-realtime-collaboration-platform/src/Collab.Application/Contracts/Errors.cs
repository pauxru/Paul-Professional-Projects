namespace Collab.Application.Contracts;

/// <summary>Base type for application errors that map to specific HTTP status codes via ProblemDetails.</summary>
public abstract class AppException(string message) : Exception(message)
{
    public abstract int StatusCode { get; }
    public virtual string Title => "Request failed";
}

public sealed class NotFoundException(string message) : AppException(message)
{
    public override int StatusCode => 404;
    public override string Title => "Not found";
}

public sealed class ForbiddenException(string message) : AppException(message)
{
    public override int StatusCode => 403;
    public override string Title => "Forbidden";
}

public sealed class ConflictException(string message) : AppException(message)
{
    public override int StatusCode => 409;
    public override string Title => "Conflict";
}

/// <summary>A domain rule rejected an otherwise well-formed request (HTTP 422).</summary>
public sealed class DomainRuleException(string message) : AppException(message)
{
    public override int StatusCode => 422;
    public override string Title => "Unprocessable entity";
}

/// <summary>Edge validation failure (HTTP 400) carrying a field-error dictionary.</summary>
public sealed class ValidationAppException(IReadOnlyDictionary<string, string[]> errors)
    : AppException("One or more validation errors occurred.")
{
    public override int StatusCode => 400;
    public override string Title => "Validation failed";
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    public ValidationAppException(string field, string error)
        : this(new Dictionary<string, string[]> { [field] = [error] }) { }
}
