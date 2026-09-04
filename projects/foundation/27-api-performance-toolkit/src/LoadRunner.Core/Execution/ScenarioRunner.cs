using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using LoadRunner.Core.Analysis;
using LoadRunner.Core.Assertions;
using LoadRunner.Core.Http;
using LoadRunner.Core.LoadModels;
using LoadRunner.Core.Metrics;
using LoadRunner.Core.Results;
using LoadRunner.Core.Scenarios;
using LoadRunner.Core.Time;

namespace LoadRunner.Core.Execution;

public sealed class ScenarioRunner
{
    private readonly HttpClient _httpClient;
    private readonly IClock _clock;

    public ScenarioRunner(HttpClient? httpClient = null, IClock? clock = null)
    {
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            UseProxy = false,
            EnableMultipleHttp2Connections = true,
            MaxConnectionsPerServer = int.MaxValue,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestVersion = HttpVersion.Version11,
        };
        _clock = clock ?? new SystemClock();
    }

    public async Task<RunResult> RunAsync(ScenarioDefinition scenario, CancellationToken cancellationToken = default)
    {
        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(scenario.BaseUrl.TrimEnd('/') + "/");
        var executor = new HttpRequestExecutor(_httpClient);
        return await RunWithExecutorAsync(scenario, executor, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunResult> RunWithExecutorAsync(
        ScenarioDefinition scenario,
        IHttpRequestExecutor executor,
        CancellationToken cancellationToken = default)
    {
        ScenarioLoader.Validate(scenario);
        var metrics = new MetricsCollector();
        CsvFeeder? feeder = null;
        if (scenario.Feeder is not null && File.Exists(scenario.Feeder.Path))
            feeder = CsvFeeder.FromFile(scenario.Feeder.Path, scenario.Feeder.Cycle);

        var runStart = _clock.UtcNow;
        var runContext = new RunContext(scenario, executor, metrics, _clock, feeder, runStart);

        var stressSteps = new List<StressStepResult>();
        KneeDetector.KneeResult? knee = null;
        CapacityBinarySearch.CapacityResult? capacityResult = null;

        try
        {
            switch (scenario.Load.Model)
            {
                case LoadModel.ConstantVUs:
                    await new ClosedModel().RunAsync(runContext, cancellationToken).ConfigureAwait(false);
                    break;
                case LoadModel.RampingVUs:
                    await new RampingClosedModel().RunAsync(runContext, cancellationToken).ConfigureAwait(false);
                    break;
                case LoadModel.ConstantArrivalRate:
                    await new OpenModel().RunAsync(runContext, cancellationToken).ConfigureAwait(false);
                    break;
                case LoadModel.RampingArrivalRate:
                    await new RampingOpenModel().RunAsync(runContext, cancellationToken).ConfigureAwait(false);
                    break;
                case LoadModel.Spike:
                    await RunSpikeAsync(runContext, cancellationToken).ConfigureAwait(false);
                    break;
                case LoadModel.Soak:
                    await new ClosedModel().RunAsync(runContext, cancellationToken).ConfigureAwait(false);
                    break;
                case LoadModel.Stress:
                    (stressSteps, knee) = await RunStressAsync(runContext, cancellationToken).ConfigureAwait(false);
                    break;
                case LoadModel.CapacitySearch:
                    capacityResult = await RunCapacitySearchAsync(runContext, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await new ClosedModel().RunAsync(runContext, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) { }

        var runEnd = _clock.UtcNow;
        var snapshot = metrics.Snapshot(runStart, runEnd, scenario.Load.WarmUp);

        SoakDrift? drift = null;
        if (scenario.Load.Model == LoadModel.Soak)
            drift = SoakDriftDetector.Detect(snapshot.TimeSeries);

        var assertionResults = scenario.Assertions is null
            ? Array.Empty<AssertionResult>()
            : ThresholdEvaluator.Evaluate(scenario.Assertions, snapshot);

        var assertionRecords = assertionResults
            .Select(r => new AssertionRecord(r.Spec.Metric, r.Spec.Op, r.Spec.Value, r.Actual, r.Passed))
            .ToArray();

        var env = BuildEnvironmentInfo();
        var runId = $"{scenario.Name.Replace(' ', '_')}-{runStart:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

        return new RunResult(
            runId,
            scenario.Name,
            runStart,
            runEnd,
            snapshot.Warmup,
            env,
            scenario,
            snapshot.Aggregate,
            snapshot.PerStep,
            snapshot.TimeSeries,
            assertionRecords,
            snapshot.OmittedWarmupSamples,
            drift,
            capacityResult,
            stressSteps.Count == 0 ? null : stressSteps,
            knee);
    }

    private static async Task RunSpikeAsync(RunContext context, CancellationToken cancellationToken)
    {
        var plan = context.Scenario.Load;
        var baseVUs = plan.SpikeBaseVUs ?? 1;
        var peakVUs = plan.SpikePeakVUs ?? baseVUs * 5;
        var hold = plan.SpikeHoldDuration ?? TimeSpan.FromSeconds(5);
        var duration = plan.Duration ?? TimeSpan.FromSeconds(15);

        var stages = new List<LoadStage>
        {
            new(baseVUs, TimeSpan.FromMilliseconds(Math.Max(500, (duration - hold).TotalMilliseconds / 3))),
            new(peakVUs, TimeSpan.FromMilliseconds(Math.Max(200, hold.TotalMilliseconds / 4))),
            new(peakVUs, hold),
            new(baseVUs, TimeSpan.FromMilliseconds(Math.Max(200, hold.TotalMilliseconds / 4))),
        };
        var spikeScenario = context.Scenario with
        {
            Load = plan with { Model = LoadModel.RampingVUs, Stages = stages, MaxVUs = peakVUs }
        };
        var spikeContext = context with { Scenario = spikeScenario };
        await new RampingClosedModel().RunAsync(spikeContext, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(List<StressStepResult>, KneeDetector.KneeResult)> RunStressAsync(
        RunContext context, CancellationToken cancellationToken)
    {
        var plan = context.Scenario.Load;
        var startRate = plan.StressStartRate ?? 5;
        var step = plan.StressStepRate ?? 5;
        var stepDuration = plan.StressStepDuration ?? TimeSpan.FromSeconds(3);
        var maxRate = plan.StressMaxRate ?? (startRate + step * 10);
        var kneeP95 = plan.StressKneeP95Ms;
        var kneeErr = plan.StressKneeErrorRate;

        var results = new List<StressStepResult>();
        var runStart = context.RunStartedUtc;
        for (var rate = startRate; rate <= maxRate; rate += step)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var stepCollector = new MetricsCollector();
            var stepContext = context with { Metrics = stepCollector };
            await new OpenModel().RunAtRateAsync(stepContext, rate, stepDuration, cancellationToken).ConfigureAwait(false);
            var snap = stepCollector.Snapshot(runStart, context.Clock.UtcNow);
            var p95 = snap.Aggregate.Service.P95Ms;
            var errRate = snap.Aggregate.ErrorRate;
            results.Add(new StressStepResult(rate, p95, errRate, snap.Aggregate.Count));
            // fold into main collector
            foreach (var s in stepCollector.Samples) context.Metrics.Record(s);
            var kneeCross = (kneeP95.HasValue && p95 > kneeP95.Value)
                            || (kneeErr.HasValue && errRate > kneeErr.Value);
            if (kneeCross) break;
        }
        var knee = KneeDetector.Detect(results, kneeP95, kneeErr);
        return (results, knee);
    }

    private static async Task<CapacityBinarySearch.CapacityResult> RunCapacitySearchAsync(
        RunContext context, CancellationToken cancellationToken)
    {
        var plan = context.Scenario.Load;
        var minRate = plan.CapacityMinRate ?? 10;
        var maxRate = plan.CapacityMaxRate ?? (minRate * 20);
        var runDuration = plan.CapacityRunDuration ?? TimeSpan.FromSeconds(3);
        var p95Target = plan.CapacityP95TargetMs ?? 500;
        var tolerance = Math.Max(1, (maxRate - minRate) / 20);

        var searcher = new CapacityBinarySearch();
        return await searcher.SearchAsync(minRate, maxRate, tolerance, p95Target, 0.05,
            async (rate, ct) =>
            {
                var stepCollector = new MetricsCollector();
                var stepContext = context with { Metrics = stepCollector };
                await new OpenModel().RunAtRateAsync(stepContext, rate, runDuration, ct).ConfigureAwait(false);
                var snap = stepCollector.Snapshot(context.RunStartedUtc, context.Clock.UtcNow);
                foreach (var s in stepCollector.Samples) context.Metrics.Record(s);
                return new CapacityBinarySearch.ProbeOutcome(snap.Aggregate.Service.P95Ms, snap.Aggregate.ErrorRate);
            }, cancellationToken).ConfigureAwait(false);
    }

    public static EnvironmentInfo BuildEnvironmentInfo(string? gitCommit = null)
    {
        var toolkitVersion = FileVersionInfo.GetVersionInfo(typeof(ScenarioRunner).Assembly.Location).ProductVersion
                              ?? typeof(ScenarioRunner).Assembly.GetName().Version?.ToString()
                              ?? "0.0.0";
        return new EnvironmentInfo(
            RuntimeInformation.OSDescription,
            Environment.ProcessorCount,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.MachineName,
            gitCommit,
            toolkitVersion);
    }
}
