using Microsoft.EntityFrameworkCore;

namespace Iiot.Infrastructure;

public sealed class IiotDbContext(DbContextOptions<IiotDbContext> options) : DbContext(options)
{
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<DeviceCredentialEntity> DeviceCredentials => Set<DeviceCredentialEntity>();
    public DbSet<DeviceTwinEntity> DeviceTwins => Set<DeviceTwinEntity>();
    public DbSet<TelemetryEntity> Telemetry => Set<TelemetryEntity>();
    public DbSet<RollupEntity> Rollups => Set<RollupEntity>();
    public DbSet<RuleEntity> Rules => Set<RuleEntity>();
    public DbSet<AlertEntity> Alerts => Set<AlertEntity>();
    public DbSet<CommandEntity> Commands => Set<CommandEntity>();
    public DbSet<AuditEntity> AuditEntries => Set<AuditEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceEntity>(entity =>
        {
            entity.ToTable("devices");
            entity.HasKey(item => item.DeviceId);
            entity.Property(item => item.DeviceId).HasMaxLength(128);
            entity.Property(item => item.DeviceType).HasMaxLength(64);
            entity.Property(item => item.CreatedAt).HasConversion(
                value => value.ToUnixTimeMilliseconds(),
                value => DateTimeOffset.FromUnixTimeMilliseconds(value));
            entity.HasIndex(item => new { item.Plant, item.Line, item.Asset });
            entity.HasIndex(item => item.Status);
        });
        modelBuilder.Entity<DeviceCredentialEntity>(entity =>
        {
            entity.ToTable("device_credentials");
            entity.HasKey(item => item.DeviceId);
            entity.Property(item => item.KeyHash).HasMaxLength(512);
            entity.Property(item => item.EnrollmentTokenHash).HasMaxLength(512);
        });
        modelBuilder.Entity<DeviceTwinEntity>(entity =>
        {
            entity.ToTable("device_twins");
            entity.HasKey(item => item.DeviceId);
            entity.Property(item => item.DesiredJson).HasColumnType("TEXT");
            entity.Property(item => item.ReportedJson).HasColumnType("TEXT");
        });
        modelBuilder.Entity<TelemetryEntity>(entity =>
        {
            entity.ToTable("telemetry_raw");
            entity.HasKey(item => new { item.DeviceId, item.Sequence });
            entity.HasIndex(item => new { item.DeviceId, item.DeviceTimestamp });
            entity.HasIndex(item => item.DeviceTimestamp);
            entity.Property(item => item.DeviceTimestamp).HasConversion(
                value => value.ToUnixTimeMilliseconds(),
                value => DateTimeOffset.FromUnixTimeMilliseconds(value));
            entity.Property(item => item.TemperatureC).HasPrecision(12, 3);
            entity.Property(item => item.VibrationMmPerSecondRms).HasPrecision(12, 3);
            entity.Property(item => item.PressureBar).HasPrecision(12, 3);
            entity.Property(item => item.CurrentA).HasPrecision(12, 3);
            entity.Property(item => item.FlowLitresPerMinute).HasPrecision(12, 3);
            entity.Property(item => item.TankLevelPercent).HasPrecision(12, 3);
        });
        modelBuilder.Entity<RollupEntity>(entity =>
        {
            entity.ToTable("telemetry_rollups");
            entity.HasKey(item => new { item.DeviceId, item.Metric, item.ResolutionSeconds, item.BucketStart });
            entity.HasIndex(item => new { item.DeviceId, item.ResolutionSeconds, item.BucketStart });
            entity.Property(item => item.BucketStart).HasConversion(
                value => value.ToUnixTimeMilliseconds(),
                value => DateTimeOffset.FromUnixTimeMilliseconds(value));
            entity.Property(item => item.Min).HasPrecision(16, 4);
            entity.Property(item => item.Max).HasPrecision(16, 4);
            entity.Property(item => item.Average).HasPrecision(16, 4);
            entity.Property(item => item.StandardDeviation).HasPrecision(16, 4);
        });
        modelBuilder.Entity<RuleEntity>(entity =>
        {
            entity.ToTable("alert_rules");
            entity.HasKey(item => item.RuleId);
            entity.HasIndex(item => item.DeviceId);
            entity.Property(item => item.ConditionsJson).HasColumnType("TEXT");
        });
        modelBuilder.Entity<AlertEntity>(entity =>
        {
            entity.ToTable("alerts");
            entity.HasKey(item => item.AlertId);
            entity.HasIndex(item => new { item.DeviceId, item.RuleId, item.State });
            entity.Property(item => item.FiredAt).HasConversion(
                value => value.ToUnixTimeMilliseconds(),
                value => DateTimeOffset.FromUnixTimeMilliseconds(value));
            entity.Property(item => item.AcknowledgedAt).HasConversion(
                value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : (long?)null,
                value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null);
            entity.Property(item => item.ResolvedAt).HasConversion(
                value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : (long?)null,
                value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null);
        });
        modelBuilder.Entity<CommandEntity>(entity =>
        {
            entity.ToTable("commands");
            entity.HasKey(item => item.CommandId);
            entity.HasIndex(item => new { item.DeviceId, item.Status });
            entity.HasIndex(item => item.TimeoutAt);
            entity.Property(item => item.ParametersJson).HasColumnType("TEXT");
            entity.Property(item => item.QueuedAt).HasConversion(
                value => value.ToUnixTimeMilliseconds(),
                value => DateTimeOffset.FromUnixTimeMilliseconds(value));
            entity.Property(item => item.SentAt).HasConversion(
                value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : (long?)null,
                value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null);
            entity.Property(item => item.AckedAt).HasConversion(
                value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : (long?)null,
                value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null);
            entity.Property(item => item.CompletedAt).HasConversion(
                value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : (long?)null,
                value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null);
            entity.Property(item => item.TimeoutAt).HasConversion(
                value => value.ToUnixTimeMilliseconds(),
                value => DateTimeOffset.FromUnixTimeMilliseconds(value));
        });
        modelBuilder.Entity<AuditEntity>(entity =>
        {
            entity.ToTable("audit_log");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.CommandId, item.At });
            entity.Property(item => item.Detail).HasColumnType("TEXT");
            entity.Property(item => item.At).HasConversion(
                value => value.ToUnixTimeMilliseconds(),
                value => DateTimeOffset.FromUnixTimeMilliseconds(value));
        });
    }
}

public sealed class DeviceEntity
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string Plant { get; set; } = string.Empty;
    public string Line { get; set; } = string.Empty;
    public string Asset { get; set; } = string.Empty;
    public string FirmwareVersion { get; set; } = string.Empty;
    public int Status { get; set; }
    public string? CertificateThumbprint { get; set; }
    public bool IsRevoked { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DeviceCredentialEntity
{
    public string DeviceId { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public string EnrollmentTokenHash { get; set; } = string.Empty;
    public string? CertificateThumbprint { get; set; }
    public bool IsRevoked { get; set; }
}

public sealed class DeviceTwinEntity
{
    public string DeviceId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string DesiredJson { get; set; } = "{}";
    public string ReportedJson { get; set; } = "{}";
}

public sealed class TelemetryEntity
{
    public string DeviceId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public DateTimeOffset DeviceTimestamp { get; set; }
    public decimal TemperatureC { get; set; }
    public decimal VibrationMmPerSecondRms { get; set; }
    public decimal PressureBar { get; set; }
    public decimal CurrentA { get; set; }
    public decimal FlowLitresPerMinute { get; set; }
    public decimal TankLevelPercent { get; set; }
    public int MachineState { get; set; }
    public int Quality { get; set; }
}

public sealed class RollupEntity
{
    public string DeviceId { get; set; } = string.Empty;
    public int Metric { get; set; }
    public int ResolutionSeconds { get; set; }
    public DateTimeOffset BucketStart { get; set; }
    public decimal Min { get; set; }
    public decimal Max { get; set; }
    public decimal Average { get; set; }
    public decimal StandardDeviation { get; set; }
    public int Count { get; set; }
}

public sealed class RuleEntity
{
    public string RuleId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public int Kind { get; set; }
    public int Metric { get; set; }
    public int Comparison { get; set; }
    public decimal Threshold { get; set; }
    public decimal Hysteresis { get; set; }
    public long DwellSeconds { get; set; }
    public long WindowSeconds { get; set; }
    public string? ConditionsJson { get; set; }
    public int CompositeOperator { get; set; }
    public long? SuppressionSeconds { get; set; }
    public bool Enabled { get; set; }
    public bool IsMaintenanceSilenced { get; set; }
}

public sealed class AlertEntity
{
    public string AlertId { get; set; } = string.Empty;
    public string RuleId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int State { get; set; }
    public DateTimeOffset FiredAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class CommandEntity
{
    public string CommandId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string CommandType { get; set; } = string.Empty;
    public string ParametersJson { get; set; } = "{}";
    public string RequestedBy { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public int Status { get; set; }
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? AckedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset TimeoutAt { get; set; }
}

public sealed class AuditEntity
{
    public long Id { get; set; }
    public string CommandId { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}
