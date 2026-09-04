namespace ReconEngine.Domain.Abstractions;

/// <summary>Raised when a domain invariant or a state-machine transition is violated.</summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}

/// <summary>Raised specifically when an exception-workflow state transition is not permitted.</summary>
public sealed class InvalidStateTransitionException : DomainException
{
    public InvalidStateTransitionException(string message) : base(message) { }
}
