using System.Text.Json;
using Iiot.Domain;

namespace Iiot.Application;

public static class CommandSchemas
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedByDeviceType =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["compressor"] = ["restart", "setSamplingInterval", "firmwareUpdate"],
            ["motor"] = ["restart", "setSamplingInterval", "firmwareUpdate"],
            ["chiller"] = ["restart", "setTargetTemperature", "setSamplingInterval", "firmwareUpdate"],
            ["tank"] = ["setSamplingInterval", "firmwareUpdate"]
        };

    public static void Validate(string deviceType, CommandRequest request)
    {
        if (!AllowedByDeviceType.TryGetValue(deviceType, out var commands) ||
            !commands.Contains(request.CommandType, StringComparer.OrdinalIgnoreCase))
        {
            throw new DomainRuleViolation($"Command '{request.CommandType}' is not permitted for device type '{deviceType}'.");
        }

        switch (request.CommandType.ToLowerInvariant())
        {
            case "restart" when request.Parameters.Count != 0:
                throw new DomainRuleViolation("Restart does not accept parameters.");
            case "setsamplinginterval" when !request.Parameters.TryGetValue("seconds", out var seconds) ||
                                            !int.TryParse(seconds, out var interval) ||
                                            interval is < 1 or > 3600 ||
                                            request.Parameters.Count != 1:
                throw new DomainRuleViolation("setSamplingInterval requires an integer seconds parameter between 1 and 3600.");
            case "settargettemperature" when !request.Parameters.TryGetValue("celsius", out var temperature) ||
                                               !decimal.TryParse(temperature, out var celsius) ||
                                               celsius is < 5 or > 40 ||
                                               request.Parameters.Count != 1:
                throw new DomainRuleViolation("setTargetTemperature requires a celsius parameter between 5 and 40.");
            case "firmwareupdate" when !request.Parameters.TryGetValue("version", out var version) ||
                                        string.IsNullOrWhiteSpace(version) ||
                                        request.Parameters.Count != 1:
                throw new DomainRuleViolation("firmwareUpdate requires a version parameter.");
        }
    }
}

public sealed class CommandService(
    ICommandStore commandStore,
    IDeviceRegistryStore deviceStore,
    IClock clock)
{
    public async Task<CommandSnapshot> QueueAsync(CommandRequest request, CancellationToken cancellationToken = default)
    {
        var device = await deviceStore.FindDeviceAsync(request.DeviceId, cancellationToken)
            ?? throw new DomainRuleViolation($"Device '{request.DeviceId}' does not exist.");
        if (device.IsRevoked)
        {
            throw new DomainRuleViolation("Commands cannot be sent to revoked devices.");
        }

        CommandSchemas.Validate(device.DeviceType, request);
        var command = new CommandSnapshot(
            Guid.NewGuid().ToString("N"),
            request.DeviceId,
            request.CommandType,
            JsonSerializer.Serialize(request.Parameters),
            request.RequestedBy,
            request.CorrelationId,
            CommandStatus.Queued,
            clock.UtcNow,
            null,
            null,
            null,
            null,
            0,
            clock.UtcNow + request.Timeout);
        await commandStore.SaveCommandAsync(command, cancellationToken);
        await commandStore.AddAuditAsync(
            new CommandAuditEntry(command.CommandId, request.RequestedBy, "Queued", clock.UtcNow, request.CorrelationId, request.CommandType),
            cancellationToken);
        return command;
    }

    public async Task<CommandSnapshot> TransitionAsync(
        string commandId,
        CommandStatus next,
        string actor,
        string? failureReason = null,
        CancellationToken cancellationToken = default)
    {
        var command = await commandStore.FindCommandAsync(commandId, cancellationToken)
            ?? throw new DomainRuleViolation($"Command '{commandId}' does not exist.");
        EnsureTransition(command.Status, next);
        var now = clock.UtcNow;
        var updated = command with
        {
            Status = next,
            SentAt = next == CommandStatus.Sent ? now : command.SentAt,
            AckedAt = next == CommandStatus.Acked ? now : command.AckedAt,
            CompletedAt = next is CommandStatus.Completed or CommandStatus.Failed or CommandStatus.TimedOut ? now : command.CompletedAt,
            FailureReason = failureReason ?? command.FailureReason,
            Attempts = next == CommandStatus.Sent ? command.Attempts + 1 : command.Attempts
        };
        await commandStore.SaveCommandAsync(updated, cancellationToken);
        await commandStore.AddAuditAsync(new CommandAuditEntry(commandId, actor, next.ToString(), now, command.CorrelationId, failureReason ?? string.Empty), cancellationToken);
        return updated;
    }

    public async Task<int> TimeoutExpiredAsync(CancellationToken cancellationToken = default)
    {
        var expired = (await commandStore.ListCommandsAsync(null, cancellationToken))
            .Where(command => command.Status is CommandStatus.Queued or CommandStatus.Sent or CommandStatus.Acked)
            .Where(command => command.TimeoutAt <= clock.UtcNow)
            .ToArray();
        foreach (var command in expired)
        {
            await TransitionAsync(command.CommandId, CommandStatus.TimedOut, "system", cancellationToken: cancellationToken);
        }

        return expired.Length;
    }

    public async Task<CommandSnapshot> RetryAsync(
        string commandId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var command = await commandStore.FindCommandAsync(commandId, cancellationToken)
            ?? throw new DomainRuleViolation($"Command '{commandId}' does not exist.");
        if (command.Status != CommandStatus.Sent || command.TimeoutAt <= clock.UtcNow)
        {
            throw new DomainRuleViolation("Only unacknowledged, non-expired sent commands can be retried.");
        }

        var retried = command with { SentAt = clock.UtcNow, Attempts = command.Attempts + 1 };
        await commandStore.SaveCommandAsync(retried, cancellationToken);
        await commandStore.AddAuditAsync(new CommandAuditEntry(commandId, actor, "Retried", clock.UtcNow, command.CorrelationId, command.CommandType), cancellationToken);
        return retried;
    }

    private static void EnsureTransition(CommandStatus current, CommandStatus next)
    {
        var valid = (current, next) switch
        {
            (CommandStatus.Queued, CommandStatus.Sent) => true,
            (CommandStatus.Queued, CommandStatus.TimedOut) => true,
            (CommandStatus.Queued, CommandStatus.Failed) => true,
            (CommandStatus.Sent, CommandStatus.Acked) => true,
            (CommandStatus.Sent, CommandStatus.TimedOut) => true,
            (CommandStatus.Sent, CommandStatus.Failed) => true,
            (CommandStatus.Acked, CommandStatus.Completed) => true,
            (CommandStatus.Acked, CommandStatus.TimedOut) => true,
            _ => false
        };
        if (!valid)
        {
            throw new DomainRuleViolation($"Invalid command transition {current} -> {next}.");
        }
    }
}
