using Iiot.Application;
using Iiot.Broker;
using Iiot.Device;
using Iiot.Domain;
using Iiot.EdgeGateway;
using Iiot.Protocol;
using Microsoft.Extensions.Hosting;
using Gateway = Iiot.EdgeGateway.EdgeGateway;

namespace Iiot.Api;

public sealed class BrokerHostedService(ILogger<BrokerHostedService> logger) : IHostedService, IAsyncDisposable
{
    private readonly MqttBroker _broker = new(new MqttBrokerOptions { Port = 18830 });

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _broker.StartAsync(cancellationToken);
        logger.LogInformation("Hand-written MQTT 3.1.1 broker listening on TCP {Port}", _broker.Port);
    }

    public Task StopAsync(CancellationToken cancellationToken) => _broker.StopAsync();

    public ValueTask DisposeAsync() => _broker.DisposeAsync();
}

public sealed class DemoRuntimeHostedService(
    IServiceScopeFactory scopeFactory,
    GatewayNetworkControl network,
    ILogger<DemoRuntimeHostedService> logger) : BackgroundService
{
    private Gateway? _gateway;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var buffer = new SqliteEdgeBuffer("edge-demo-buffer.db", capacity: 2_000);
        var cloud = new ScopedCloudTelemetrySink(scopeFactory);
        var localRule = new RuleDefinition(
            "edge-vibration-safety",
            "cmp-01",
            RuleKind.Threshold,
            SensorMetric.VibrationMmPerSecondRms,
            RuleComparison.GreaterThanOrEqual,
            8m,
            0.5m,
            TimeSpan.Zero,
            TimeSpan.Zero);
        _gateway = new Gateway(buffer, cloud, new LocalRuleEvaluator([localRule]));
        _gateway.LocalAlertRaised += alert =>
        {
            logger.LogWarning("Edge safety alert {AlertId} for {DeviceId}: {Reason}", alert.AlertId, alert.DeviceId, alert.Reason);
            return Task.CompletedTask;
        };

        var devices = new[]
        {
            new SimulatedDevice("cmp-01", "compressor", "plant-a", "1.0.0", 10),
            new SimulatedDevice("chl-01", "chiller", "plant-a", "1.0.0", 20),
            new SimulatedDevice("tnk-01", "tank", "plant-b", "1.0.0", 30)
        };
        await using var localTransport = new InMemoryMessageTransport();
        await _gateway.StartAsync(localTransport, stoppingToken);
        var tick = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            _gateway.SetCloudReachable(network.IsOnline);
            if (tick == 20)
            {
                devices[0].Inject(new FaultInjection(SimulatedFault.BearingWear, DateTimeOffset.UtcNow));
            }

            foreach (var device in devices)
            {
                await device.PublishTelemetryAsync(localTransport, DateTimeOffset.UtcNow, stoppingToken);
            }

            if (network.IsOnline)
            {
                await _gateway.FlushAsync(stoppingToken);
            }

            network.SetBufferDepth(await buffer.CountAsync(stoppingToken));
            tick++;
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_gateway is not null)
        {
            await _gateway.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }

    private sealed class ScopedCloudTelemetrySink(IServiceScopeFactory scopeFactory) : ICloudTelemetrySink
    {
        public async Task<CloudIngestionReceipt> IngestAsync(IReadOnlyList<TelemetryReading> readings, CancellationToken cancellationToken = default)
        {
            using var scope = scopeFactory.CreateScope();
            var telemetry = scope.ServiceProvider.GetRequiredService<TelemetryIngestionService>();
            var alerts = scope.ServiceProvider.GetRequiredService<AlertRuleOrchestrator>();
            var devices = scope.ServiceProvider.GetRequiredService<IDeviceRegistryStore>();
            var result = await telemetry.IngestAsync(readings, cancellationToken);
            foreach (var reading in readings)
            {
                await alerts.EvaluateAsync(reading, cancellationToken);
            }
            foreach (var deviceId in readings.Select(item => item.DeviceId).Distinct(StringComparer.Ordinal))
            {
                await devices.UpdateDeviceStatusAsync(deviceId, DeviceStatus.Online, cancellationToken);
            }

            return new CloudIngestionReceipt(result.Accepted, result.Duplicates, result.Rejected);
        }
    }
}

public static class DemoSeed
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var registryStore = scope.ServiceProvider.GetRequiredService<IDeviceRegistryStore>();
        if (await registryStore.DeviceExistsAsync("cmp-01"))
        {
            return;
        }

        var registry = scope.ServiceProvider.GetRequiredService<DeviceRegistryService>();
        var telemetry = scope.ServiceProvider.GetRequiredService<TelemetryIngestionService>();
        var rules = scope.ServiceProvider.GetRequiredService<IRuleStore>();
        var now = DateTimeOffset.UtcNow;
        var devices = new[]
        {
            ("cmp-01", "compressor", "plant-a", "line-1", "air-compressor-01", "sim-cmp-key-0001"),
            ("chl-01", "chiller", "plant-a", "line-2", "process-chiller-01", "sim-chl-key-0001"),
            ("tnk-01", "tank", "plant-b", "line-1", "coolant-tank-01", "sim-tnk-key-0001")
        };
        foreach (var device in devices)
        {
            await registry.ProvisionAsync(
                new DeviceRegistration(device.Item1, device.Item2, device.Item3, device.Item4, device.Item5, "1.0.0", $"enrol-{device.Item1}"),
                device.Item6);
            var simulator = new SimulatedDevice(device.Item1, device.Item2, device.Item3, "1.0.0", device.Item1.GetHashCode(StringComparison.Ordinal));
            var samples = Enumerable.Range(0, 12)
                .Select(offset => simulator.Generate(now - TimeSpan.FromMinutes(11 - offset)))
                .ToArray();
            await telemetry.IngestAsync(samples);
            await telemetry.RecalculateRollupsAsync(device.Item1, now - TimeSpan.FromMinutes(15), now);
        }

        await rules.SaveRuleAsync(new RuleDefinition(
            "compressor-temperature",
            "cmp-01",
            RuleKind.Threshold,
            SensorMetric.TemperatureC,
            RuleComparison.GreaterThanOrEqual,
            75m,
            2m,
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero,
            SuppressionWindow: TimeSpan.FromMinutes(5)));
    }
}
