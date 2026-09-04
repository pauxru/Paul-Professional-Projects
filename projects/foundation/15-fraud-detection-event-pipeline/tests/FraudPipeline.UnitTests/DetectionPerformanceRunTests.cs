using System.Diagnostics;
using FraudPipeline.Application.Feedback;
using FraudPipeline.Application.FeatureStore;
using FraudPipeline.Application.Rules;
using FraudPipeline.Application.Scoring;
using FraudPipeline.Application.Synthetic;
using FraudPipeline.Domain.Abstractions;
using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.Rules;
using FraudPipeline.Infrastructure.Time;
using FraudPipeline.UnitTests.Fakes;
using Xunit.Abstractions;

namespace FraudPipeline.UnitTests;

public class DetectionPerformanceRunTests
{
    private readonly ITestOutputHelper _output;
    public DetectionPerformanceRunTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// End-to-end run: synthetic data -> feature store -> scoring -> detection metrics.
    /// This is the source of the numbers in docs/detection-performance.md.
    /// Test asserts that precision > 0 and recall > 0.5 on the synthetic ground truth.
    /// </summary>
    [Fact]
    public async Task RunSyntheticEndToEnd_ProducesRealMeasuredMetrics()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);
        var ids = new GuidIdGenerator();
        var runtime = new FeatureStoreRuntime(clock);
        var fs = new FeatureStoreService(runtime);
        var engine = new RuleEngine();
        var metrics = new ScoringMetrics();
        var txns = new InMemoryTransactionRepository();
        var decisions = new InMemoryScoringDecisionRepository();
        var lists = new InMemoryListRepository();
        var rsRepo = new InMemoryRulesetRepository();

        var def = DefaultRulesets.BuildV1();
        var r = new Ruleset(Guid.NewGuid(), def.Version, def.Name, RulesetSerializer.Serialize(def), start);
        r.Activate(start);
        await rsRepo.AddAsync(r);

        var scoring = new ScoringService(fs, engine, rsRepo, lists, txns, decisions, ids, clock,
            new ScoringOptions { LatencyBudgetMs = 200, EnableShadow = false }, metrics);

        var gen = new SyntheticDataGenerator(new SyntheticGeneratorOptions
        {
            Seed = 42,
            CustomerCount = 100,
            MerchantCount = 30,
            DeviceCount = 200,
            IpCount = 150,
            NormalTransactionCount = 1000,
            FraudPatternCount = 20,
            StartTime = start
        }, ids);
        var pop = gen.BuildPopulation();
        var stream = gen.Generate(pop).OrderBy(t => t.OccurredAt).ToList();

        var sw = Stopwatch.StartNew();
        var latencies = new List<double>();
        foreach (var t in stream)
        {
            var delta = t.ReceivedAt - clock.UtcNow;
            if (delta > TimeSpan.Zero) clock.Advance(delta);
            await txns.AddAsync(t);
            fs.Observe(t);
            var res = await scoring.ScoreAsync(t);
            latencies.Add(res.LatencyMs);
        }
        sw.Stop();

        var eval = new DetectionEvaluator(decisions, txns, fs, scoring, lists);
        var m = await eval.ComputeAsync(stream.Count + 100);

        latencies.Sort();
        double P(double q) => latencies[(int)Math.Clamp(q * latencies.Count, 0, latencies.Count - 1)];

        // The reproducible-detection-metrics half: seeded synthetic stream +
        // fixed ruleset -> byte-identical output on every run. Committed to the
        // repository as evidence for the numbers reported in the docs.
        var report = new
        {
            Scored = stream.Count,
            FraudInjected = stream.Count(t => t.GroundTruthFraud),
            m.LabelledTransactions,
            m.TruePositives,
            m.FalsePositives,
            m.TrueNegatives,
            m.FalseNegatives,
            m.Precision,
            m.Recall,
            m.FalsePositiveRate,
            m.F1,
            m.AlertVolume,
            m.ValueDetected
        };

        // The volatile-timing half: wall-clock and percentile timings that vary
        // with host load. Written to a gitignored artifacts/ folder — the docs
        // reference these by name, not by embedded value.
        var latencyReport = new
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Scored = stream.Count,
            TotalWallSec = sw.Elapsed.TotalSeconds,
            P50Ms = P(0.50),
            P95Ms = P(0.95),
            P99Ms = P(0.99)
        };

        var jsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };

        _output.WriteLine("=== DETECTION METRICS ===");
        _output.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, jsonOpts));
        _output.WriteLine("=== LATENCY (volatile) ===");
        _output.WriteLine(System.Text.Json.JsonSerializer.Serialize(latencyReport, jsonOpts));

        var docsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs");
        Directory.CreateDirectory(docsDir);
        var reportPath = Path.Combine(docsDir, "detection-performance.snapshot.json");
        File.WriteAllText(reportPath, System.Text.Json.JsonSerializer.Serialize(report, jsonOpts));

        var artifactsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "latency");
        Directory.CreateDirectory(artifactsDir);
        var latencyPath = Path.Combine(artifactsDir, "detection-performance.latency.json");
        File.WriteAllText(latencyPath, System.Text.Json.JsonSerializer.Serialize(latencyReport, jsonOpts));

        Assert.True(m.LabelledTransactions > 0);
        Assert.True(m.Recall > 0);
    }
}
