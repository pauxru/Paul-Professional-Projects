using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using FraudPipeline.Application.Feedback;
using FraudPipeline.Application.FeatureStore;
using FraudPipeline.Application.Rules;
using FraudPipeline.Application.Scoring;
using FraudPipeline.Application.Synthetic;
using FraudPipeline.Domain.Abstractions;
using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.Rules;
using FraudPipeline.Domain.ValueObjects;
using FraudPipeline.Infrastructure.Time;
using FraudPipeline.UnitTests.Fakes;
using Xunit.Abstractions;

namespace FraudPipeline.UnitTests;

/// <summary>
/// Champion / challenger benchmark. Runs the same seeded synthetic stream through
/// BOTH the default (v1.0.0) and the tuned (v1.1.0) rulesets, plus a threshold
/// sweep on v1.1.0, and writes the results to two well-separated locations:
///
///   docs/detection-performance-v1_0.snapshot.json    - baseline detection metrics (committed)
///   docs/detection-performance-v1_1.snapshot.json    - challenger detection metrics (committed)
///   docs/detection-performance-sweep.snapshot.json   - band-threshold sweep detection metrics (committed)
///
///   artifacts/latency/detection-performance-v1_0.latency.json    - baseline wall + percentiles (gitignored)
///   artifacts/latency/detection-performance-v1_1.latency.json    - challenger wall + percentiles (gitignored)
///   artifacts/latency/detection-performance-sweep.latency.json   - sweep wall + percentiles (gitignored)
///
/// The split is deliberate: detection metrics are byte-for-byte reproducible from the
/// seeded synthetic stream + fixed ruleset and belong in version control as evidence
/// that the documented numbers were not fabricated. Wall-clock timing is environmental —
/// it varies with host load — and does not belong in a committed snapshot, so it goes
/// to a gitignored artifacts/ folder that the docs reference by name.
///
/// The test asserts that v1.1.0 clears the operating-point targets set in the
/// project brief (recall &gt;= 0.55 at FPR &lt;= 0.03). If these ever regress,
/// this test fails, which is the point. It does NOT assert on timing — the p99
/// budget is enforced at scoring time by <see cref="ScoringService"/>, not here.
/// </summary>
public class DetectionBenchmarkRunTests
{
    private readonly ITestOutputHelper _output;
    public DetectionBenchmarkRunTests(ITestOutputHelper output) => _output = output;

    private static readonly DateTimeOffset _start =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ChampionChallenger_ProducesRealMeasuredMetrics()
    {
        // Generate the shared stream once, run all rulesets against the identical
        // sequence so metrics are comparable.
        var pop = BuildPopulation();
        var stream = GenerateStream(pop);

        var baseline = await RunAgainst(stream, DefaultRulesets.BuildV1());
        var challenger = await RunAgainst(stream, DefaultRulesets.BuildV1Challenger());

        var sweepPoints = new (int approveMax, int stepUpMax, int reviewMax, string label)[]
        {
            (250, 500, 800, "default"),
            (250, 400, 700, "tighter-stepup"),
            (200, 400, 700, "tighter-approve"),
            (200, 350, 650, "aggressive"),
            (300, 550, 850, "looser"),
            (150, 350, 600, "very-tight")
        };
        var sweep = new List<(PatternedRunReport metrics, LatencyReport latency)>();
        foreach (var (a, s, r, label) in sweepPoints)
        {
            var def = DefaultRulesets.BuildV1Challenger();
            var tuned = def with { Bands = new RiskBands(a, s, r) };
            var report = await RunAgainst(stream, tuned);
            var labelled = $"v1.1@{label}(a={a},s={s},r={r})";
            sweep.Add((report.metrics with { Label = labelled }, report.latency with { Label = labelled }));
        }

        var docsDir = ResolveDocsDir();
        var artifactsDir = ResolveLatencyArtifactsDir();
        Directory.CreateDirectory(docsDir);
        Directory.CreateDirectory(artifactsDir);
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // Deterministic detection metrics -> docs/*.snapshot.json (committed).
        File.WriteAllText(Path.Combine(docsDir, "detection-performance-v1_0.snapshot.json"),
            JsonSerializer.Serialize(baseline.metrics, opts));
        File.WriteAllText(Path.Combine(docsDir, "detection-performance-v1_1.snapshot.json"),
            JsonSerializer.Serialize(challenger.metrics, opts));
        File.WriteAllText(Path.Combine(docsDir, "detection-performance-sweep.snapshot.json"),
            JsonSerializer.Serialize(sweep.Select(x => x.metrics).ToList(), opts));

        // Volatile latency measurements -> artifacts/latency/*.latency.json (gitignored).
        File.WriteAllText(Path.Combine(artifactsDir, "detection-performance-v1_0.latency.json"),
            JsonSerializer.Serialize(baseline.latency, opts));
        File.WriteAllText(Path.Combine(artifactsDir, "detection-performance-v1_1.latency.json"),
            JsonSerializer.Serialize(challenger.latency, opts));
        File.WriteAllText(Path.Combine(artifactsDir, "detection-performance-sweep.latency.json"),
            JsonSerializer.Serialize(sweep.Select(x => x.latency).ToList(), opts));

        _output.WriteLine("=== BASELINE (v1.0.0) metrics ===");
        _output.WriteLine(JsonSerializer.Serialize(baseline.metrics, opts));
        _output.WriteLine("=== BASELINE (v1.0.0) latency ===");
        _output.WriteLine(JsonSerializer.Serialize(baseline.latency, opts));
        _output.WriteLine("=== CHALLENGER (v1.1.0) metrics ===");
        _output.WriteLine(JsonSerializer.Serialize(challenger.metrics, opts));
        _output.WriteLine("=== CHALLENGER (v1.1.0) latency ===");
        _output.WriteLine(JsonSerializer.Serialize(challenger.latency, opts));
        _output.WriteLine("=== SWEEP metrics ===");
        _output.WriteLine(JsonSerializer.Serialize(sweep.Select(x => x.metrics).ToList(), opts));
        _output.WriteLine("=== SWEEP latency ===");
        _output.WriteLine(JsonSerializer.Serialize(sweep.Select(x => x.latency).ToList(), opts));

        // The challenger MUST out-perform the baseline on recall.
        Assert.True(
            challenger.metrics.Recall > baseline.metrics.Recall,
            $"Challenger recall {challenger.metrics.Recall:P2} did not exceed baseline recall {baseline.metrics.Recall:P2}.");

        // Operating-point floors: recall >= 0.55, FPR <= 0.03.
        // If the model changes and these floors are missed, we want CI red.
        Assert.True(challenger.metrics.Recall >= 0.55,
            $"Challenger recall {challenger.metrics.Recall:P2} below floor 55%.");
        Assert.True(challenger.metrics.FalsePositiveRate <= 0.03,
            $"Challenger FPR {challenger.metrics.FalsePositiveRate:P2} above ceiling 3%.");
    }

    // ---------------------------------------------------------------------
    // Machinery
    // ---------------------------------------------------------------------

    private static string ResolveDocsDir()
        => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs");

    private static string ResolveLatencyArtifactsDir()
        => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "latency");

    private static SyntheticPopulation BuildPopulation()
    {
        var ids = new GuidIdGenerator();
        var gen = new SyntheticDataGenerator(new SyntheticGeneratorOptions
        {
            Seed = 42,
            CustomerCount = 100,
            MerchantCount = 30,
            DeviceCount = 200,
            IpCount = 150,
            NormalTransactionCount = 1000,
            FraudPatternCount = 20,
            StartTime = _start
        }, ids);
        return gen.BuildPopulation();
    }

    private static List<Transaction> GenerateStream(SyntheticPopulation pop)
    {
        // We build a fresh generator per invocation but keep the seed constant,
        // so the txn stream is deterministic across the baseline / challenger /
        // sweep runs. Ordering by OccurredAt matches the live pipeline.
        var ids = new GuidIdGenerator();
        var gen = new SyntheticDataGenerator(new SyntheticGeneratorOptions
        {
            Seed = 42,
            CustomerCount = 100,
            MerchantCount = 30,
            DeviceCount = 200,
            IpCount = 150,
            NormalTransactionCount = 1000,
            FraudPatternCount = 20,
            StartTime = _start
        }, ids);
        return gen.Generate(pop).OrderBy(t => t.OccurredAt).ToList();
    }

    private static async Task<(PatternedRunReport metrics, LatencyReport latency)> RunAgainst(
        IReadOnlyList<Transaction> stream,
        RulesetDefinition def)
    {
        var clock = new FakeClock(_start);
        var ids = new GuidIdGenerator();
        var runtime = new FeatureStoreRuntime(clock);
        var features = new FeatureStoreService(runtime);
        var engine = new RuleEngine();
        var metrics = new ScoringMetrics();
        var txns = new InMemoryTransactionRepository();
        var decisions = new InMemoryScoringDecisionRepository();
        var lists = new InMemoryListRepository();
        var rsRepo = new InMemoryRulesetRepository();
        var r = new Ruleset(Guid.NewGuid(), def.Version, def.Name, RulesetSerializer.Serialize(def), _start);
        r.Activate(_start);
        await rsRepo.AddAsync(r);
        var scoring = new ScoringService(features, engine, rsRepo, lists, txns, decisions, ids, clock,
            new ScoringOptions { LatencyBudgetMs = 200, EnableShadow = false }, metrics);

        var latencies = new List<double>(stream.Count);
        var sw = Stopwatch.StartNew();
        foreach (var t in stream)
        {
            var delta = t.ReceivedAt - clock.UtcNow;
            if (delta > TimeSpan.Zero) clock.Advance(delta);
            await txns.AddAsync(t);
            features.Observe(t);
            var res = await scoring.ScoreAsync(t);
            latencies.Add(res.LatencyMs);
        }
        sw.Stop();

        var evaluator = new DetectionEvaluator(decisions, txns, features, scoring, lists);
        var m = await evaluator.ComputeAsync(stream.Count + 100);

        // Per-pattern breakdown: for each ground-truth pattern in the stream,
        // how many transactions were caught vs missed? Also emits "legit" bucket.
        var patternBreakdown = ComputePatternBreakdown(stream, decisions);

        latencies.Sort();
        double P(double q) => latencies.Count == 0 ? 0 : latencies[(int)Math.Clamp(q * latencies.Count, 0, latencies.Count - 1)];

        var metricsReport = new PatternedRunReport
        {
            RulesetVersion = def.Version,
            RulesetName = def.Name,
            Label = def.Version,
            Bands = new BandsReport(def.Bands.ApproveMax, def.Bands.StepUpMax, def.Bands.ReviewMax),
            Scored = stream.Count,
            FraudInjected = stream.Count(t => t.GroundTruthFraud),
            LabelledTransactions = m.LabelledTransactions,
            TruePositives = m.TruePositives,
            FalsePositives = m.FalsePositives,
            TrueNegatives = m.TrueNegatives,
            FalseNegatives = m.FalseNegatives,
            Precision = m.Precision,
            Recall = m.Recall,
            FalsePositiveRate = m.FalsePositiveRate,
            F1 = m.F1,
            AlertVolume = m.AlertVolume,
            ValueDetected = m.ValueDetected,
            PatternBreakdown = patternBreakdown,
            TopRulesByFires = m.ByRule.Values
                .OrderByDescending(v => v.Fires)
                .Take(10)
                .Select(v => new RuleReport(v.RuleId, v.Fires, v.TruePositives, v.FalsePositives, v.Precision, v.ValueDetected))
                .ToList()
        };

        var latencyReport = new LatencyReport
        {
            Label = def.Version,
            RulesetVersion = def.Version,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Scored = stream.Count,
            TotalWallSec = sw.Elapsed.TotalSeconds,
            P50Ms = P(0.50),
            P95Ms = P(0.95),
            P99Ms = P(0.99)
        };

        return (metricsReport, latencyReport);
    }

    private static List<PatternReport> ComputePatternBreakdown(
        IReadOnlyList<Transaction> stream,
        InMemoryScoringDecisionRepository decisions)
    {
        var byRef = decisions.Items.ToDictionary(d => d.TransactionRef, d => d);
        var groups = stream
            .GroupBy(t => t.GroundTruthFraud ? (t.GroundTruthPattern ?? "unknown-fraud") : "legit");
        var reports = new List<PatternReport>();
        foreach (var g in groups)
        {
            int caught = 0, missed = 0;
            decimal caughtValue = 0m;
            foreach (var t in g)
            {
                if (!byRef.TryGetValue(t.TransactionRef, out var d)) continue;
                var predictedFraud = d.Decision == Decision.Decline || d.Decision == Decision.Review;
                if (predictedFraud) { caught++; caughtValue += t.Amount.Amount; }
                else missed++;
            }
            reports.Add(new PatternReport(g.Key, g.Count(), caught, missed, caughtValue));
        }
        return reports.OrderByDescending(r => r.Total).ToList();
    }

    // ---------------------------------------------------------------------
    // Report DTOs
    // ---------------------------------------------------------------------

    public sealed record BandsReport(int ApproveMax, int StepUpMax, int ReviewMax);

    public sealed record RuleReport(
        string RuleId,
        int Fires,
        int TruePositives,
        int FalsePositives,
        double Precision,
        decimal ValueDetected);

    public sealed record PatternReport(
        string Pattern,
        int Total,
        int PredictedFraud,
        int PredictedLegit,
        decimal ValueCaught);

    /// <summary>
    /// The reproducible-detection-metrics half of a benchmark run. Every field on
    /// this record is a deterministic function of (seeded synthetic stream,
    /// ruleset), so instances of this record serialize byte-identically on
    /// re-runs and can be safely committed to the repository as evidence.
    /// </summary>
    public sealed record PatternedRunReport
    {
        public string RulesetVersion { get; init; } = "";
        public string RulesetName { get; init; } = "";
        public string Label { get; init; } = "";
        public BandsReport Bands { get; init; } = new(0, 0, 0);
        public int Scored { get; init; }
        public int FraudInjected { get; init; }
        public int LabelledTransactions { get; init; }
        public int TruePositives { get; init; }
        public int FalsePositives { get; init; }
        public int TrueNegatives { get; init; }
        public int FalseNegatives { get; init; }
        public double Precision { get; init; }
        public double Recall { get; init; }
        public double FalsePositiveRate { get; init; }
        public double F1 { get; init; }
        public int AlertVolume { get; init; }
        public decimal ValueDetected { get; init; }
        public IReadOnlyList<PatternReport> PatternBreakdown { get; init; } = new List<PatternReport>();
        public IReadOnlyList<RuleReport> TopRulesByFires { get; init; } = new List<RuleReport>();
    }

    /// <summary>
    /// The volatile-timing half of a benchmark run. Every field on this record is
    /// wall-clock / percentile timing that varies with host load, so instances of
    /// this record are NOT committed — they are written to a gitignored
    /// artifacts/ folder on each run and referenced from docs by name.
    /// </summary>
    public sealed record LatencyReport
    {
        public string Label { get; init; } = "";
        public string RulesetVersion { get; init; } = "";
        public DateTimeOffset CapturedAtUtc { get; init; }
        public int Scored { get; init; }
        public double TotalWallSec { get; init; }
        public double P50Ms { get; init; }
        public double P95Ms { get; init; }
        public double P99Ms { get; init; }
    }
}
