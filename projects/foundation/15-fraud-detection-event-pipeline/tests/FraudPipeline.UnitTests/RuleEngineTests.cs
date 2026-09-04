using FraudPipeline.Application.Rules;
using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.Rules;
using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.UnitTests;

public class RuleEngineTests
{
    private static readonly DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Transaction Txn(decimal amount = 100m, string mcc = "5411", string country = "US", string ip = "192.0.2.1", DateTimeOffset? at = null)
        => new(
            id: Guid.NewGuid(),
            transactionRef: "TX-" + Guid.NewGuid().ToString("N")[..8],
            cardId: "CARD1",
            customerId: "CUST1",
            deviceId: "DEV1",
            ipAddress: ip,
            merchantId: "MERCH1",
            mcc: mcc,
            amount: Money.Of(amount, country == "KE" ? "KES" : "USD"),
            type: TransactionType.CardNotPresent,
            location: GeoLocation.Of(country == "KE" ? -1.29 : 40.71, country == "KE" ? 36.82 : -74.00, country),
            occurredAt: at ?? _now,
            receivedAt: (at ?? _now).AddSeconds(1));

    private static FeatureVector Features(
        decimal amt = 100m,
        int txnCount1m = 0, int txnCount5m = 0, int txnCount1h = 0, int txnCount7d = 5,
        decimal avg = 100m, decimal stddev = 10m,
        int distinctMerchants1h = 1, int distinctCountries24h = 1,
        bool isNewDevice = false, int customersForDevice = 1,
        GeoLocation? lastLocation = null, DateTimeOffset? lastAt = null)
        => new(
            CardId: "CARD1", CustomerId: "CUST1", DeviceId: "DEV1", IpAddress: "192.0.2.1", MerchantId: "MERCH1",
            TxnCount1m: txnCount1m, TxnCount5m: txnCount5m, TxnCount1h: txnCount1h, TxnCount24h: 5, TxnCount7d: txnCount7d,
            AmountSum1m: amt, AmountSum5m: amt, AmountSum1h: amt, AmountSum24h: amt, AmountSum7d: amt * 5,
            AmountAvg7d: avg, AmountStdDev7d: stddev,
            DistinctMerchants1h: distinctMerchants1h, DistinctCountries24h: distinctCountries24h, DistinctDevices24h: 1,
            DeclineCount1h: 0, ChargebackCount7d: 0,
            IsNewDevice: isNewDevice, DevicesForCustomer: 1, CustomersForDevice: customersForDevice,
            LastLocation: lastLocation, LastTxnAt: lastAt);

    private static RuleEngineInputs BuildInputs(Transaction txn, FeatureVector features, IReadOnlyList<Transaction>? recent = null, IReadOnlyList<ListEntry>? lists = null, IReadOnlyList<string>? ipDeny = null)
        => new()
        {
            Transaction = txn,
            Features = features,
            Lists = lists ?? Array.Empty<ListEntry>(),
            RecentTransactions = recent ?? Array.Empty<Transaction>(),
            IpReputationDenylist = ipDeny ?? Array.Empty<string>(),
            CustomersForDevice = features.CustomersForDevice,
            WasIpEverSeen = false
        };

    private static RulesetDefinition RulesetWith(params RuleDefinition[] rules)
        => new("test-v1", "test", rules, new RiskBands(200, 500, 800), new Dictionary<string, MerchantPolicy>());

    // ---------- Velocity ----------
    [Fact]
    public void Velocity_UnderThreshold_DoesNotFire()
    {
        var engine = new RuleEngine();
        var def = RulesetWith(new RuleDefinition("v", RuleKind.Velocity, 100, new Dictionary<string, string> { ["windowSeconds"] = "60", ["threshold"] = "5" }));
        var firings = engine.Evaluate(def, BuildInputs(Txn(), Features(txnCount1m: 2)));
        Assert.Empty(firings);
    }

    [Fact]
    public void Velocity_AboveThreshold_Fires()
    {
        var engine = new RuleEngine();
        var def = RulesetWith(new RuleDefinition("v", RuleKind.Velocity, 100, new Dictionary<string, string> { ["windowSeconds"] = "60", ["threshold"] = "5" }));
        var firings = engine.Evaluate(def, BuildInputs(Txn(), Features(txnCount1m: 8)));
        var f = Assert.Single(firings);
        Assert.Equal(RuleKind.Velocity, f.Kind);
        Assert.True(f.Contribution > 0);
    }

    // ---------- Unusual Amount Z ----------
    [Fact]
    public void UnusualAmountZScore_HighZ_Fires()
    {
        var engine = new RuleEngine();
        var def = RulesetWith(new RuleDefinition("z", RuleKind.UnusualAmountZScore, 100, new Dictionary<string, string> { ["zThreshold"] = "3.0" }));
        var t = Txn(amount: 10_000m);
        var f = engine.Evaluate(def, BuildInputs(t, Features(avg: 100m, stddev: 20m, txnCount7d: 10)));
        Assert.NotEmpty(f);
    }

    [Fact]
    public void UnusualAmountZScore_TypicalTxn_DoesNotFire()
    {
        var engine = new RuleEngine();
        var def = RulesetWith(new RuleDefinition("z", RuleKind.UnusualAmountZScore, 100, new Dictionary<string, string> { ["zThreshold"] = "3.0" }));
        var t = Txn(amount: 105m);
        var f = engine.Evaluate(def, BuildInputs(t, Features(avg: 100m, stddev: 20m, txnCount7d: 10)));
        Assert.Empty(f);
    }

    // ---------- MCC Risk ----------
    [Fact]
    public void MccRisk_HighRiskCategory_Fires()
    {
        var engine = new RuleEngine();
        var def = RulesetWith(new RuleDefinition("mcc", RuleKind.MerchantMccRisk, 100, new Dictionary<string, string>()));
        var f = engine.Evaluate(def, BuildInputs(Txn(mcc: "6051"), Features()));
        Assert.NotEmpty(f);
    }

    [Fact]
    public void MccRisk_LowRiskCategory_DoesNotFire()
    {
        var engine = new RuleEngine();
        var def = RulesetWith(new RuleDefinition("mcc", RuleKind.MerchantMccRisk, 100, new Dictionary<string, string>()));
        var f = engine.Evaluate(def, BuildInputs(Txn(mcc: "5411"), Features()));
        Assert.Empty(f);
    }

    // ---------- Impossible Travel ----------
    [Fact]
    public void ImpossibleTravelRule_FarApartQuickly_Fires()
    {
        var engine = new RuleEngine();
        var def = RulesetWith(new RuleDefinition("t", RuleKind.ImpossibleTravel, 200, new Dictionary<string, string> { ["maxKmh"] = "900" }));
        var lastLoc = GeoLocation.Of(-1.29, 36.82, "KE");
        var lastAt = _now.AddMinutes(-30);
        var t = Txn(country: "US", at: _now);
        var f = engine.Evaluate(def, BuildInputs(t, Features(lastLocation: lastLoc, lastAt: lastAt)));
        Assert.NotEmpty(f);
    }

    [Fact]
    public void ImpossibleTravelRule_ReasonableTime_DoesNotFire()
    {
        var engine = new RuleEngine();
        var def = RulesetWith(new RuleDefinition("t", RuleKind.ImpossibleTravel, 200, new Dictionary<string, string> { ["maxKmh"] = "900" }));
        var lastLoc = GeoLocation.Of(-1.29, 36.82, "KE");
        var lastAt = _now.AddHours(-24);
        var t = Txn(country: "US", at: _now);
        var f = engine.Evaluate(def, BuildInputs(t, Features(lastLocation: lastLoc, lastAt: lastAt)));
        Assert.Empty(f);
    }

    // ---------- Card Testing Pattern ----------
    [Fact]
    public void CardTestingPattern_FiveSmallThenBig_Fires()
    {
        var engine = new RuleEngine();
        var def = RulesetWith(new RuleDefinition("ct", RuleKind.CardTestingPattern, 200,
            new Dictionary<string, string> { ["smallAmountCeiling"] = "100", ["smallCountThreshold"] = "5", ["bigMultiple"] = "20" }));
        var recent = new List<Transaction>();
        for (int i = 0; i < 5; i++) recent.Add(Txn(amount: 5m, at: _now.AddMinutes(-i - 1)));
        var t = Txn(amount: 2500m);
        var f = engine.Evaluate(def, BuildInputs(t, Features(), recent));
        Assert.NotEmpty(f);
    }

    // ---------- Deny/Allow Lists ----------
    [Fact]
    public void DenyList_HitsCard_Fires()
    {
        var engine = new RuleEngine();
        var def = RulesetWith(new RuleDefinition("dl", RuleKind.DenyList, 1000, new Dictionary<string, string>()));
        var lists = new[] { new ListEntry(Guid.NewGuid(), ListType.Deny, ListSubject.Card, "CARD1", "known bad", _now) };
        var f = engine.Evaluate(def, BuildInputs(Txn(), Features(), lists: lists));
        Assert.NotEmpty(f);
    }

    [Fact]
    public void AllowList_HitsMerchant_Fires()
    {
        var engine = new RuleEngine();
        var def = RulesetWith(new RuleDefinition("al", RuleKind.AllowList, 0, new Dictionary<string, string>()));
        var lists = new[] { new ListEntry(Guid.NewGuid(), ListType.Allow, ListSubject.Merchant, "MERCH1", "trusted", _now) };
        var f = engine.Evaluate(def, BuildInputs(Txn(), Features(), lists: lists));
        Assert.NotEmpty(f);
        Assert.Equal(RuleKind.AllowList, f[0].Kind);
    }

    // ---------- Round Amount ----------
    [Fact]
    public void RoundAmount_MultipleOfDenomination_Fires()
    {
        var engine = new RuleEngine();
        var def = RulesetWith(new RuleDefinition("ra", RuleKind.RoundAmountAnomaly, 30, new Dictionary<string, string> { ["denomination"] = "10000" }));
        var f = engine.Evaluate(def, BuildInputs(Txn(amount: 20000m), Features()));
        Assert.NotEmpty(f);
    }

    // ---------- Time of day ----------
    [Fact]
    public void TimeOfDay_MiddleOfNightInLocal_Fires()
    {
        var engine = new RuleEngine();
        var def = RulesetWith(new RuleDefinition("tod", RuleKind.TimeOfDayAnomaly, 40, new Dictionary<string, string> { ["startHourLocal"] = "1", ["endHourLocal"] = "5" }));
        // A US txn at 07:00 UTC == 02:00 EST — within window
        var t = Txn(at: new DateTimeOffset(2026, 1, 1, 7, 0, 0, TimeSpan.Zero));
        var f = engine.Evaluate(def, BuildInputs(t, Features()));
        Assert.NotEmpty(f);
    }
}
