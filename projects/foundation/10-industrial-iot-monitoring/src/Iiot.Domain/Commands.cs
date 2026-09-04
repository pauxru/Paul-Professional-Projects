namespace Iiot.Domain;

public sealed record CommandRequest(
    string DeviceId,
    string CommandType,
    IReadOnlyDictionary<string, string> Parameters,
    string RequestedBy,
    string CorrelationId,
    TimeSpan Timeout);

public sealed record CommandAuditEntry(
    string CommandId,
    string Actor,
    string Action,
    DateTimeOffset At,
    string CorrelationId,
    string Detail);

public sealed class RemoteCommand
{
    public RemoteCommand(string commandId, CommandRequest request, DateTimeOffset queuedAt)
    {
        CommandId = commandId;
        Request = request;
        QueuedAt = queuedAt;
        Status = CommandStatus.Queued;
    }

    public string CommandId { get; }
    public CommandRequest Request { get; }
    public DateTimeOffset QueuedAt { get; }
    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset? AckedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? FailureReason { get; private set; }
    public CommandStatus Status { get; private set; }

    public void MarkSent(DateTimeOffset at) => Transition(CommandStatus.Sent, at);
    public void MarkAcked(DateTimeOffset at) => Transition(CommandStatus.Acked, at);
    public void MarkCompleted(DateTimeOffset at) => Transition(CommandStatus.Completed, at);

    public void MarkFailed(DateTimeOffset at, string reason)
    {
        FailureReason = reason;
        Transition(CommandStatus.Failed, at);
    }

    public void MarkTimedOut(DateTimeOffset at) => Transition(CommandStatus.TimedOut, at);

    private void Transition(CommandStatus next, DateTimeOffset at)
    {
        var valid = (Status, next) switch
        {
            (CommandStatus.Queued, CommandStatus.Sent) => true,
            (CommandStatus.Sent, CommandStatus.Acked) => true,
            (CommandStatus.Acked, CommandStatus.Completed) => true,
            (CommandStatus.Queued, CommandStatus.TimedOut) => true,
            (CommandStatus.Sent, CommandStatus.TimedOut) => true,
            (CommandStatus.Acked, CommandStatus.TimedOut) => true,
            (CommandStatus.Queued, CommandStatus.Failed) => true,
            (CommandStatus.Sent, CommandStatus.Failed) => true,
            _ => false
        };

        if (!valid)
        {
            throw new DomainRuleViolation($"Command cannot transition from {Status} to {next}.");
        }

        Status = next;
        switch (next)
        {
            case CommandStatus.Sent:
                SentAt = at;
                break;
            case CommandStatus.Acked:
                AckedAt = at;
                break;
            case CommandStatus.Completed:
            case CommandStatus.TimedOut:
            case CommandStatus.Failed:
                CompletedAt = at;
                break;
        }
    }
}
