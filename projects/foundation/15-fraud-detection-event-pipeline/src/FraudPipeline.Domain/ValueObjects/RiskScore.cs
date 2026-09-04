namespace FraudPipeline.Domain.ValueObjects;

public enum Decision
{
    Approve = 0,
    StepUp = 1,
    Review = 2,
    Decline = 3
}

/// <summary>
/// A 0-1000 risk score with an associated decision.
/// </summary>
public readonly record struct RiskScore
{
    public int Value { get; }
    public Decision Decision { get; }

    private RiskScore(int value, Decision decision)
    {
        Value = value;
        Decision = decision;
    }

    public static RiskScore Of(int value, Decision decision)
    {
        if (value < 0 || value > 1000) throw new ArgumentOutOfRangeException(nameof(value), "Score must be within [0, 1000].");
        return new RiskScore(value, decision);
    }

    public override string ToString() => $"{Value} ({Decision})";
}
