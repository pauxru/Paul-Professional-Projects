using Iiot.Domain;

namespace Iiot.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class FakeClock(DateTimeOffset initial) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = initial;

    public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    public void Set(DateTimeOffset value) => UtcNow = value;
}

public interface IDeviceKeyProtector
{
    string Hash(string value);
    bool Verify(string value, string hash);
}

public interface IDeviceRegistryStore
{
    Task<bool> DeviceExistsAsync(string deviceId, CancellationToken cancellationToken = default);
    Task CreateDeviceAsync(DeviceDescriptor device, DeviceCredential credential, DeviceTwinSnapshot twin, CancellationToken cancellationToken = default);
    Task<DeviceDescriptor?> FindDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceDescriptor>> ListDevicesAsync(CancellationToken cancellationToken = default);
    Task<DeviceCredential?> FindCredentialAsync(string deviceId, CancellationToken cancellationToken = default);
    Task UpdateDeviceStatusAsync(string deviceId, DeviceStatus status, CancellationToken cancellationToken = default);
    Task<DeviceTwinSnapshot?> GetTwinAsync(string deviceId, CancellationToken cancellationToken = default);
    Task SaveTwinAsync(string deviceId, DeviceTwinSnapshot twin, CancellationToken cancellationToken = default);
}

public interface ITelemetryStore
{
    Task<bool> TryAppendAsync(TelemetryReading reading, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TelemetryReading>> QueryTelemetryAsync(
        string deviceId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);
    Task ReplaceRollupsAsync(
        IReadOnlyList<TelemetryRollup> rollups,
        TimeSpan resolution,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TelemetryRollup>> QueryRollupsAsync(
        string deviceId,
        SensorMetric metric,
        TimeSpan resolution,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);
    Task<int> PruneRawTelemetryBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
    Task<int> PruneRollupsBeforeAsync(TimeSpan resolution, DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}

public interface IRuleStore
{
    Task<IReadOnlyList<RuleDefinition>> ListRulesAsync(string? deviceId = null, CancellationToken cancellationToken = default);
    Task SaveRuleAsync(RuleDefinition rule, CancellationToken cancellationToken = default);
    Task<bool> DeleteRuleAsync(string ruleId, CancellationToken cancellationToken = default);
}

public sealed record AlertSnapshot(
    string AlertId,
    string RuleId,
    string DeviceId,
    string Reason,
    AlertState State,
    DateTimeOffset FiredAt,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? ResolvedAt);

public interface IAlertStore
{
    Task<IReadOnlyList<AlertSnapshot>> ListAlertsAsync(bool activeOnly, CancellationToken cancellationToken = default);
    Task<AlertSnapshot?> FindActiveAlertAsync(string ruleId, string deviceId, CancellationToken cancellationToken = default);
    Task SaveAlertAsync(AlertSnapshot alert, CancellationToken cancellationToken = default);
    Task<AlertSnapshot?> FindAlertAsync(string alertId, CancellationToken cancellationToken = default);
}

public sealed record CommandSnapshot(
    string CommandId,
    string DeviceId,
    string CommandType,
    string ParametersJson,
    string RequestedBy,
    string CorrelationId,
    CommandStatus Status,
    DateTimeOffset QueuedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? AckedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason,
    int Attempts,
    DateTimeOffset TimeoutAt);

public interface ICommandStore
{
    Task SaveCommandAsync(CommandSnapshot command, CancellationToken cancellationToken = default);
    Task<CommandSnapshot?> FindCommandAsync(string commandId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommandSnapshot>> ListCommandsAsync(string? deviceId = null, CancellationToken cancellationToken = default);
    Task AddAuditAsync(CommandAuditEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CommandAuditEntry>> ListAuditAsync(string commandId, CancellationToken cancellationToken = default);
}
