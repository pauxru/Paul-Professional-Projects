using System.Security.Cryptography;
using System.Text.Json;
using Iiot.Application;
using Iiot.Domain;
using Microsoft.EntityFrameworkCore;

namespace Iiot.Infrastructure;

public sealed class SqliteIiotRepository(IiotDbContext database) :
    IDeviceRegistryStore,
    ITelemetryStore,
    IRuleStore,
    IAlertStore,
    ICommandStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<bool> DeviceExistsAsync(string deviceId, CancellationToken cancellationToken = default) =>
        database.Devices.AnyAsync(item => item.DeviceId == deviceId, cancellationToken);

    public async Task CreateDeviceAsync(
        DeviceDescriptor device,
        DeviceCredential credential,
        DeviceTwinSnapshot twin,
        CancellationToken cancellationToken = default)
    {
        database.Devices.Add(ToEntity(device));
        database.DeviceCredentials.Add(new DeviceCredentialEntity
        {
            DeviceId = credential.DeviceId,
            KeyHash = credential.KeyHash,
            EnrollmentTokenHash = credential.EnrollmentTokenHash,
            CertificateThumbprint = credential.CertificateThumbprint,
            IsRevoked = credential.IsRevoked
        });
        database.DeviceTwins.Add(ToEntity(device.DeviceId, twin));
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<DeviceDescriptor?> FindDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var entity = await database.Devices.AsNoTracking().SingleOrDefaultAsync(item => item.DeviceId == deviceId, cancellationToken);
        return entity is null ? null : ToDescriptor(entity);
    }

    public async Task<IReadOnlyList<DeviceDescriptor>> ListDevicesAsync(CancellationToken cancellationToken = default) =>
        (await database.Devices.AsNoTracking().OrderBy(item => item.DeviceId).ToListAsync(cancellationToken)).Select(ToDescriptor).ToArray();

    public async Task<DeviceCredential?> FindCredentialAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var entity = await database.DeviceCredentials.AsNoTracking().SingleOrDefaultAsync(item => item.DeviceId == deviceId, cancellationToken);
        return entity is null
            ? null
            : new DeviceCredential(entity.DeviceId, entity.KeyHash, entity.EnrollmentTokenHash, entity.CertificateThumbprint, entity.IsRevoked);
    }

    public async Task UpdateDeviceStatusAsync(string deviceId, DeviceStatus status, CancellationToken cancellationToken = default)
    {
        var device = await database.Devices.SingleOrDefaultAsync(item => item.DeviceId == deviceId, cancellationToken)
            ?? throw new DomainRuleViolation($"Device '{deviceId}' does not exist.");
        device.Status = (int)status;
        if (status == DeviceStatus.Revoked)
        {
            device.IsRevoked = true;
            var credential = await database.DeviceCredentials.SingleOrDefaultAsync(item => item.DeviceId == deviceId, cancellationToken);
            if (credential is not null)
            {
                credential.IsRevoked = true;
            }
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<DeviceTwinSnapshot?> GetTwinAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var entity = await database.DeviceTwins.AsNoTracking().SingleOrDefaultAsync(item => item.DeviceId == deviceId, cancellationToken);
        return entity is null ? null : ToTwin(entity);
    }

    public async Task SaveTwinAsync(string deviceId, DeviceTwinSnapshot twin, CancellationToken cancellationToken = default)
    {
        var entity = await database.DeviceTwins.SingleOrDefaultAsync(item => item.DeviceId == deviceId, cancellationToken)
            ?? throw new DomainRuleViolation($"Device '{deviceId}' does not exist.");
        entity.Version = twin.Version;
        entity.DesiredJson = JsonSerializer.Serialize(twin.Desired, JsonOptions);
        entity.ReportedJson = JsonSerializer.Serialize(twin.Reported, JsonOptions);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> TryAppendAsync(TelemetryReading reading, CancellationToken cancellationToken = default)
    {
        // Fast duplicate path avoids expected unique-constraint noise during gateway replays.
        if (await database.Telemetry.AnyAsync(
                item => item.DeviceId == reading.DeviceId && item.Sequence == reading.Sequence,
                cancellationToken))
        {
            return false;
        }

        var entity = ToEntity(reading);
        database.Telemetry.Add(entity);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            database.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<IReadOnlyList<TelemetryReading>> QueryTelemetryAsync(
        string deviceId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default) =>
        (await database.Telemetry.AsNoTracking()
            .Where(item => item.DeviceId == deviceId && item.DeviceTimestamp >= from && item.DeviceTimestamp <= to)
            .OrderBy(item => item.DeviceTimestamp).ThenBy(item => item.Sequence)
            .ToListAsync(cancellationToken))
        .Select(ToReading)
        .ToArray();

    public async Task ReplaceRollupsAsync(
        IReadOnlyList<TelemetryRollup> rollups,
        TimeSpan resolution,
        CancellationToken cancellationToken = default)
    {
        if (rollups.Count == 0)
        {
            return;
        }

        var keys = rollups
            .Select(item => new { item.DeviceId, Metric = (int)item.Metric, item.BucketStart })
            .ToHashSet();
        var seconds = checked((int)resolution.TotalSeconds);
        var existing = await database.Rollups
            .Where(item => item.ResolutionSeconds == seconds)
            .ToListAsync(cancellationToken);
        database.Rollups.RemoveRange(existing.Where(item => keys.Contains(new { item.DeviceId, item.Metric, item.BucketStart })));
        database.Rollups.AddRange(rollups.Select(ToEntity));
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TelemetryRollup>> QueryRollupsAsync(
        string deviceId,
        SensorMetric metric,
        TimeSpan resolution,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default) =>
        (await database.Rollups.AsNoTracking()
            .Where(item => item.DeviceId == deviceId &&
                           item.Metric == (int)metric &&
                           item.ResolutionSeconds == (int)resolution.TotalSeconds &&
                           item.BucketStart >= from &&
                           item.BucketStart <= to)
            .OrderBy(item => item.BucketStart)
            .ToListAsync(cancellationToken))
        .Select(item => new TelemetryRollup(
            item.DeviceId,
            (SensorMetric)item.Metric,
            item.BucketStart,
            TimeSpan.FromSeconds(item.ResolutionSeconds),
            item.Min,
            item.Max,
            item.Average,
            item.StandardDeviation,
            item.Count))
        .ToArray();

    public Task<int> PruneRawTelemetryBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
        database.Telemetry.Where(item => item.DeviceTimestamp < cutoff).ExecuteDeleteAsync(cancellationToken);

    public Task<int> PruneRollupsBeforeAsync(TimeSpan resolution, DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
        database.Rollups
            .Where(item => item.ResolutionSeconds == (int)resolution.TotalSeconds && item.BucketStart < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

    public async Task<IReadOnlyList<RuleDefinition>> ListRulesAsync(string? deviceId = null, CancellationToken cancellationToken = default)
    {
        var query = database.Rules.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            query = query.Where(item => item.DeviceId == deviceId);
        }

        return (await query.OrderBy(item => item.RuleId).ToListAsync(cancellationToken)).Select(ToRule).ToArray();
    }

    public async Task SaveRuleAsync(RuleDefinition rule, CancellationToken cancellationToken = default)
    {
        var entity = await database.Rules.SingleOrDefaultAsync(item => item.RuleId == rule.RuleId, cancellationToken);
        if (entity is null)
        {
            database.Rules.Add(ToEntity(rule));
        }
        else
        {
            Copy(ToEntity(rule), entity);
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteRuleAsync(string ruleId, CancellationToken cancellationToken = default)
    {
        var entity = await database.Rules.SingleOrDefaultAsync(item => item.RuleId == ruleId, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        database.Rules.Remove(entity);
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<AlertSnapshot>> ListAlertsAsync(bool activeOnly, CancellationToken cancellationToken = default)
    {
        var query = database.Alerts.AsNoTracking().AsQueryable();
        if (activeOnly)
        {
            query = query.Where(item => item.State != (int)AlertState.Resolved);
        }

        return (await query.OrderByDescending(item => item.FiredAt).ToListAsync(cancellationToken)).Select(ToSnapshot).ToArray();
    }

    public async Task<AlertSnapshot?> FindActiveAlertAsync(string ruleId, string deviceId, CancellationToken cancellationToken = default)
    {
        var entity = await database.Alerts.AsNoTracking()
            .Where(item => item.RuleId == ruleId && item.DeviceId == deviceId && item.State != (int)AlertState.Resolved)
            .OrderByDescending(item => item.FiredAt)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task SaveAlertAsync(AlertSnapshot alert, CancellationToken cancellationToken = default)
    {
        var entity = await database.Alerts.SingleOrDefaultAsync(item => item.AlertId == alert.AlertId, cancellationToken);
        if (entity is null)
        {
            database.Alerts.Add(ToEntity(alert));
        }
        else
        {
            entity.Reason = alert.Reason;
            entity.State = (int)alert.State;
            entity.AcknowledgedAt = alert.AcknowledgedAt;
            entity.ResolvedAt = alert.ResolvedAt;
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<AlertSnapshot?> FindAlertAsync(string alertId, CancellationToken cancellationToken = default)
    {
        var entity = await database.Alerts.AsNoTracking().SingleOrDefaultAsync(item => item.AlertId == alertId, cancellationToken);
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task SaveCommandAsync(CommandSnapshot command, CancellationToken cancellationToken = default)
    {
        var entity = await database.Commands.SingleOrDefaultAsync(item => item.CommandId == command.CommandId, cancellationToken);
        if (entity is null)
        {
            database.Commands.Add(ToEntity(command));
        }
        else
        {
            Copy(ToEntity(command), entity);
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<CommandSnapshot?> FindCommandAsync(string commandId, CancellationToken cancellationToken = default)
    {
        var entity = await database.Commands.AsNoTracking().SingleOrDefaultAsync(item => item.CommandId == commandId, cancellationToken);
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task<IReadOnlyList<CommandSnapshot>> ListCommandsAsync(string? deviceId = null, CancellationToken cancellationToken = default)
    {
        var query = database.Commands.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            query = query.Where(item => item.DeviceId == deviceId);
        }

        return (await query.OrderByDescending(item => item.QueuedAt).ToListAsync(cancellationToken)).Select(ToSnapshot).ToArray();
    }

    public async Task AddAuditAsync(CommandAuditEntry entry, CancellationToken cancellationToken = default)
    {
        database.AuditEntries.Add(new AuditEntity
        {
            CommandId = entry.CommandId,
            Actor = entry.Actor,
            Action = entry.Action,
            At = entry.At,
            CorrelationId = entry.CorrelationId,
            Detail = entry.Detail
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CommandAuditEntry>> ListAuditAsync(string commandId, CancellationToken cancellationToken = default) =>
        (await database.AuditEntries.AsNoTracking()
            .Where(item => item.CommandId == commandId)
            .OrderBy(item => item.At)
            .ToListAsync(cancellationToken))
        .Select(item => new CommandAuditEntry(item.CommandId, item.Actor, item.Action, item.At, item.CorrelationId, item.Detail))
        .ToArray();

    private static DeviceEntity ToEntity(DeviceDescriptor source) => new()
    {
        DeviceId = source.DeviceId,
        DeviceType = source.DeviceType,
        Plant = source.Plant,
        Line = source.Line,
        Asset = source.Asset,
        FirmwareVersion = source.FirmwareVersion,
        Status = (int)source.Status,
        CertificateThumbprint = source.CertificateThumbprint,
        IsRevoked = source.IsRevoked,
        CreatedAt = source.CreatedAt
    };

    private static DeviceDescriptor ToDescriptor(DeviceEntity source) => new(
        source.DeviceId,
        source.DeviceType,
        source.Plant,
        source.Line,
        source.Asset,
        source.FirmwareVersion,
        (DeviceStatus)source.Status,
        source.CertificateThumbprint,
        source.IsRevoked,
        source.CreatedAt);

    private static DeviceTwinEntity ToEntity(string deviceId, DeviceTwinSnapshot twin) => new()
    {
        DeviceId = deviceId,
        Version = twin.Version,
        DesiredJson = JsonSerializer.Serialize(twin.Desired, JsonOptions),
        ReportedJson = JsonSerializer.Serialize(twin.Reported, JsonOptions)
    };

    private static DeviceTwinSnapshot ToTwin(DeviceTwinEntity source) => new(
        source.Version,
        JsonSerializer.Deserialize<Dictionary<string, string>>(source.DesiredJson, JsonOptions) ?? new Dictionary<string, string>(),
        JsonSerializer.Deserialize<Dictionary<string, string>>(source.ReportedJson, JsonOptions) ?? new Dictionary<string, string>());

    private static TelemetryEntity ToEntity(TelemetryReading source) => new()
    {
        DeviceId = source.DeviceId,
        Sequence = source.Sequence,
        DeviceTimestamp = source.DeviceTimestamp,
        TemperatureC = source.Values.TemperatureC,
        VibrationMmPerSecondRms = source.Values.VibrationMmPerSecondRms,
        PressureBar = source.Values.PressureBar,
        CurrentA = source.Values.CurrentA,
        FlowLitresPerMinute = source.Values.FlowLitresPerMinute,
        TankLevelPercent = source.Values.TankLevelPercent,
        MachineState = (int)source.Values.MachineState,
        Quality = (int)source.Quality
    };

    private static TelemetryReading ToReading(TelemetryEntity source) => new(
        source.DeviceId,
        source.Sequence,
        source.DeviceTimestamp,
        new TelemetryValues(
            source.TemperatureC,
            source.VibrationMmPerSecondRms,
            source.PressureBar,
            source.CurrentA,
            source.FlowLitresPerMinute,
            source.TankLevelPercent,
            (MachineState)source.MachineState),
        (QualityFlag)source.Quality);

    private static RollupEntity ToEntity(TelemetryRollup source) => new()
    {
        DeviceId = source.DeviceId,
        Metric = (int)source.Metric,
        ResolutionSeconds = (int)source.Resolution.TotalSeconds,
        BucketStart = source.BucketStart,
        Min = source.Min,
        Max = source.Max,
        Average = source.Average,
        StandardDeviation = source.StandardDeviation,
        Count = source.Count
    };

    private static RuleEntity ToEntity(RuleDefinition source) => new()
    {
        RuleId = source.RuleId,
        DeviceId = source.DeviceId,
        Kind = (int)source.Kind,
        Metric = (int)source.Metric,
        Comparison = (int)source.Comparison,
        Threshold = source.Threshold,
        Hysteresis = source.Hysteresis,
        DwellSeconds = (long)source.Dwell.TotalSeconds,
        WindowSeconds = (long)source.Window.TotalSeconds,
        ConditionsJson = source.Conditions is null ? null : JsonSerializer.Serialize(source.Conditions, JsonOptions),
        CompositeOperator = (int)source.CompositeOperator,
        SuppressionSeconds = source.SuppressionWindow is null ? null : (long)source.SuppressionWindow.Value.TotalSeconds,
        Enabled = source.Enabled,
        IsMaintenanceSilenced = source.IsMaintenanceSilenced
    };

    private static RuleDefinition ToRule(RuleEntity source) => new(
        source.RuleId,
        source.DeviceId,
        (RuleKind)source.Kind,
        (SensorMetric)source.Metric,
        (RuleComparison)source.Comparison,
        source.Threshold,
        source.Hysteresis,
        TimeSpan.FromSeconds(source.DwellSeconds),
        TimeSpan.FromSeconds(source.WindowSeconds),
        source.ConditionsJson is null ? null : JsonSerializer.Deserialize<List<RuleCondition>>(source.ConditionsJson, JsonOptions),
        (CompositeOperator)source.CompositeOperator,
        source.SuppressionSeconds is null ? null : TimeSpan.FromSeconds(source.SuppressionSeconds.Value),
        source.Enabled,
        source.IsMaintenanceSilenced);

    private static void Copy(RuleEntity from, RuleEntity to)
    {
        to.DeviceId = from.DeviceId;
        to.Kind = from.Kind;
        to.Metric = from.Metric;
        to.Comparison = from.Comparison;
        to.Threshold = from.Threshold;
        to.Hysteresis = from.Hysteresis;
        to.DwellSeconds = from.DwellSeconds;
        to.WindowSeconds = from.WindowSeconds;
        to.ConditionsJson = from.ConditionsJson;
        to.CompositeOperator = from.CompositeOperator;
        to.SuppressionSeconds = from.SuppressionSeconds;
        to.Enabled = from.Enabled;
        to.IsMaintenanceSilenced = from.IsMaintenanceSilenced;
    }

    private static AlertEntity ToEntity(AlertSnapshot source) => new()
    {
        AlertId = source.AlertId,
        RuleId = source.RuleId,
        DeviceId = source.DeviceId,
        Reason = source.Reason,
        State = (int)source.State,
        FiredAt = source.FiredAt,
        AcknowledgedAt = source.AcknowledgedAt,
        ResolvedAt = source.ResolvedAt
    };

    private static AlertSnapshot ToSnapshot(AlertEntity source) => new(
        source.AlertId,
        source.RuleId,
        source.DeviceId,
        source.Reason,
        (AlertState)source.State,
        source.FiredAt,
        source.AcknowledgedAt,
        source.ResolvedAt);

    private static CommandEntity ToEntity(CommandSnapshot source) => new()
    {
        CommandId = source.CommandId,
        DeviceId = source.DeviceId,
        CommandType = source.CommandType,
        ParametersJson = source.ParametersJson,
        RequestedBy = source.RequestedBy,
        CorrelationId = source.CorrelationId,
        Status = (int)source.Status,
        QueuedAt = source.QueuedAt,
        SentAt = source.SentAt,
        AckedAt = source.AckedAt,
        CompletedAt = source.CompletedAt,
        FailureReason = source.FailureReason,
        Attempts = source.Attempts,
        TimeoutAt = source.TimeoutAt
    };

    private static CommandSnapshot ToSnapshot(CommandEntity source) => new(
        source.CommandId,
        source.DeviceId,
        source.CommandType,
        source.ParametersJson,
        source.RequestedBy,
        source.CorrelationId,
        (CommandStatus)source.Status,
        source.QueuedAt,
        source.SentAt,
        source.AckedAt,
        source.CompletedAt,
        source.FailureReason,
        source.Attempts,
        source.TimeoutAt);

    private static void Copy(CommandEntity from, CommandEntity to)
    {
        to.DeviceId = from.DeviceId;
        to.CommandType = from.CommandType;
        to.ParametersJson = from.ParametersJson;
        to.RequestedBy = from.RequestedBy;
        to.CorrelationId = from.CorrelationId;
        to.Status = from.Status;
        to.QueuedAt = from.QueuedAt;
        to.SentAt = from.SentAt;
        to.AckedAt = from.AckedAt;
        to.CompletedAt = from.CompletedAt;
        to.FailureReason = from.FailureReason;
        to.Attempts = from.Attempts;
        to.TimeoutAt = from.TimeoutAt;
    }
}

public sealed class Pbkdf2DeviceKeyProtector : IDeviceKeyProtector
{
    private const int Iterations = 210_000;

    public string Hash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(value, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string value, string hash)
    {
        try
        {
            var parts = hash.Split('.');
            if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
            {
                return false;
            }

            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(value, Convert.FromBase64String(parts[1]), iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
