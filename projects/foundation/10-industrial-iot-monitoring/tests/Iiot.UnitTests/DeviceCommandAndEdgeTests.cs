using System.IO.Compression;
using System.Text.Json;
using Iiot.Application;
using Iiot.Device;
using Iiot.Domain;
using Iiot.EdgeGateway;
using Gateway = Iiot.EdgeGateway.EdgeGateway;

namespace Iiot.UnitTests;

public sealed class DeviceCommandAndEdgeTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DeviceSimulator_NormalSignal_IsPhysicallyPlausible()
    {
        var device = new SimulatedDevice("cmp-01", "compressor", "plant-a", "1.0.0", 1);

        var reading = device.Generate(Start);

        Assert.InRange(reading.Values.TemperatureC, 50m, 66m);
        Assert.InRange(reading.Values.VibrationMmPerSecondRms, 1m, 4m);
        Assert.InRange(reading.Values.PressureBar, 7m, 8m);
        Assert.Equal(QualityFlag.Good, reading.Quality);
    }

    [Fact]
    public void DeviceSimulator_BearingWear_RampsVibration()
    {
        var device = Faulted(SimulatedFault.BearingWear);

        var reading = device.Generate(Start.AddMinutes(100));

        Assert.True(reading.Values.VibrationMmPerSecondRms > 10m);
    }

    [Fact]
    public void DeviceSimulator_Overheating_RampsTemperature()
    {
        var device = Faulted(SimulatedFault.Overheating);

        var reading = device.Generate(Start.AddMinutes(100));

        Assert.True(reading.Values.TemperatureC > 85m);
    }

    [Fact]
    public void DeviceSimulator_SensorStuckAt_RepeatsValues()
    {
        var device = Faulted(SimulatedFault.SensorStuckAt);
        var first = device.Generate(Start);
        var second = device.Generate(Start.AddMinutes(10));

        Assert.Equal(first.Values, second.Values);
        Assert.Equal(QualityFlag.Suspect, second.Quality);
    }

    [Fact]
    public void DeviceSimulator_SensorDrift_ChangesPressure()
    {
        var device = Faulted(SimulatedFault.SensorDrift);

        var reading = device.Generate(Start.AddMinutes(100));

        Assert.True(reading.Values.PressureBar > 9m);
    }

    [Fact]
    public void DeviceSimulator_Dropout_ReportsMissingQuality()
    {
        var device = Faulted(SimulatedFault.Dropout);

        var reading = device.Generate(Start.AddMinutes(1));

        Assert.Equal(QualityFlag.Missing, reading.Quality);
    }

    [Fact]
    public void DeviceSimulator_Spike_ReportsSuspectHighValues()
    {
        var device = Faulted(SimulatedFault.Spike);

        var reading = device.Generate(Start.AddMinutes(1));

        Assert.True(reading.Values.TemperatureC > 80m);
        Assert.Equal(QualityFlag.Suspect, reading.Quality);
    }

    [Fact]
    public async Task OtaDesiredFirmware_ValidImage_AppliesAndReports()
    {
        var device = new SimulatedDevice("cmp-01", "compressor", "plant-a", "1.0.0");
        var twin = new DeviceTwin();
        twin.PatchDesired(new Dictionary<string, string?> { ["firmwareVersion"] = "1.1.0" }, 1);
        Dictionary<string, string?>? report = null;

        var result = await device.ApplyDesiredFirmwareAsync(twin.Snapshot(), patch =>
        {
            report = new Dictionary<string, string?>(patch);
            return Task.CompletedTask;
        });

        Assert.True(result.Succeeded);
        Assert.Equal("1.1.0", device.FirmwareVersion);
        Assert.Equal("applied", report!["otaStatus"]);
    }

    [Fact]
    public async Task OtaDesiredFirmware_FailedVerification_RollsBack()
    {
        var device = new SimulatedDevice("cmp-01", "compressor", "plant-a", "1.0.0");
        var twin = new DeviceTwin();
        twin.PatchDesired(new Dictionary<string, string?> { ["firmwareVersion"] = "bad-1.1.0" }, 1);

        var result = await device.ApplyDesiredFirmwareAsync(twin.Snapshot());

        Assert.False(result.Succeeded);
        Assert.Equal("1.0.0", device.FirmwareVersion);
        Assert.Equal("rolledBack", result.Status);
    }

    [Fact]
    public void CommandSchemas_ArbitraryCommand_IsRejected()
    {
        var request = new CommandRequest("cmp-01", "runShell", new Dictionary<string, string> { ["script"] = "rm -rf" }, "operator", "c", TimeSpan.FromSeconds(5));

        Assert.Throws<DomainRuleViolation>(() => CommandSchemas.Validate("compressor", request));
    }

    [Fact]
    public void RemoteCommand_ValidStateProgression_Completes()
    {
        var command = new RemoteCommand("cmd", new CommandRequest("cmp-01", "restart", new Dictionary<string, string>(), "operator", "c", TimeSpan.FromSeconds(5)), Start);
        command.MarkSent(Start.AddSeconds(1));
        command.MarkAcked(Start.AddSeconds(2));
        command.MarkCompleted(Start.AddSeconds(3));

        Assert.Equal(CommandStatus.Completed, command.Status);
        Assert.Equal(Start.AddSeconds(3), command.CompletedAt);
    }

    [Fact]
    public void RemoteCommand_InvalidStateTransition_Throws()
    {
        var command = new RemoteCommand("cmd", new CommandRequest("cmp-01", "restart", new Dictionary<string, string>(), "operator", "c", TimeSpan.FromSeconds(5)), Start);

        Assert.Throws<DomainRuleViolation>(() => command.MarkCompleted(Start));
    }

    [Fact]
    public async Task CommandService_ExpiredQueuedCommand_TimesOutAndAudits()
    {
        var deviceStore = new MemoryDeviceStore();
        var registry = new DeviceRegistryService(deviceStore, new FixedKeyProtector(), new FakeClock(Start));
        await registry.ProvisionAsync(new DeviceRegistration("cmp-01", "compressor", "plant-a", "line-1", "asset", "1.0.0", "enrol"), "device-key-12345");
        var clock = new FakeClock(Start);
        var commandStore = new MemoryCommandStore();
        var service = new CommandService(commandStore, deviceStore, clock);
        var queued = await service.QueueAsync(new CommandRequest("cmp-01", "restart", new Dictionary<string, string>(), "operator", "corr", TimeSpan.FromSeconds(10)));

        clock.Advance(TimeSpan.FromSeconds(11));
        var expired = await service.TimeoutExpiredAsync();
        var persisted = await commandStore.FindCommandAsync(queued.CommandId);

        Assert.Equal(1, expired);
        Assert.Equal(CommandStatus.TimedOut, persisted!.Status);
        Assert.Equal(2, (await commandStore.ListAuditAsync(queued.CommandId)).Count);
    }

    [Fact]
    public async Task CommandService_UnacknowledgedSentCommand_CanRetryBeforeTimeout()
    {
        var deviceStore = new MemoryDeviceStore();
        var clock = new FakeClock(Start);
        var registry = new DeviceRegistryService(deviceStore, new FixedKeyProtector(), clock);
        await registry.ProvisionAsync(new DeviceRegistration("cmp-01", "compressor", "plant-a", "line-1", "asset", "1.0.0", "enrol"), "device-key-12345");
        var commands = new MemoryCommandStore();
        var service = new CommandService(commands, deviceStore, clock);
        var queued = await service.QueueAsync(new CommandRequest("cmp-01", "restart", new Dictionary<string, string>(), "operator", "corr", TimeSpan.FromMinutes(1)));
        await service.TransitionAsync(queued.CommandId, CommandStatus.Sent, "gateway");

        var retried = await service.RetryAsync(queued.CommandId, "gateway");

        Assert.Equal(CommandStatus.Sent, retried.Status);
        Assert.Equal(2, retried.Attempts);
        Assert.Equal(3, (await commands.ListAuditAsync(queued.CommandId)).Count);
    }

    [Fact]
    public async Task EdgeGateway_OfflineThenOnline_ReplaysAllMessagesInOrderExactlyOnceEffectively()
    {
        var path = BufferPath();
        try
        {
            var buffer = new SqliteEdgeBuffer(path, 20);
            var cloud = new CollectingCloudSink();
            await using var gateway = new Gateway(buffer, cloud);
            gateway.SetCloudReachable(false);
            for (var index = 0; index < 10; index++)
            {
                await gateway.HandleTelemetryAsync(TelemetryAndRegistryTests.Reading(index, Start.AddSeconds(index)));
            }

            Assert.Equal(10, await buffer.CountAsync());
            gateway.SetCloudReachable(true);
            await gateway.FlushAsync();

            Assert.Equal(Enumerable.Range(0, 10).Select(index => (long)index), cloud.Delivered.Select(item => item.Sequence));
            Assert.Equal(0, await buffer.CountAsync());
            Console.WriteLine("Edge buffering measurement: produced=10, replayed=10, duplicate deliveries=0, order=preserved, final buffer depth=0.");
        }
        finally
        {
            DeleteBuffer(path);
        }
    }

    [Fact]
    public async Task EdgeGateway_CapEviction_DropsOldestBufferedMessages()
    {
        var path = BufferPath();
        try
        {
            var buffer = new SqliteEdgeBuffer(path, 3);
            await using var gateway = new Gateway(buffer, new CollectingCloudSink());
            gateway.SetCloudReachable(false);
            for (var index = 0; index < 5; index++)
            {
                await gateway.HandleTelemetryAsync(TelemetryAndRegistryTests.Reading(index, Start.AddSeconds(index)));
            }

            var buffered = await buffer.ReadBatchAsync(10);

            Assert.Equal(3, buffered.Count);
            Assert.Equal(new long[] { 2, 3, 4 }, buffered.Select(item => item.Reading.Sequence));
        }
        finally
        {
            DeleteBuffer(path);
        }
    }

    [Fact]
    public async Task EdgeGateway_CloudFailure_BuffersThenRecovers()
    {
        var path = BufferPath();
        try
        {
            var buffer = new SqliteEdgeBuffer(path, 10);
            var cloud = new CollectingCloudSink { ThrowWhenCalled = true };
            await using var gateway = new Gateway(buffer, cloud);

            await gateway.HandleTelemetryAsync(TelemetryAndRegistryTests.Reading(1, Start));
            Assert.False(gateway.IsCloudReachable);
            Assert.Equal(1, await buffer.CountAsync());

            cloud.ThrowWhenCalled = false;
            gateway.SetCloudReachable(true);
            await gateway.FlushAsync();

            Assert.Single(cloud.Delivered);
            Assert.Equal(0, await buffer.CountAsync());
        }
        finally
        {
            DeleteBuffer(path);
        }
    }

    [Fact]
    public async Task EdgeGateway_OfflineLocalRule_RaisesSafetyAlert()
    {
        var path = BufferPath();
        try
        {
            var rule = new RuleDefinition("safety", "cmp-01", RuleKind.Threshold, SensorMetric.TemperatureC, RuleComparison.GreaterThanOrEqual, 75m, 0m, TimeSpan.Zero, TimeSpan.Zero);
            var buffer = new SqliteEdgeBuffer(path, 10);
            await using var gateway = new Gateway(buffer, new CollectingCloudSink(), new LocalRuleEvaluator([rule]));
            var alerts = new List<Alert>();
            gateway.LocalAlertRaised += alert =>
            {
                alerts.Add(alert);
                return Task.CompletedTask;
            };
            gateway.SetCloudReachable(false);

            await gateway.HandleTelemetryAsync(TelemetryAndRegistryTests.Reading(1, Start, 80m));

            Assert.Single(alerts);
            Assert.Equal(1, await buffer.CountAsync());
        }
        finally
        {
            DeleteBuffer(path);
        }
    }

    [Fact]
    public void EdgeCompression_GzipRoundTripsTelemetryBatch()
    {
        var input = new[] { TelemetryAndRegistryTests.Reading(1, Start) };
        var compressed = EdgeCompression.CompressJson(input);
        using var source = new MemoryStream(compressed);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        var output = JsonSerializer.Deserialize<TelemetryReading[]>(gzip, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        });

        var readings = Assert.IsType<TelemetryReading[]>(output);
        Assert.Single(readings);
        Assert.Equal(input[0].DeviceId, readings[0].DeviceId);
    }

    private static SimulatedDevice Faulted(SimulatedFault fault)
    {
        var device = new SimulatedDevice("cmp-01", "compressor", "plant-a", "1.0.0", 7);
        device.Inject(new FaultInjection(fault, Start));
        return device;
    }

    private static string BufferPath() => Path.Combine(Directory.GetCurrentDirectory(), $"edge-test-{Guid.NewGuid():N}.db");

    private static void DeleteBuffer(string path)
    {
        foreach (var file in new[] { path, $"{path}-shm", $"{path}-wal" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
