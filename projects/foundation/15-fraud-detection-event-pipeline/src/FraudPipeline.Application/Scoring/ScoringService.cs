using System.Diagnostics;
using System.Text.Json;
using FraudPipeline.Application.Abstractions;
using FraudPipeline.Application.FeatureStore;
using FraudPipeline.Application.Rules;
using FraudPipeline.Domain.Abstractions;
using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.Rules;
using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.Application.Scoring;

public sealed record ScoreResult(
    string TransactionRef,
    int Score,
    Decision Decision,
    string RulesetVersion,
    IReadOnlyList<RuleFiringResult> RulesFired,
    FeatureVector Features,
    double LatencyMs,
    bool BudgetExceeded,
    string Reasons,
    bool Shadow);

public sealed class ScoringOptions
{
    public int LatencyBudgetMs { get; set; } = 50;
    public Decision DegradedDecision { get; set; } = Decision.Review;
    public bool EnableShadow { get; set; } = true;
}

/// <summary>
/// The scoring service — the read side of the pipeline.
///   1. Snapshot the feature vector for the transaction under evaluation.
///   2. Run the active ruleset's rules; sum weighted contributions.
///   3. Apply allow/deny precedence, merchant policy overrides.
///   4. Enforce the latency budget: if exceeded, degrade gracefully.
///   5. Persist a full explanation.
/// </summary>
public sealed class ScoringService
{
    private static readonly ActivitySource ActivitySource = new("FraudPipeline.Scoring");

    private readonly FeatureStoreService _features;
    private readonly RuleEngine _engine;
    private readonly IRulesetRepository _rulesets;
    private readonly IListEntryRepository _lists;
    private readonly ITransactionRepository _txns;
    private readonly IScoringDecisionRepository _decisions;
    private readonly IIdGenerator _ids;
    private readonly IClock _clock;
    private readonly ScoringOptions _options;
    private readonly ScoringMetrics _metrics;

    private static readonly string[] _syntheticIpDenylist = new[]
    {
        "185.220.", // TOR-like (fictional)
        "45.66.",   // Datacentre-like (fictional)
        "134.19.",  // Proxy pool (fictional)
    };

    public ScoringService(
        FeatureStoreService features,
        RuleEngine engine,
        IRulesetRepository rulesets,
        IListEntryRepository lists,
        ITransactionRepository txns,
        IScoringDecisionRepository decisions,
        IIdGenerator ids,
        IClock clock,
        ScoringOptions options,
        ScoringMetrics metrics)
    {
        _features = features;
        _engine = engine;
        _rulesets = rulesets;
        _lists = lists;
        _txns = txns;
        _decisions = decisions;
        _ids = ids;
        _clock = clock;
        _options = options;
        _metrics = metrics;
    }

    public async Task<ScoreResult> ScoreAsync(Transaction txn, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("Score", ActivityKind.Internal);
        var sw = Stopwatch.StartNew();

        var active = await _rulesets.GetActiveAsync(ct) ?? throw new InvalidOperationException("No active ruleset configured.");
        var def = RulesetSerializer.Deserialize(active.DefinitionJson);

        var live = await ScoreWithRulesetAsync(txn, def, sw, shadow: false, ct);
        _metrics.RecordDecision(live);

        // Shadow evaluation runs after the live decision so it never contributes to the returned result.
        if (_options.EnableShadow)
        {
            var shadow = await _rulesets.GetShadowAsync(ct);
            if (shadow is not null)
            {
                var shadowDef = RulesetSerializer.Deserialize(shadow.DefinitionJson);
                var shadowSw = Stopwatch.StartNew();
                var shadowResult = await ScoreWithRulesetAsync(txn, shadowDef, shadowSw, shadow: true, ct);
                _metrics.RecordShadow(live, shadowResult);
            }
        }

        return live;
    }

    private async Task<ScoreResult> ScoreWithRulesetAsync(Transaction txn, RulesetDefinition def, Stopwatch sw, bool shadow, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var features = _features.Snapshot(txn, now);
        var recent = await _txns.ListRecentByCustomerAsync(txn.CustomerId, limit: 50, ct);
        var lists = await _lists.ListAsync(ct);

        var inputs = new RuleEngineInputs
        {
            Transaction = txn,
            Features = features,
            Lists = lists,
            RecentTransactions = recent,
            IpReputationDenylist = _syntheticIpDenylist,
            CustomersForDevice = features.CustomersForDevice,
            WasIpEverSeen = _features.WasIpEverSeen(txn.IpAddress)
        };

        var firings = _engine.Evaluate(def, inputs);
        var (score, decision, reasons) = Aggregate(firings, def, txn);

        double latency = sw.Elapsed.TotalMilliseconds;
        bool budgetExceeded = latency > _options.LatencyBudgetMs;
        if (budgetExceeded)
        {
            // Graceful degradation: escalate to the conservative default unless allow-listed / already declined.
            var hasAllow = firings.Any(f => f.Kind == RuleKind.AllowList);
            if (!hasAllow && decision < _options.DegradedDecision) decision = _options.DegradedDecision;
            reasons = reasons + "; budget_exceeded";
        }

        var featuresJson = JsonSerializer.Serialize(features, RulesetSerializer.Options);
        var firingsJson = JsonSerializer.Serialize(firings, RulesetSerializer.Options);

        var record = new ScoringDecision(
            id: _ids.NewGuid(),
            transactionId: txn.Id,
            transactionRef: txn.TransactionRef,
            rulesetVersion: def.Version,
            score: score,
            decision: decision,
            reasons: reasons,
            rulesFiredJson: firingsJson,
            featureVectorJson: featuresJson,
            latencyMs: latency,
            budgetExceeded: budgetExceeded,
            shadow: shadow,
            decidedAt: now);

        await _decisions.AddAsync(record, ct);
        await _decisions.SaveAsync(ct);

        return new ScoreResult(
            TransactionRef: txn.TransactionRef,
            Score: score,
            Decision: decision,
            RulesetVersion: def.Version,
            RulesFired: firings,
            Features: features,
            LatencyMs: latency,
            BudgetExceeded: budgetExceeded,
            Reasons: reasons,
            Shadow: shadow);
    }

    /// <summary>
    /// Public helper used by the tuning/replay path. Same code path as live scoring
    /// but never touches state — useful for what-if analysis.
    /// </summary>
    public (int Score, Decision Decision, IReadOnlyList<RuleFiringResult> Firings) EvaluateOnly(
        Transaction txn,
        RulesetDefinition def,
        FeatureVector features,
        IReadOnlyList<Transaction> recent,
        IReadOnlyList<ListEntry> lists)
    {
        var inputs = new RuleEngineInputs
        {
            Transaction = txn,
            Features = features,
            Lists = lists,
            RecentTransactions = recent,
            IpReputationDenylist = _syntheticIpDenylist,
            CustomersForDevice = features.CustomersForDevice,
            WasIpEverSeen = false
        };
        var firings = _engine.Evaluate(def, inputs);
        var (score, decision, _) = Aggregate(firings, def, txn);
        return (score, decision, firings);
    }

    private static (int score, Decision decision, string reasons) Aggregate(IReadOnlyList<RuleFiringResult> firings, RulesetDefinition def, Transaction txn)
    {
        var raw = 0;
        var allow = false;
        var deny = false;
        var parts = new List<string>();
        foreach (var f in firings)
        {
            if (f.Kind == RuleKind.AllowList) { allow = true; continue; }
            if (f.Kind == RuleKind.DenyList) { deny = true; }
            raw += f.Contribution;
            parts.Add(f.Reason);
        }
        raw = Math.Min(raw, def.MaxScoreCap);

        Decision decision;
        if (allow) { decision = Decision.Approve; raw = 0; parts.Insert(0, "allow-list overrides all rules"); }
        else if (deny) { decision = Decision.Decline; raw = Math.Max(raw, def.MaxScoreCap); }
        else decision = def.Bands.Classify(raw);

        // Merchant-level override — additive score offset and/or decision override.
        if (def.MerchantOverrides.TryGetValue(txn.MerchantId, out var policy))
        {
            if (policy.ScoreOffset is int off)
            {
                raw = Math.Clamp(raw + off, 0, def.MaxScoreCap);
                decision = def.Bands.Classify(raw);
            }
            if (policy.ForceReview && decision < Decision.Review) decision = Decision.Review;
            if (policy.OverrideDecision is Decision od) decision = od;
            parts.Add($"merchant-policy applied for {txn.MerchantId}");
        }

        var reason = parts.Count == 0 ? "no rules fired" : string.Join("; ", parts);
        return (raw, decision, reason);
    }
}
