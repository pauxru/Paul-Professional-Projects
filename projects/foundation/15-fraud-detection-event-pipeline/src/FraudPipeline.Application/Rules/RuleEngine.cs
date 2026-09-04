using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.Rules;
using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.Application.Rules;

public sealed class RuleEngineInputs
{
    public required Transaction Transaction { get; init; }
    public required FeatureVector Features { get; init; }
    public required IReadOnlyList<ListEntry> Lists { get; init; }
    public required IReadOnlyList<Transaction> RecentTransactions { get; init; }
    public required IReadOnlyList<string> IpReputationDenylist { get; init; }
    public required int CustomersForDevice { get; init; }
    public required bool WasIpEverSeen { get; init; }
}

/// <summary>
/// Executes a <see cref="RulesetDefinition"/> against a feature vector and
/// returns the set of firings. Pure — no I/O, no clock — so testable at will.
/// </summary>
public sealed class RuleEngine
{
    public IReadOnlyList<RuleFiringResult> Evaluate(RulesetDefinition ruleset, RuleEngineInputs inputs)
    {
        var results = new List<RuleFiringResult>();
        foreach (var rule in ruleset.Rules)
        {
            if (!rule.Enabled) continue;
            var firing = EvaluateOne(rule, inputs);
            if (firing is not null) results.Add(firing);
        }
        return results;
    }

    private RuleFiringResult? EvaluateOne(RuleDefinition rule, RuleEngineInputs inputs)
    {
        return rule.Kind switch
        {
            RuleKind.Velocity => Velocity(rule, inputs),
            RuleKind.DistinctMerchants => DistinctMerchants(rule, inputs),
            RuleKind.AmountSum => AmountSum(rule, inputs),
            RuleKind.UnusualAmountZScore => UnusualAmountZ(rule, inputs),
            RuleKind.FirstTimeHighValue => FirstTimeHighValue(rule, inputs),
            RuleKind.NewDevice => NewDevice(rule, inputs),
            RuleKind.DeviceSharing => DeviceSharing(rule, inputs),
            RuleKind.IpReputation => IpReputation(rule, inputs),
            RuleKind.IpCountryMismatch => IpCountryMismatch(rule, inputs),
            RuleKind.ImpossibleTravel => ImpossibleTravelRule(rule, inputs),
            RuleKind.MerchantMccRisk => MccRiskRule(rule, inputs),
            RuleKind.TimeOfDayAnomaly => TimeOfDay(rule, inputs),
            RuleKind.CardTestingPattern => CardTesting(rule, inputs),
            RuleKind.RoundAmountAnomaly => RoundAmount(rule, inputs),
            RuleKind.DenyList => DenyList(rule, inputs),
            RuleKind.AllowList => AllowList(rule, inputs),
            _ => null
        };
    }

    private static RuleFiringResult Fire(RuleDefinition r, int contribution, string reason, IReadOnlyDictionary<string, string>? evidence = null)
        => new(r.Id, r.Kind, r.Weight, contribution, reason, evidence);

    // -------- Rule implementations --------

    private static RuleFiringResult? Velocity(RuleDefinition r, RuleEngineInputs i)
    {
        var w = r.GetParam<int>("windowSeconds", 60);
        var t = r.GetParam<int>("threshold", 4);
        var count = w switch
        {
            <= 60 => i.Features.TxnCount1m,
            <= 300 => i.Features.TxnCount5m,
            <= 3600 => i.Features.TxnCount1h,
            <= 86400 => i.Features.TxnCount24h,
            _ => i.Features.TxnCount7d
        };
        if (count < t) return null;
        var contribution = Math.Min(r.Weight, (int)(r.Weight * (double)count / (t * 2.0)));
        return Fire(r, contribution, $"velocity: {count} txns in {w}s (threshold {t})", new Dictionary<string, string> { ["count"] = count.ToString(), ["threshold"] = t.ToString(), ["windowSeconds"] = w.ToString() });
    }

    private static RuleFiringResult? DistinctMerchants(RuleDefinition r, RuleEngineInputs i)
    {
        var t = r.GetParam<int>("threshold", 6);
        var distinct = i.Features.DistinctMerchants1h;
        if (distinct < t) return null;
        return Fire(r, r.Weight, $"distinct-merchants: {distinct} in 1h (threshold {t})", new Dictionary<string, string> { ["distinctMerchants"] = distinct.ToString() });
    }

    private static RuleFiringResult? AmountSum(RuleDefinition r, RuleEngineInputs i)
    {
        var w = r.GetParam<int>("windowSeconds", 3600);
        var t = r.GetParam<decimal>("thresholdAmount", 50000m);
        var sum = w switch
        {
            <= 60 => i.Features.AmountSum1m,
            <= 300 => i.Features.AmountSum5m,
            <= 3600 => i.Features.AmountSum1h,
            <= 86400 => i.Features.AmountSum24h,
            _ => i.Features.AmountSum7d
        };
        if (sum < t) return null;
        return Fire(r, r.Weight, $"amount-sum: {sum} in {w}s (threshold {t})", new Dictionary<string, string> { ["sum"] = sum.ToString(), ["threshold"] = t.ToString() });
    }

    private static RuleFiringResult? UnusualAmountZ(RuleDefinition r, RuleEngineInputs i)
    {
        var z = r.GetParam<double>("zThreshold", 3.0);
        if (i.Features.AmountStdDev7d <= 0m || i.Features.TxnCount7d < 5) return null;
        var amt = (double)i.Transaction.Amount.Amount;
        var mean = (double)i.Features.AmountAvg7d;
        var stddev = (double)i.Features.AmountStdDev7d;
        var score = (amt - mean) / stddev;
        if (score < z) return null;
        var contribution = Math.Min(r.Weight, (int)(r.Weight * score / (z * 2.0)));
        return Fire(r, contribution, $"unusual-amount-z: z={score:F2} > {z:F1}", new Dictionary<string, string> { ["z"] = score.ToString("F3"), ["mean"] = mean.ToString("F2"), ["stddev"] = stddev.ToString("F2") });
    }

    private static RuleFiringResult? FirstTimeHighValue(RuleDefinition r, RuleEngineInputs i)
    {
        var mult = r.GetParam<double>("multipleOfAvg", 5.0);
        var floor = r.GetParam<decimal>("absoluteFloor", 20000m);
        if (i.Features.TxnCount7d < 3) return null;
        if (i.Features.AmountAvg7d <= 0m) return null;
        var ratio = i.Transaction.Amount.Amount / i.Features.AmountAvg7d;
        if (ratio < (decimal)mult) return null;
        if (i.Transaction.Amount.Amount < floor) return null;
        return Fire(r, r.Weight, $"first-time-high-value: amount is {ratio:F1}x avg", new Dictionary<string, string> { ["ratio"] = ratio.ToString("F2") });
    }

    private static RuleFiringResult? NewDevice(RuleDefinition r, RuleEngineInputs i)
    {
        if (!i.Features.IsNewDevice) return null;
        return Fire(r, r.Weight, "new-device for customer");
    }

    private static RuleFiringResult? DeviceSharing(RuleDefinition r, RuleEngineInputs i)
    {
        var t = r.GetParam<int>("threshold", 3);
        if (i.CustomersForDevice < t) return null;
        return Fire(r, r.Weight, $"device-sharing: {i.CustomersForDevice} customers on device (threshold {t})", new Dictionary<string, string> { ["customersOnDevice"] = i.CustomersForDevice.ToString() });
    }

    private static RuleFiringResult? IpReputation(RuleDefinition r, RuleEngineInputs i)
    {
        var ip = i.Transaction.IpAddress;
        foreach (var entry in i.IpReputationDenylist)
        {
            if (string.Equals(entry, ip, StringComparison.OrdinalIgnoreCase) || ip.StartsWith(entry, StringComparison.OrdinalIgnoreCase))
                return Fire(r, r.Weight, $"ip-reputation: {ip} matches {entry}");
        }
        return null;
    }

    private static RuleFiringResult? IpCountryMismatch(RuleDefinition r, RuleEngineInputs i)
    {
        // Simple heuristic: any IP starting with "203." (fictional) is treated as KE-based.
        var ip = i.Transaction.IpAddress;
        string? ipCountry = null;
        if (ip.StartsWith("203.", StringComparison.Ordinal)) ipCountry = "KE";
        else if (ip.StartsWith("50.", StringComparison.Ordinal)) ipCountry = "US";
        else if (ip.StartsWith("10.", StringComparison.Ordinal)) ipCountry = null; // unknown
        if (ipCountry is null) return null;
        var txnCountry = i.Transaction.Location.CountryIso2;
        if (string.Equals(ipCountry, txnCountry, StringComparison.OrdinalIgnoreCase)) return null;
        return Fire(r, r.Weight, $"ip-country-mismatch: ip={ipCountry} vs txn={txnCountry}", new Dictionary<string, string> { ["ipCountry"] = ipCountry, ["txnCountry"] = txnCountry });
    }

    private static RuleFiringResult? ImpossibleTravelRule(RuleDefinition r, RuleEngineInputs i)
    {
        if (i.Features.LastLocation is not GeoLocation prev) return null;
        if (i.Features.LastTxnAt is not DateTimeOffset prevAt) return null;
        var kmh = r.GetParam<double>("maxKmh", 900.0);
        var assessed = ImpossibleTravel.Assess(prev, prevAt, i.Transaction.Location, i.Transaction.OccurredAt, kmh);
        if (!assessed.Impossible) return null;
        return Fire(r, r.Weight, $"impossible-travel: {assessed.DistanceKm:F0}km at {assessed.SpeedKmh:F0}km/h", new Dictionary<string, string> { ["distanceKm"] = assessed.DistanceKm.ToString("F1"), ["speedKmh"] = assessed.SpeedKmh.ToString("F1") });
    }

    private static RuleFiringResult? MccRiskRule(RuleDefinition r, RuleEngineInputs i)
    {
        var w = MccRisk.Weight(i.Transaction.MerchantCategoryCode);
        if (w < 50) return null;
        var contribution = Math.Min(r.Weight, r.Weight * w / 100);
        return Fire(r, contribution, $"mcc-risk: {i.Transaction.MerchantCategoryCode} weight={w}");
    }

    private static RuleFiringResult? TimeOfDay(RuleDefinition r, RuleEngineInputs i)
    {
        var startH = r.GetParam<int>("startHourLocal", 1);
        var endH = r.GetParam<int>("endHourLocal", 5);
        // Naive local hour: derive from country (KE = UTC+3, US = UTC-5 as a default). Explicit for auditability.
        var offset = i.Transaction.Location.CountryIso2 switch
        {
            "KE" => TimeSpan.FromHours(3),
            "US" => TimeSpan.FromHours(-5),
            _ => TimeSpan.Zero
        };
        var localHour = i.Transaction.OccurredAt.ToOffset(offset).Hour;
        bool anomalous = startH <= endH
            ? localHour >= startH && localHour < endH
            : localHour >= startH || localHour < endH;
        if (!anomalous) return null;
        return Fire(r, r.Weight, $"time-of-day: {localHour}:00 local", new Dictionary<string, string> { ["localHour"] = localHour.ToString() });
    }

    private static RuleFiringResult? CardTesting(RuleDefinition r, RuleEngineInputs i)
    {
        var ceiling = r.GetParam<decimal>("smallAmountCeiling", 100m);
        var smallThreshold = r.GetParam<int>("smallCountThreshold", 5);
        var bigMultiple = r.GetParam<decimal>("bigMultiple", 20m);
        var recent = i.RecentTransactions
            .Where(t => t.CardId == i.Transaction.CardId && t.TransactionRef != i.Transaction.TransactionRef)
            .OrderBy(t => t.OccurredAt)
            .ToList();
        var lastN = recent.TakeLast(smallThreshold).ToList();
        if (lastN.Count < smallThreshold) return null;
        var smallOnes = lastN.All(t => t.Amount.Amount <= ceiling);
        var current = i.Transaction.Amount.Amount;
        var big = current >= ceiling * bigMultiple;
        if (!(smallOnes && big)) return null;
        return Fire(r, r.Weight, $"card-testing: {smallThreshold} small then {current} big", new Dictionary<string, string> { ["ceiling"] = ceiling.ToString(), ["current"] = current.ToString() });
    }

    private static RuleFiringResult? RoundAmount(RuleDefinition r, RuleEngineInputs i)
    {
        var denom = r.GetParam<decimal>("denomination", 10000m);
        if (i.Transaction.Amount.Amount < denom) return null;
        if (i.Transaction.Amount.Amount % denom != 0m) return null;
        return Fire(r, r.Weight, $"round-amount: {i.Transaction.Amount.Amount} is multiple of {denom}");
    }

    private static RuleFiringResult? DenyList(RuleDefinition r, RuleEngineInputs i)
    {
        foreach (var e in i.Lists)
        {
            if (e.Type != ListType.Deny) continue;
            if (Matches(e, i.Transaction)) return Fire(r, r.Weight, $"deny-list: {e.Subject}={e.Value} ({e.Reason})");
        }
        return null;
    }

    private static RuleFiringResult? AllowList(RuleDefinition r, RuleEngineInputs i)
    {
        foreach (var e in i.Lists)
        {
            if (e.Type != ListType.Allow) continue;
            if (Matches(e, i.Transaction)) return Fire(r, r.Weight, $"allow-list: {e.Subject}={e.Value}");
        }
        return null;
    }

    private static bool Matches(ListEntry entry, Transaction t) => entry.Subject switch
    {
        ListSubject.Card => string.Equals(entry.Value, t.CardId, StringComparison.OrdinalIgnoreCase),
        ListSubject.Customer => string.Equals(entry.Value, t.CustomerId, StringComparison.OrdinalIgnoreCase),
        ListSubject.Device => string.Equals(entry.Value, t.DeviceId, StringComparison.OrdinalIgnoreCase),
        ListSubject.Ip => string.Equals(entry.Value, t.IpAddress, StringComparison.OrdinalIgnoreCase),
        ListSubject.Merchant => string.Equals(entry.Value, t.MerchantId, StringComparison.OrdinalIgnoreCase),
        ListSubject.Country => string.Equals(entry.Value, t.Location.CountryIso2, StringComparison.OrdinalIgnoreCase),
        _ => false
    };
}
