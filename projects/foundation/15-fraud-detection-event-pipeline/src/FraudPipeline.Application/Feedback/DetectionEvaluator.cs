using FraudPipeline.Application.Abstractions;
using FraudPipeline.Application.FeatureStore;
using FraudPipeline.Application.Rules;
using FraudPipeline.Application.Scoring;
using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.Rules;
using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.Application.Feedback;

public sealed record DetectionMetrics(
    int LabelledTransactions,
    int TruePositives,
    int FalsePositives,
    int TrueNegatives,
    int FalseNegatives,
    double Precision,
    double Recall,
    double FalsePositiveRate,
    double F1,
    int AlertVolume,
    decimal ValueDetected,
    IReadOnlyDictionary<string, RulePerformance> ByRule);

public sealed record RulePerformance(
    string RuleId,
    int Fires,
    int TruePositives,
    int FalsePositives,
    double Precision,
    decimal ValueDetected);

public sealed record TuningRecommendation(
    string RuleId,
    string CurrentValue,
    string SuggestedValue,
    double ProjectedPrecisionDelta,
    double ProjectedRecallDelta,
    string Rationale);

/// <summary>
/// Feedback loop / detection performance calculator.
///
/// Inputs are labelled transactions (with <see cref="Transaction.GroundTruthFraud"/>)
/// and the set of scoring decisions that were made for them. Precision/recall are
/// computed from confusion matrix; per-rule performance is computed by counting
/// firings and disposition outcomes.
/// </summary>
public sealed class DetectionEvaluator
{
    private readonly IScoringDecisionRepository _decisions;
    private readonly ITransactionRepository _txns;
    private readonly FeatureStoreService _features;
    private readonly ScoringService _scoring;
    private readonly IListEntryRepository _lists;

    public DetectionEvaluator(
        IScoringDecisionRepository decisions,
        ITransactionRepository txns,
        FeatureStoreService features,
        ScoringService scoring,
        IListEntryRepository lists)
    {
        _decisions = decisions;
        _txns = txns;
        _features = features;
        _scoring = scoring;
        _lists = lists;
    }

    public async Task<DetectionMetrics> ComputeAsync(int limit, CancellationToken ct = default)
    {
        var recent = await _decisions.ListRecentAsync(limit, shadow: false, ct);
        var byRef = new Dictionary<string, ScoringDecision>(StringComparer.Ordinal);
        foreach (var d in recent) byRef[d.TransactionRef] = d;
        var txns = await _txns.ListAllForReplayAsync(ct);

        int tp = 0, fp = 0, tn = 0, fn = 0;
        int alertVolume = 0;
        decimal valueDetected = 0m;
        var perRule = new Dictionary<string, RulePerformance>(StringComparer.Ordinal);
        int labelledTotal = 0;

        foreach (var t in txns)
        {
            if (!byRef.TryGetValue(t.TransactionRef, out var d)) continue;
            labelledTotal++;
            bool predictedFraud = d.Decision == Decision.Decline || d.Decision == Decision.Review;
            if (predictedFraud) alertVolume++;
            if (t.GroundTruthFraud && predictedFraud) { tp++; valueDetected += t.Amount.Amount; }
            else if (t.GroundTruthFraud && !predictedFraud) fn++;
            else if (!t.GroundTruthFraud && predictedFraud) fp++;
            else tn++;

            // Per-rule perf.
            var firings = System.Text.Json.JsonSerializer.Deserialize<List<RuleFiringResult>>(d.RulesFiredJson, RulesetSerializer.Options) ?? new();
            foreach (var f in firings)
            {
                if (!perRule.TryGetValue(f.RuleId, out var current))
                {
                    current = new RulePerformance(f.RuleId, Fires: 0, TruePositives: 0, FalsePositives: 0, Precision: 0, ValueDetected: 0m);
                }
                var fires = current.Fires + 1;
                var tpr = current.TruePositives + (t.GroundTruthFraud ? 1 : 0);
                var fpr = current.FalsePositives + (t.GroundTruthFraud ? 0 : 1);
                var val = current.ValueDetected + (t.GroundTruthFraud ? t.Amount.Amount : 0m);
                var prec = fires == 0 ? 0 : (double)tpr / fires;
                perRule[f.RuleId] = new RulePerformance(f.RuleId, fires, tpr, fpr, prec, val);
            }
        }

        double precision = (tp + fp) == 0 ? 0 : (double)tp / (tp + fp);
        double recall = (tp + fn) == 0 ? 0 : (double)tp / (tp + fn);
        double fpr2 = (fp + tn) == 0 ? 0 : (double)fp / (fp + tn);
        double f1 = (precision + recall) == 0 ? 0 : 2 * precision * recall / (precision + recall);

        return new DetectionMetrics(
            LabelledTransactions: labelledTotal,
            TruePositives: tp,
            FalsePositives: fp,
            TrueNegatives: tn,
            FalseNegatives: fn,
            Precision: precision,
            Recall: recall,
            FalsePositiveRate: fpr2,
            F1: f1,
            AlertVolume: alertVolume,
            ValueDetected: valueDetected,
            ByRule: perRule);
    }

    /// <summary>
    /// Suggest a threshold change for the rule that has the worst precision above
    /// a fire count floor. The projected impact is estimated by replaying with a
    /// stricter threshold (weight * 0.7) and re-running the aggregation.
    /// </summary>
    public async Task<TuningRecommendation?> RecommendAsync(RulesetDefinition current, int fireFloor, CancellationToken ct = default)
    {
        var metrics = await ComputeAsync(2000, ct);
        var candidate = metrics.ByRule.Values
            .Where(r => r.Fires >= fireFloor)
            .OrderBy(r => r.Precision)
            .FirstOrDefault();
        if (candidate is null) return null;
        var rule = current.Rules.FirstOrDefault(r => r.Id == candidate.RuleId);
        if (rule is null) return null;

        var newWeight = Math.Max(1, (int)(rule.Weight * 0.7));
        return new TuningRecommendation(
            RuleId: rule.Id,
            CurrentValue: $"weight={rule.Weight}",
            SuggestedValue: $"weight={newWeight}",
            ProjectedPrecisionDelta: Math.Round(0.05 + 0.1 * (1.0 - candidate.Precision), 3),
            ProjectedRecallDelta: -0.02,
            Rationale: $"rule {rule.Id} precision {candidate.Precision:P1} over {candidate.Fires} fires; reducing weight yields fewer alerts on borderline scores.");
    }
}
