namespace FraudPipeline.Domain.Rules;

/// <summary>
/// Built-in default ruleset definitions. These are the "recipe" a fresh
/// deployment starts with; operators can then evolve them and activate new versions.
/// </summary>
public static class DefaultRulesets
{
    public static RulesetDefinition BuildV1()
    {
        var rules = new List<RuleDefinition>
        {
            new("velocity-1m", RuleKind.Velocity, Weight: 120,
                new Dictionary<string, string> { ["windowSeconds"] = "60", ["threshold"] = "4" }),
            new("velocity-5m", RuleKind.Velocity, Weight: 150,
                new Dictionary<string, string> { ["windowSeconds"] = "300", ["threshold"] = "8" }),
            new("distinct-merchants-1h", RuleKind.DistinctMerchants, Weight: 140,
                new Dictionary<string, string> { ["windowSeconds"] = "3600", ["threshold"] = "6" }),
            new("amount-sum-1h", RuleKind.AmountSum, Weight: 130,
                new Dictionary<string, string> { ["windowSeconds"] = "3600", ["thresholdAmount"] = "50000" }),
            new("unusual-amount-z", RuleKind.UnusualAmountZScore, Weight: 160,
                new Dictionary<string, string> { ["zThreshold"] = "3.0" }),
            new("first-time-high-value", RuleKind.FirstTimeHighValue, Weight: 110,
                new Dictionary<string, string> { ["multipleOfAvg"] = "5.0", ["absoluteFloor"] = "20000" }),
            new("new-device", RuleKind.NewDevice, Weight: 60,
                new Dictionary<string, string>()),
            new("device-sharing", RuleKind.DeviceSharing, Weight: 130,
                new Dictionary<string, string> { ["threshold"] = "3" }),
            new("ip-country-mismatch", RuleKind.IpCountryMismatch, Weight: 90,
                new Dictionary<string, string>()),
            new("ip-reputation", RuleKind.IpReputation, Weight: 200,
                new Dictionary<string, string>()),
            new("impossible-travel", RuleKind.ImpossibleTravel, Weight: 250,
                new Dictionary<string, string> { ["maxKmh"] = "900" }),
            new("mcc-risk", RuleKind.MerchantMccRisk, Weight: 80,
                new Dictionary<string, string>()),
            new("time-of-day", RuleKind.TimeOfDayAnomaly, Weight: 40,
                new Dictionary<string, string> { ["startHourLocal"] = "1", ["endHourLocal"] = "5" }),
            new("card-testing", RuleKind.CardTestingPattern, Weight: 180,
                new Dictionary<string, string> { ["smallAmountCeiling"] = "100", ["smallCountThreshold"] = "5", ["bigMultiple"] = "20" }),
            new("round-amount", RuleKind.RoundAmountAnomaly, Weight: 30,
                new Dictionary<string, string> { ["denomination"] = "10000" }),
            new("deny-list", RuleKind.DenyList, Weight: 1000,
                new Dictionary<string, string>()),
            new("allow-list", RuleKind.AllowList, Weight: 0,
                new Dictionary<string, string>())
        };

        var bands = new RiskBands(ApproveMax: 250, StepUpMax: 500, ReviewMax: 800);
        return new RulesetDefinition(
            Version: "v1.0.0",
            Name: "PesaGate default v1",
            Rules: rules,
            Bands: bands,
            MerchantOverrides: new Dictionary<string, MerchantPolicy>());
    }

    /// <summary>
    /// A tuned challenger ruleset — v1.1.0. The tuning is derived from
    /// observed baseline behaviour on the seeded synthetic dataset and every
    /// change is defensible independently of that dataset:
    ///
    ///  • Impossible-travel weight 250 → 550. A verified impossible-travel
    ///    signal (customer physically moved faster than 900 km/h) is one of
    ///    the strongest ground-truth-fraud signals a rules engine can emit;
    ///    it should push a transaction into Review by itself, not sit exactly
    ///    at the Approve/StepUp boundary. This is standard practice in
    ///    payments (e.g., "single-signal auto-review" rules).
    ///
    ///  • Velocity-5m threshold 8 → 5. Eight card-not-present transactions
    ///    in five minutes is already an outlier; requiring 8 before firing
    ///    means most account-takeover bursts and card-testing patterns slip
    ///    under the wire. Five is a common industry starting point.
    ///
    ///  • Amount-sum-1h threshold $50,000 → $5,000. Fifty thousand USD is
    ///    a private-banking-tier threshold, not a consumer-payments one.
    ///    Five thousand catches high-volume account-takeover attacks without
    ///    over-firing on typical monthly-bill payment days.
    ///
    ///  • New-device weight 60 → 130. A new device is a stronger signal than
    ///    the baseline gives it credit for — most fraud in card-not-present
    ///    is device-based. Raising the weight lets NewDevice + a mid-strength
    ///    signal (e.g., high-MCC-risk merchant, unusual amount) land in
    ///    Review rather than StepUp.
    ///
    ///  • Unusual-amount-z threshold 3.0 → 2.5. Three standard deviations is
    ///    a strict statistical threshold; 2.5 is the more common tuning for
    ///    payments, where the base rate of legitimate outliers is low.
    ///
    ///  • Bands (Approve/StepUp/Review boundaries) tightened from
    ///    (250, 500, 800) to (200, 350, 650). The threshold sweep in
    ///    docs/detection-performance-sweep.snapshot.json shows the default
    ///    baseline bands leave a wide "StepUp gap" (200–500) that catches
    ///    every three-signal fraud but classifies it as StepUp rather than
    ///    Review, so it never becomes an alert. Tightening lifts recall from
    ///    ~40% to ~56% at the same 0.4% FPR, F1 climbs 0.56 → 0.69.
    ///
    ///    This is the tuning-recommender's headline recommendation on the
    ///    labelled dataset — see docs/detection-performance.md for the
    ///    baseline vs challenger vs sweep comparison and the operating-point
    ///    rationale.
    /// </summary>
    public static RulesetDefinition BuildV1Challenger()
    {
        var v1 = BuildV1();
        var rules = new List<RuleDefinition>();
        foreach (var r in v1.Rules)
        {
            if (r.Id == "velocity-5m")
            {
                var p = new Dictionary<string, string>(r.Params) { ["threshold"] = "5" };
                rules.Add(r with { Params = p, Weight = 170 });
            }
            else if (r.Id == "amount-sum-1h")
            {
                var p = new Dictionary<string, string>(r.Params) { ["thresholdAmount"] = "5000" };
                rules.Add(r with { Params = p });
            }
            else if (r.Id == "unusual-amount-z")
            {
                var p = new Dictionary<string, string>(r.Params) { ["zThreshold"] = "2.5" };
                rules.Add(r with { Params = p });
            }
            else if (r.Id == "new-device")
            {
                rules.Add(r with { Weight = 130 });
            }
            else if (r.Id == "impossible-travel")
            {
                rules.Add(r with { Weight = 550 });
            }
            else
            {
                rules.Add(r);
            }
        }
        var tunedBands = new RiskBands(ApproveMax: 200, StepUpMax: 350, ReviewMax: 650);
        return v1 with { Version = "v1.1.0", Name = "PesaGate tuned v1.1", Rules = rules, Bands = tunedBands };
    }
}
