namespace Contoso.Payments.Domain.Common;

/// <summary>
/// Raised when a business rule or invariant would be violated.  These are expected errors and
/// should be surfaced to callers as HTTP 4xx (typically 422).
/// </summary>
public sealed class DomainException : Exception
{
    public string Code { get; }

    public DomainException(string message, string code = "domain.rule_violation")
        : base(message)
    {
        Code = code;
    }
}
