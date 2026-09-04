using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Iiot.Application;
using Iiot.Domain;
using Iiot.Protocol;

namespace Iiot.Device;

public sealed record FaultInjection(SimulatedFault Fault, DateTimeOffset StartsAt);

public sealed record OtaResult(bool Succeeded, string ReportedFirmwareVersion, string Status, string Detail);

public sealed record CommandExecutionResult(bool Accepted, bool Completed, string Detail);

public sealed class SimulatedDevice
{
    private readonly Random _random;
    private TelemetryValues? _stuckValues;
    private long _sequence;

    public SimulatedDevice(
        string deviceId,
        string deviceType,
        string plant,
        string firmwareVersion,
        int seed = 42)
    {
        DeviceId = deviceId;
        DeviceType = deviceType;
        Plant = plant;
        FirmwareVersion = firmwareVersion;
        _random = new Random(seed);
    }

    public string DeviceId { get; }
    public string DeviceType { get; }
    public string Plant { get; }
    public string FirmwareVersion { get; private set; }
    public FaultInjection? Fault { get; private set; }

    public void Inject(FaultInjection? fault) => Fault = fault;

    public TelemetryReading Generate(DateTimeOffset timestamp)
    {
        var hourRadians = 2d * Math.PI * timestamp.TimeOfDay.TotalHours / 24d;
        var elapsedMinutes = Fault is null ? 0d : Math.Max(0d, (timestamp - Fault.StartsAt).TotalMinutes);
        decimal Noise(double amplitude) => (decimal)((_random.NextDouble() - 0.5d) * amplitude);

        var temperature = 58m + (decimal)(3d * Math.Sin(hourRadians)) + Noise(0.8);
        var vibration = 2.2m + (decimal)(0.25d * Math.Sin(hourRadians + 1)) + Noise(0.12);
        var pressure = 7.4m + (decimal)(0.15d * Math.Sin(hourRadians - 1)) + Noise(0.05);
        var current = 26m + (decimal)(1.5d * Math.Sin(hourRadians + 0.5)) + Noise(0.4);
        var flow = 185m + (decimal)(12d * Math.Sin(hourRadians - 0.2)) + Noise(1.5);
        var tankLevel = 64m + (decimal)(8d * Math.Sin(hourRadians / 2)) + Noise(0.5);
        var quality = QualityFlag.Good;

        if (Fault is { } fault)
        {
            switch (fault.Fault)
            {
                case SimulatedFault.BearingWear:
                    vibration += (decimal)Math.Min(12d, elapsedMinutes * 0.12d);
                    current += (decimal)Math.Min(8d, elapsedMinutes * 0.04d);
                    break;
                case SimulatedFault.Overheating:
                    temperature += (decimal)Math.Min(45d, elapsedMinutes * 0.35d);
                    current += (decimal)Math.Min(5d, elapsedMinutes * 0.03d);
                    break;
                case SimulatedFault.SensorDrift:
                    pressure += (decimal)Math.Min(3d, elapsedMinutes * 0.025d);
                    break;
                case SimulatedFault.Dropout:
                    quality = QualityFlag.Missing;
                    break;
                case SimulatedFault.Spike:
                    temperature += 32m;
                    vibration += 8m;
                    quality = QualityFlag.Suspect;
                    break;
            }
        }

        var values = new TelemetryValues(
            decimal.Round(temperature, 3),
            decimal.Round(vibration, 3),
            decimal.Round(pressure, 3),
            decimal.Round(current, 3),
            decimal.Round(flow, 3),
            decimal.Round(decimal.Clamp(tankLevel, 0m, 100m), 3),
            quality == QualityFlag.Missing ? MachineState.Idle : MachineState.Running);

        if (Fault?.Fault == SimulatedFault.SensorStuckAt)
        {
            _stuckValues ??= values;
            values = _stuckValues;
            quality = QualityFlag.Suspect;
        }
        else
        {
            _stuckValues = null;
        }

        return new TelemetryReading(DeviceId, _sequence++, timestamp, values, quality);
    }

    public async Task<TelemetryReading> PublishTelemetryAsync(
        IMessageTransport transport,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        var reading = Generate(timestamp);
        var payload = JsonSerializer.SerializeToUtf8Bytes(reading, TelemetryWireFormat.JsonOptions);
        await transport.PublishAsync(
            new TransportMessage($"plants/{Plant}/devices/{DeviceId}/telemetry", payload, MqttQualityOfService.AtLeastOnce),
            cancellationToken);
        return reading;
    }

    public Task<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request)
    {
        try
        {
            CommandSchemas.Validate(DeviceType, request);
            return Task.FromResult(new CommandExecutionResult(true, true, $"{request.CommandType} completed safely."));
        }
        catch (DomainRuleViolation exception)
        {
            return Task.FromResult(new CommandExecutionResult(false, false, exception.Message));
        }
    }

    public Task<OtaResult> ApplyDesiredFirmwareAsync(
        DeviceTwinSnapshot twin,
        Func<IReadOnlyDictionary<string, string?>, Task>? reportPatch = null)
    {
        if (!twin.Desired.TryGetValue("firmwareVersion", out var requested) || string.IsNullOrWhiteSpace(requested))
        {
            return Task.FromResult(new OtaResult(true, FirmwareVersion, "unchanged", "No desired firmware version is set."));
        }

        if (requested.Contains("bad", StringComparison.OrdinalIgnoreCase) ||
            requested.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            var failed = new OtaResult(false, FirmwareVersion, "rolledBack", "Verification failed; retained previous firmware image.");
            return ReportAsync(failed, reportPatch);
        }

        FirmwareVersion = requested;
        var succeeded = new OtaResult(true, FirmwareVersion, "applied", "Downloaded, checksum-verified, applied, and reported.");
        return ReportAsync(succeeded, reportPatch);
    }

    private static async Task<OtaResult> ReportAsync(OtaResult result, Func<IReadOnlyDictionary<string, string?>, Task>? reportPatch)
    {
        if (reportPatch is not null)
        {
            await reportPatch(new Dictionary<string, string?>
            {
                ["firmwareVersion"] = result.ReportedFirmwareVersion,
                ["otaStatus"] = result.Status,
                ["otaDetail"] = result.Detail
            });
        }

        return result;
    }
}

public static class TelemetryWireFormat
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
