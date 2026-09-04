namespace ReconEngine.Application.Common;

/// <summary>Requested entity does not exist. Mapped to HTTP 404 by the API.</summary>
public sealed class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }
}

/// <summary>The request cannot proceed in the current state (e.g. no active ruleset). Mapped to HTTP 409.</summary>
public sealed class ConflictException : Exception
{
    public ConflictException(string message) : base(message) { }
}
