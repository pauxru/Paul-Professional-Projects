namespace JobScheduler.Application.Abstractions;

/// <summary>
/// The result a handler returns. A failure can be marked non-retriable to short-circuit the
/// retry policy (e.g. a permanent validation error) and dead-letter immediately.
/// </summary>
public sealed record HandlerOutcome(bool Succeeded, string? Output, string? Error, bool Retriable)
{
    public static HandlerOutcome Ok(string? output = null) => new(true, output, null, false);

    public static HandlerOutcome Fail(string error, bool retriable = true) =>
        new(false, null, error, retriable);
}

/// <summary>
/// Context handed to a handler for a single attempt. Handlers must honour
/// <see cref="CancellationToken"/> for cooperative timeout/cancellation, and should use
/// <see cref="IdempotencyKey"/> to make their side effects safe under at-least-once delivery.
/// </summary>
public sealed class JobExecutionContext(
    Guid runId,
    Guid jobDefinitionId,
    string jobName,
    string handlerType,
    string payloadJson,
    int attempt,
    string idempotencyKey,
    string correlationId,
    string nodeId)
{
    private readonly List<(string Level, string Message)> _logs = [];

    public Guid RunId { get; } = runId;
    public Guid JobDefinitionId { get; } = jobDefinitionId;
    public string JobName { get; } = jobName;
    public string HandlerType { get; } = handlerType;
    public string PayloadJson { get; } = payloadJson;
    public int Attempt { get; } = attempt;
    public string IdempotencyKey { get; } = idempotencyKey;
    public string CorrelationId { get; } = correlationId;
    public string NodeId { get; } = nodeId;

    public IReadOnlyList<(string Level, string Message)> Logs => _logs;

    public void Log(string message) => _logs.Add(("Information", message));

    public void Log(string level, string message) => _logs.Add((level, message));
}

/// <summary>
/// A registered unit of work. Handlers are the ONLY code that a payload can cause to run — there
/// is no arbitrary code/shell execution from a payload. The handler type on a job definition must
/// resolve to one of these through the allow-list registry.
/// </summary>
public interface IJobHandler
{
    /// <summary>Stable identifier matched against <c>JobDefinition.HandlerType</c>.</summary>
    string HandlerType { get; }

    Task<HandlerOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The allow-list of executable handlers. This is a security boundary: a job definition whose
/// handler type is not registered is rejected and never executed.
/// </summary>
public interface IHandlerRegistry
{
    bool IsRegistered(string handlerType);
    IJobHandler Resolve(string handlerType);
    IReadOnlyCollection<string> RegisteredTypes { get; }
}

/// <summary>Raised when a job references a handler type that is not on the allow-list.</summary>
public sealed class HandlerNotRegisteredException(string handlerType)
    : InvalidOperationException($"Handler type '{handlerType}' is not registered in the allow-list.")
{
    public string HandlerType { get; } = handlerType;
}
