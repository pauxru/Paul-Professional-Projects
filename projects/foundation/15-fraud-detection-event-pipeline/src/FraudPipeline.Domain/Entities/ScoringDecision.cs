using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.Domain.Entities;

/// <summary>
/// Persistent record of a scoring decision. Full explanation so that
/// re-scoring with the same ruleset version reproduces the same result.
/// </summary>
public sealed class ScoringDecision
{
    public Guid Id { get; private set; }
    public Guid TransactionId { get; private set; }
    public string TransactionRef { get; private set; }
    public string RulesetVersion { get; private set; }
    public int Score { get; private set; }
    public Decision Decision { get; private set; }
    public string Reasons { get; private set; }
    public string RulesFiredJson { get; private set; }
    public string FeatureVectorJson { get; private set; }
    public double LatencyMs { get; private set; }
    public bool BudgetExceeded { get; private set; }
    public bool Shadow { get; private set; }
    public DateTimeOffset DecidedAt { get; private set; }

    private ScoringDecision()
    {
        TransactionRef = RulesetVersion = Reasons = RulesFiredJson = FeatureVectorJson = "";
    }

    public ScoringDecision(
        Guid id,
        Guid transactionId,
        string transactionRef,
        string rulesetVersion,
        int score,
        Decision decision,
        string reasons,
        string rulesFiredJson,
        string featureVectorJson,
        double latencyMs,
        bool budgetExceeded,
        bool shadow,
        DateTimeOffset decidedAt)
    {
        Id = id;
        TransactionId = transactionId;
        TransactionRef = transactionRef;
        RulesetVersion = rulesetVersion;
        Score = score;
        Decision = decision;
        Reasons = reasons;
        RulesFiredJson = rulesFiredJson;
        FeatureVectorJson = featureVectorJson;
        LatencyMs = latencyMs;
        BudgetExceeded = budgetExceeded;
        Shadow = shadow;
        DecidedAt = decidedAt;
    }
}
