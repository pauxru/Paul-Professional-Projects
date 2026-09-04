namespace Contoso.Payments.Application.Common;

/// <summary>
/// Result type returned from Application-layer handlers.  Success carries a value, failure
/// carries a code + message that the API translates to RFC 7807 ProblemDetails.
/// </summary>
public sealed record AppResult<T>(bool IsSuccess, T? Value, string? Code, string? Message, int? HttpStatus)
{
    public static AppResult<T> Ok(T value) => new(true, value, null, null, null);
    public static AppResult<T> Fail(string code, string message, int httpStatus) =>
        new(false, default, code, message, httpStatus);
}
