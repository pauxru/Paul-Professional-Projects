using Iiot.Application;
using Iiot.Domain;
using Iiot.EdgeGateway;

namespace Iiot.UnitTests;

internal sealed class MemoryDeviceStore : IDeviceRegistryStore
{
    private readonly Dictionary<string, DeviceDescriptor> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceCredential> _credentials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceTwinSnapshot> _twins = new(StringComparer.Ordinal);

    public Task<bool> DeviceExistsAsync(string deviceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_devices.ContainsKey(deviceId));

    public Task CreateDeviceAsync(DeviceDescriptor device, DeviceCredential credential, DeviceTwinSnapshot twin, CancellationToken cancellationToken = default)
    {
        _devices.Add(device.DeviceId, device);
        _credentials.Add(device.DeviceId, credential);
        _twins.Add(device.DeviceId, twin);
        return Task.CompletedTask;
    }

    public Task<DeviceDescriptor?> FindDeviceAsync(string deviceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_devices.GetValueOrDefault(deviceId));

    public Task<IReadOnlyList<DeviceDescriptor>> ListDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeviceDescriptor>>(_devices.Values.OrderBy(item => item.DeviceId).ToArray());

    public Task<DeviceCredential?> FindCredentialAsync(string deviceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_credentials.GetValueOrDefault(deviceId));

    public Task UpdateDeviceStatusAsync(string deviceId, DeviceStatus status, CancellationToken cancellationToken = default)
    {
        var device = _devices[deviceId];
        _devices[deviceId] = device with { Status = status, IsRevoked = status == DeviceStatus.Revoked || device.IsRevoked };
        if (status == DeviceStatus.Revoked)
        {
            var credential = _credentials[deviceId];
            _credentials[deviceId] = credential with { IsRevoked = true };
        }

        return Task.CompletedTask;
    }

    public Task<DeviceTwinSnapshot?> GetTwinAsync(string deviceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_twins.GetValueOrDefault(deviceId));

    public Task SaveTwinAsync(string deviceId, DeviceTwinSnapshot twin, CancellationToken cancellationToken = default)
    {
        _twins[deviceId] = twin;
        return Task.CompletedTask;
    }
}

internal sealed class MemoryTelemetryStore : ITelemetryStore
{
    private readonly Dictionary<(string DeviceId, long Sequence), TelemetryReading> _readings = [];
    private readonly List<TelemetryRollup> _rollups = [];

    public Task<bool> TryAppendAsync(TelemetryReading reading, CancellationToken cancellationToken = default)
    {
        var key = (reading.DeviceId, reading.Sequence);
        if (_readings.ContainsKey(key))
        {
            return Task.FromResult(false);
        }

        _readings.Add(key, reading);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<TelemetryReading>> QueryTelemetryAsync(string deviceId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TelemetryReading>>(_readings.Values
            .Where(item => item.DeviceId == deviceId && item.DeviceTimestamp >= from && item.DeviceTimestamp <= to)
            .OrderBy(item => item.DeviceTimestamp).ThenBy(item => item.Sequence).ToArray());

    public Task ReplaceRollupsAsync(IReadOnlyList<TelemetryRollup> rollups, TimeSpan resolution, CancellationToken cancellationToken = default)
    {
        var keys = rollups.Select(item => (item.DeviceId, item.Metric, item.BucketStart, item.Resolution)).ToHashSet();
        _rollups.RemoveAll(item => keys.Contains((item.DeviceId, item.Metric, item.BucketStart, item.Resolution)));
        _rollups.AddRange(rollups);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TelemetryRollup>> QueryRollupsAsync(string deviceId, SensorMetric metric, TimeSpan resolution, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TelemetryRollup>>(_rollups
            .Where(item => item.DeviceId == deviceId && item.Metric == metric && item.Resolution == resolution && item.BucketStart >= from && item.BucketStart <= to)
            .OrderBy(item => item.BucketStart).ToArray());

    public Task<int> PruneRawTelemetryBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var keys = _readings.Where(item => item.Value.DeviceTimestamp < cutoff).Select(item => item.Key).ToArray();
        foreach (var key in keys)
        {
            _readings.Remove(key);
        }

        return Task.FromResult(keys.Length);
    }

    public Task<int> PruneRollupsBeforeAsync(TimeSpan resolution, DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var removed = _rollups.RemoveAll(item => item.Resolution == resolution && item.BucketStart < cutoff);
        return Task.FromResult(removed);
    }
}

internal sealed class MemoryCommandStore : ICommandStore
{
    private readonly Dictionary<string, CommandSnapshot> _commands = new(StringComparer.Ordinal);
    private readonly List<CommandAuditEntry> _audit = [];

    public Task SaveCommandAsync(CommandSnapshot command, CancellationToken cancellationToken = default)
    {
        _commands[command.CommandId] = command;
        return Task.CompletedTask;
    }

    public Task<CommandSnapshot?> FindCommandAsync(string commandId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_commands.GetValueOrDefault(commandId));

    public Task<IReadOnlyList<CommandSnapshot>> ListCommandsAsync(string? deviceId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CommandSnapshot>>(_commands.Values
            .Where(item => deviceId is null || item.DeviceId == deviceId)
            .OrderBy(item => item.QueuedAt).ToArray());

    public Task AddAuditAsync(CommandAuditEntry entry, CancellationToken cancellationToken = default)
    {
        _audit.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CommandAuditEntry>> ListAuditAsync(string commandId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CommandAuditEntry>>(_audit.Where(item => item.CommandId == commandId).ToArray());
}

internal sealed class MemoryAlertStore : IAlertStore
{
    private readonly Dictionary<string, AlertSnapshot> _alerts = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<AlertSnapshot>> ListAlertsAsync(bool activeOnly, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AlertSnapshot>>(_alerts.Values
            .Where(item => !activeOnly || item.State != AlertState.Resolved)
            .OrderBy(item => item.FiredAt).ToArray());

    public Task<AlertSnapshot?> FindActiveAlertAsync(string ruleId, string deviceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_alerts.Values
            .Where(item => item.RuleId == ruleId && item.DeviceId == deviceId && item.State != AlertState.Resolved)
            .OrderByDescending(item => item.FiredAt)
            .FirstOrDefault());

    public Task SaveAlertAsync(AlertSnapshot alert, CancellationToken cancellationToken = default)
    {
        _alerts[alert.AlertId] = alert;
        return Task.CompletedTask;
    }

    public Task<AlertSnapshot?> FindAlertAsync(string alertId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_alerts.GetValueOrDefault(alertId));
}

internal sealed class FixedKeyProtector : IDeviceKeyProtector
{
    public string Hash(string value) => $"hash:{value}";
    public bool Verify(string value, string hash) => hash == Hash(value);
}

internal sealed class CollectingCloudSink : ICloudTelemetrySink
{
    private readonly HashSet<(string, long)> _dedup = [];
    public bool ThrowWhenCalled { get; set; }
    public List<TelemetryReading> Delivered { get; } = [];

    public Task<CloudIngestionReceipt> IngestAsync(IReadOnlyList<TelemetryReading> readings, CancellationToken cancellationToken = default)
    {
        if (ThrowWhenCalled)
        {
            throw new HttpRequestException("Synthetic cloud outage.");
        }

        var accepted = 0;
        var duplicates = 0;
        foreach (var reading in readings)
        {
            if (_dedup.Add((reading.DeviceId, reading.Sequence)))
            {
                Delivered.Add(reading);
                accepted++;
            }
            else
            {
                duplicates++;
            }
        }

        return Task.FromResult(new CloudIngestionReceipt(accepted, duplicates, 0));
    }
}
