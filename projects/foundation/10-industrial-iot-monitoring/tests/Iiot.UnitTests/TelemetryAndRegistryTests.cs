using Iiot.Application;
using Iiot.Domain;

namespace Iiot.UnitTests;

public sealed class TelemetryAndRegistryTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RollupCalculator_OneMinuteBucket_ComputesMinMaxAverageCountAndStdDev()
    {
        var values = new[] { 10m, 20m, 30m };
        var readings = values.Select((temperature, index) => Reading(index, Start.AddSeconds(index * 10), temperature)).ToArray();

        var rollup = TelemetryRollupCalculator.Calculate(readings, TimeSpan.FromMinutes(1))
            .Single(item => item.Metric == SensorMetric.TemperatureC);

        Assert.Equal(10m, rollup.Min);
        Assert.Equal(30m, rollup.Max);
        Assert.Equal(20m, rollup.Average);
        Assert.Equal(3, rollup.Count);
        Assert.InRange(rollup.StandardDeviation, 8.16m, 8.17m);
    }

    [Fact]
    public void RollupCalculator_SuspectAndMissingSamples_ExcludesThem()
    {
        var good = Reading(1, Start, 20m);
        var missing = Reading(2, Start.AddSeconds(10), 90m) with { Quality = QualityFlag.Missing };

        var rollup = TelemetryRollupCalculator.Calculate([good, missing], TimeSpan.FromMinutes(1))
            .Single(item => item.Metric == SensorMetric.TemperatureC);

        Assert.Equal(20m, rollup.Average);
        Assert.Equal(1, rollup.Count);
    }

    [Fact]
    public async Task Ingestion_RepeatedDeviceSequence_IsIdempotent()
    {
        var store = new MemoryTelemetryStore();
        var service = new TelemetryIngestionService(store, new FakeClock(Start));
        var reading = Reading(9, Start, 30m);

        var result = await service.IngestAsync([reading, reading]);
        var persisted = await store.QueryTelemetryAsync("cmp-01", Start.AddMinutes(-1), Start.AddMinutes(1));

        Assert.Equal(1, result.Accepted);
        Assert.Equal(1, result.Duplicates);
        Assert.Single(persisted);
    }

    [Fact]
    public async Task Ingestion_InvalidPhysicalReading_IsRejectedWithoutPersisting()
    {
        var store = new MemoryTelemetryStore();
        var service = new TelemetryIngestionService(store, new FakeClock(Start));
        var invalid = Reading(1, Start, 20m) with
        {
            Values = Reading(1, Start, 20m).Values with { TankLevelPercent = 101m }
        };

        var result = await service.IngestAsync([invalid]);

        Assert.Equal(1, result.Rejected);
        Assert.Empty(await store.QueryTelemetryAsync("cmp-01", Start.AddMinutes(-1), Start.AddMinutes(1)));
    }

    [Fact]
    public async Task Retention_WithFakeClock_PrunesOnlyExpiredRawTelemetry()
    {
        var store = new MemoryTelemetryStore();
        var clock = new FakeClock(Start.AddHours(2));
        var service = new TelemetryIngestionService(store, clock);
        await service.IngestAsync([Reading(1, Start, 20m), Reading(2, Start.AddHours(1).AddMinutes(30), 21m)]);

        var removed = await service.ApplyRawRetentionAsync(TimeSpan.FromHours(1));
        var remaining = await store.QueryTelemetryAsync("cmp-01", Start, Start.AddHours(3));

        Assert.Equal(1, removed);
        Assert.Single(remaining);
        Assert.Equal(2, remaining[0].Sequence);
    }

    [Fact]
    public async Task RetentionPolicy_WithFakeClock_PrunesMinuteTierBeforeHourTier()
    {
        var store = new MemoryTelemetryStore();
        var clock = new FakeClock(Start);
        var service = new TelemetryIngestionService(store, clock);
        await service.IngestAsync([Reading(1, Start, 20m)]);
        await service.RecalculateRollupsAsync("cmp-01", Start.AddMinutes(-1), Start.AddMinutes(1));
        clock.Advance(TimeSpan.FromDays(2));

        var removed = await service.ApplyRetentionAsync(new TelemetryRetentionPolicy(
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(24),
            TimeSpan.FromDays(7)));
        var hours = await store.QueryRollupsAsync(
            "cmp-01",
            SensorMetric.TemperatureC,
            TimeSpan.FromHours(1),
            Start.AddHours(-1),
            Start.AddDays(3));

        Assert.Equal(1, removed.Raw);
        Assert.Equal(6, removed.Minute);
        Assert.Equal(0, removed.Hour);
        Assert.Single(hours);
    }

    [Fact]
    public async Task DeviceRegistry_ValidKey_IsAccepted()
    {
        var (service, _, _) = CreateRegistry();
        await ProvisionAsync(service);

        Assert.True(await service.AuthenticateDeviceAsync("cmp-01", "device-key-12345"));
    }

    [Fact]
    public async Task DeviceRegistry_InvalidKey_IsRejected()
    {
        var (service, _, _) = CreateRegistry();
        await ProvisionAsync(service);

        Assert.False(await service.AuthenticateDeviceAsync("cmp-01", "not-the-key-12345"));
    }

    [Fact]
    public async Task DeviceRegistry_RevokedDevice_IsRejected()
    {
        var (service, _, _) = CreateRegistry();
        await ProvisionAsync(service);
        await service.RevokeAsync("cmp-01");

        Assert.False(await service.AuthenticateDeviceAsync("cmp-01", "device-key-12345"));
    }

    [Fact]
    public async Task DeviceRegistry_EnrollmentToken_TransitionsDeviceToOffline()
    {
        var (service, _, _) = CreateRegistry();
        await ProvisionAsync(service);

        var enrolled = await service.EnrollAsync("cmp-01", "enrollment-token");

        Assert.Equal(DeviceStatus.Offline, enrolled.Status);
    }

    [Fact]
    public async Task DeviceTwin_DesiredAndReportedPatches_IncrementVersionAndRemoveNulls()
    {
        var (service, store, _) = CreateRegistry();
        await ProvisionAsync(service);
        var desired = await service.PatchTwinAsync(
            "cmp-01",
            new Dictionary<string, string?> { ["firmwareVersion"] = "1.1.0", ["samplingSeconds"] = "15" },
            1,
            false);
        var reported = await service.PatchTwinAsync(
            "cmp-01",
            new Dictionary<string, string?> { ["otaStatus"] = "applied", ["samplingSeconds"] = null },
            desired.Version,
            true);

        Assert.Equal(3, reported.Version);
        Assert.Equal("1.1.0", reported.Desired["firmwareVersion"]);
        Assert.False(reported.Reported.ContainsKey("samplingSeconds"));
        Assert.Equal("applied", (await store.GetTwinAsync("cmp-01"))!.Reported["otaStatus"]);
    }

    [Fact]
    public async Task DeviceTwin_StaleVersion_RejectsPatch()
    {
        var (service, _, _) = CreateRegistry();
        await ProvisionAsync(service);
        await service.PatchTwinAsync("cmp-01", new Dictionary<string, string?> { ["mode"] = "eco" }, 1, false);

        await Assert.ThrowsAsync<DomainRuleViolation>(() =>
            service.PatchTwinAsync("cmp-01", new Dictionary<string, string?> { ["mode"] = "turbo" }, 1, false));
    }

    private static (DeviceRegistryService Service, MemoryDeviceStore Store, FakeClock Clock) CreateRegistry()
    {
        var store = new MemoryDeviceStore();
        var clock = new FakeClock(Start);
        return (new DeviceRegistryService(store, new FixedKeyProtector(), clock), store, clock);
    }

    private static Task ProvisionAsync(DeviceRegistryService service) =>
        service.ProvisionAsync(
            new DeviceRegistration("cmp-01", "compressor", "plant-a", "line-1", "air-compressor-01", "1.0.0", "enrollment-token"),
            "device-key-12345");

    internal static TelemetryReading Reading(long sequence, DateTimeOffset at, decimal temperature = 58m, decimal vibration = 2m) =>
        new(
            "cmp-01",
            sequence,
            at,
            new TelemetryValues(temperature, vibration, 7.4m, 26m, 185m, 64m, MachineState.Running),
            QualityFlag.Good);
}
