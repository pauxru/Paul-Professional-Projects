namespace Iiot.Domain;

public enum SensorMetric
{
    TemperatureC,
    VibrationMmPerSecondRms,
    PressureBar,
    CurrentA,
    FlowLitresPerMinute,
    TankLevelPercent
}

public enum QualityFlag
{
    Good,
    Suspect,
    Bad,
    Missing
}

public enum MachineState
{
    Stopped,
    Starting,
    Running,
    Idle,
    Faulted
}

public enum DeviceStatus
{
    Provisioned,
    Online,
    Offline,
    Revoked,
    Maintenance
}

public enum RuleKind
{
    Threshold,
    RateOfChange,
    MissingData,
    Composite
}

public enum RuleComparison
{
    GreaterThanOrEqual,
    LessThanOrEqual
}

public enum CompositeOperator
{
    And,
    Or
}

public enum AlertState
{
    Firing,
    Acknowledged,
    Resolved
}

public enum CommandStatus
{
    Queued,
    Sent,
    Acked,
    Completed,
    TimedOut,
    Failed
}

public enum SimulatedFault
{
    None,
    BearingWear,
    Overheating,
    SensorStuckAt,
    SensorDrift,
    Dropout,
    Spike
}

public sealed class DomainRuleViolation(string message) : InvalidOperationException(message);
