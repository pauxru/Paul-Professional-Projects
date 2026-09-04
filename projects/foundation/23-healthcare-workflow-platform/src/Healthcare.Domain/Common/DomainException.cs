namespace Healthcare.Domain.Common;

/// <summary>
/// Domain exception used for guarded invariant violations. Not for validation of inputs at the API edge.
/// </summary>
public class DomainException : Exception
{
    public string Code { get; }
    public DomainException(string code, string message) : base(message) => Code = code;
}
