namespace Iiot.Domain;

public sealed record TelemetryValues(
    decimal TemperatureC,
    decimal VibrationMmPerSecondRms,
    decimal PressureBar,
    decimal CurrentA,
    decimal FlowLitresPerMinute,
    decimal TankLevelPercent,
    MachineState MachineState)
{
    public decimal GetMetric(SensorMetric metric) => metric switch
    {
        SensorMetric.TemperatureC => TemperatureC,
        SensorMetric.VibrationMmPerSecondRms => VibrationMmPerSecondRms,
        SensorMetric.PressureBar => PressureBar,
        SensorMetric.CurrentA => CurrentA,
        SensorMetric.FlowLitresPerMinute => FlowLitresPerMinute,
        SensorMetric.TankLevelPercent => TankLevelPercent,
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };
}

public sealed record TelemetryReading(
    string DeviceId,
    long Sequence,
    DateTimeOffset DeviceTimestamp,
    TelemetryValues Values,
    QualityFlag Quality = QualityFlag.Good)
{
    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(DeviceId) || Sequence < 0)
        {
            throw new DomainRuleViolation("Telemetry requires a device identifier and non-negative sequence.");
        }

        if (Values.TankLevelPercent is < 0 or > 100)
        {
            throw new DomainRuleViolation("Tank level must be between 0 and 100 percent.");
        }

        if (Values.VibrationMmPerSecondRms < 0 || Values.PressureBar < 0 || Values.CurrentA < 0 || Values.FlowLitresPerMinute < 0)
        {
            throw new DomainRuleViolation("Physical quantities that cannot be negative must be non-negative.");
        }
    }
}

public sealed record TelemetryRollup(
    string DeviceId,
    SensorMetric Metric,
    DateTimeOffset BucketStart,
    TimeSpan Resolution,
    decimal Min,
    decimal Max,
    decimal Average,
    decimal StandardDeviation,
    int Count);
