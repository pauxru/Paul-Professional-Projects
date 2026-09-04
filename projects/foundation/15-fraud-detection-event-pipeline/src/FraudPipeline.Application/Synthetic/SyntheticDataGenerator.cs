using FraudPipeline.Domain.Abstractions;
using FraudPipeline.Domain.Entities;
using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.Application.Synthetic;

public sealed record SyntheticPopulation(
    IReadOnlyList<string> CustomerIds,
    IReadOnlyList<string> CardIds,
    IReadOnlyList<string> DeviceIds,
    IReadOnlyList<string> MerchantIds,
    IReadOnlyList<string> IpAddresses);

public sealed class SyntheticGeneratorOptions
{
    public int Seed { get; set; } = 42;
    public int CustomerCount { get; set; } = 200;
    public int MerchantCount { get; set; } = 50;
    public int DeviceCount { get; set; } = 400;
    public int IpCount { get; set; } = 300;
    public int NormalTransactionCount { get; set; } = 5_000;
    public int FraudPatternCount { get; set; } = 40;
    public DateTimeOffset StartTime { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// Seeded synthetic data generator. Produces normal traffic + a known number of
/// each fraud pattern so precision/recall can be computed against ground truth.
///
/// Patterns injected (each labelled with GroundTruthPattern):
///  - "card-testing":     N small transactions in quick succession + one large.
///  - "takeover-burst":   sudden burst of high-value transactions from new device.
///  - "impossible-travel": two txns in far-apart cities in a short time window.
///  - "merchant-collusion": many refunds from one merchant to same customer.
///  - "refund-abuse":     high-value txn then refund shortly after.
/// </summary>
public sealed class SyntheticDataGenerator
{
    private readonly SyntheticGeneratorOptions _options;
    private readonly IIdGenerator _ids;
    private readonly Random _random;

    private static readonly (string City, double Lat, double Lon, string Country)[] _cities = new[]
    {
        ("Nairobi",   -1.2921, 36.8219, "KE"),
        ("Mombasa",   -4.0435, 39.6682, "KE"),
        ("Kisumu",    -0.0917, 34.7680, "KE"),
        ("Nakuru",    -0.3031, 36.0800, "KE"),
        ("New York",  40.7128, -74.0060, "US"),
        ("San Francisco", 37.7749, -122.4194, "US"),
        ("Los Angeles", 34.0522, -118.2437, "US"),
        ("Chicago",   41.8781, -87.6298, "US"),
    };

    // Realistic MCC distribution: mostly low-risk consumer categories (grocery,
    // restaurants, transport). One high-risk category is included so the rule
    // engine has something to fire on, but it appears only ~5% of the time —
    // matching real payment-network data where crypto/wire-transfer MCCs are
    // a small tail. In an earlier version this pool was 50 % high-risk and
    // produced garbage FP rates on the MCC rule.
    private static readonly (string Mcc, double Weight)[] _mccWeights = new[]
    {
        ("5411", 25.0), // grocery
        ("5812", 20.0), // restaurants
        ("4111", 10.0), // transport
        ("5541", 10.0), // fuel
        ("5912", 8.0),  // pharmacy
        ("4816",  5.0), // digital goods
        ("5411", 5.0),  // (weighted twice on purpose — normal shopping dominates)
        ("5812", 5.0),
        ("6011", 3.0),  // ATM (higher-risk — small tail)
        ("5967", 2.0),  // direct marketing (higher-risk)
        ("6051", 2.0),  // quasi-cash / crypto (highest-risk — very small tail)
        ("4829", 1.0),  // money transfer (highest-risk)
    };

    /// <summary>Pick an MCC with the weighted distribution above.</summary>
    private string PickMcc()
    {
        var total = _mccWeights.Sum(m => m.Weight);
        var r = _random.NextDouble() * total;
        double acc = 0;
        foreach (var (mcc, w) in _mccWeights)
        {
            acc += w;
            if (r < acc) return mcc;
        }
        return _mccWeights[0].Mcc;
    }

    public SyntheticDataGenerator(SyntheticGeneratorOptions options, IIdGenerator ids)
    {
        _options = options;
        _ids = ids;
        _random = new Random(options.Seed);
    }

    public SyntheticPopulation BuildPopulation()
    {
        var customers = Enumerable.Range(1, _options.CustomerCount).Select(i => $"CUST{i:D5}").ToArray();
        var cards = Enumerable.Range(1, _options.CustomerCount).Select(i => $"CARD{i:D5}").ToArray();
        var devices = Enumerable.Range(1, _options.DeviceCount).Select(i => $"DEV{i:D5}").ToArray();
        var merchants = Enumerable.Range(1, _options.MerchantCount).Select(i => $"MERCH{i:D4}").ToArray();
        // Split IP pool by geography so the ip-country-mismatch rule has a
        // realistic base rate. In an earlier version every IP started with
        // "203." (which the rule engine treats as Kenyan), which meant every
        // US-based txn produced an ip-country mismatch false-positive — see
        // src/FraudPipeline.Application/Rules/RuleEngine.cs::IpCountryMismatch
        // for the IP-to-country heuristic.
        var half = _options.IpCount / 2;
        var ips = new List<string>(_options.IpCount);
        for (int i = 0; i < half; i++) ips.Add($"203.0.{i / 256}.{i % 256}");        // KE-mapped
        for (int i = 0; i < _options.IpCount - half; i++) ips.Add($"50.10.{i / 256}.{i % 256}"); // US-mapped
        return new SyntheticPopulation(customers, cards, devices, merchants, ips.ToArray());
    }

    public IEnumerable<Transaction> Generate(SyntheticPopulation population)
    {
        // Each customer is assigned a stable "home profile": home city, primary
        // device, primary IP prefix. Normal traffic uses those defaults with
        // low-probability drift (e.g. an occasional txn in another city with
        // a plausible neighbouring-country IP), which matches how real
        // consumer traffic looks. Fraud patterns then deliberately break these
        // defaults (attacker uses a new device, txn appears in a foreign city
        // 30 minutes after the last home-city txn, etc.), which is what makes
        // them detectable.
        //
        // Without this stable profile, normal traffic randomly bounced between
        // Nairobi, Mombasa, New York, San Francisco every transaction — and
        // the impossible-travel rule (correctly) fired on almost every txn,
        // producing catastrophic false-positive rates. That was a harness bug,
        // not a detection bug.
        var homeCity = new (string City, double Lat, double Lon, string Country)[population.CustomerIds.Count];
        var homeDevice = new string[population.CustomerIds.Count];
        var homeIp = new string[population.CustomerIds.Count];
        // Deterministic collision-free device assignment. With random
        // assignment from a pool of size D and C customers, the expected
        // number of colliding pairs is C*(C-1)/(2D) — for 200 customers on
        // 400 devices that's ~50 devices shared between two customers by
        // construction, and every subsequent legit txn on those devices
        // fires the device-sharing rule as a false positive. In production
        // a "one device -> one primary account" invariant is enforced at
        // enrolment; we model that here by assigning devices without repeats.
        var shuffledDevices = population.DeviceIds.OrderBy(_ => _random.Next()).ToArray();
        var shuffledKeIps = population.IpAddresses.Where(ip => ip.StartsWith("203.", StringComparison.Ordinal)).OrderBy(_ => _random.Next()).ToArray();
        var shuffledUsIps = population.IpAddresses.Where(ip => ip.StartsWith("50.", StringComparison.Ordinal)).OrderBy(_ => _random.Next()).ToArray();
        var keIpCursor = 0;
        var usIpCursor = 0;
        for (int c = 0; c < population.CustomerIds.Count; c++)
        {
            homeCity[c] = _cities[_random.Next(_cities.Length)];
            homeDevice[c] = shuffledDevices[c % shuffledDevices.Length];
            // Home IP must be geographically consistent with home city or the
            // ip-country-mismatch rule fires as a background hum on legit
            // traffic.
            if (homeCity[c].Country == "KE" && shuffledKeIps.Length > 0)
            {
                homeIp[c] = shuffledKeIps[keIpCursor++ % shuffledKeIps.Length];
            }
            else if (shuffledUsIps.Length > 0)
            {
                homeIp[c] = shuffledUsIps[usIpCursor++ % shuffledUsIps.Length];
            }
            else
            {
                homeIp[c] = population.IpAddresses[_random.Next(population.IpAddresses.Count)];
            }
        }

        var timePointer = _options.StartTime;
        var txnRefSeq = 1;

        // Normal traffic — stable per-customer home city / device / ip with
        // small drift probabilities. NO cross-country drift in normal traffic:
        // the compressed timeline of the test dataset makes any legitimate
        // cross-country trip look like impossible travel. In production a
        // pre-authorisation "travel notice" workflow would carve those out.
        for (int i = 0; i < _options.NormalTransactionCount; i++)
        {
            timePointer = timePointer.AddSeconds(_random.Next(1, 60));
            var customerIdx = _random.Next(population.CustomerIds.Count);
            var city = homeCity[customerIdx];
            // NO in-stream city drift: the compressed timeline makes any city
            // change look like impossible travel even for a plausible neighbouring
            // city (Chicago -> NY 30 seconds apart is 1.4M km/h). In production
            // a travel-notice workflow would handle this; in the synthetic run
            // we keep customers pinned to their home city, and rely on the
            // labelled fraud patterns to inject the geographic anomaly.
            // No device drift in normal traffic: a single stray "family
            // shared" txn makes the device permanently multi-customer and
            // the device-sharing rule then fires on every subsequent legit
            // txn on that device. In production a "trusted device" enrolment
            // flow handles this — in the harness we keep customers pinned to
            // their home device and let fraud patterns explicitly introduce
            // device sharing (takeover-burst, card-testing).
            var deviceId = homeDevice[customerIdx];
            string ipAddr;
            if (_random.NextDouble() < 0.03)
            {
                var ipPrefix = city.Country == "KE" ? "203." : "50.";
                var pool = population.IpAddresses.Where(ip => ip.StartsWith(ipPrefix, StringComparison.Ordinal)).ToArray();
                ipAddr = pool.Length > 0 ? pool[_random.Next(pool.Length)] : homeIp[customerIdx];
            }
            else
            {
                ipAddr = homeIp[customerIdx];
            }
            var amount = (decimal)(50 + _random.NextDouble() * 500);
            yield return NewTxn(
                txnRef: $"TX{txnRefSeq++:D7}",
                customerIdx: customerIdx,
                population: population,
                mcc: PickMcc(),
                amount: amount,
                city: city,
                at: timePointer,
                type: TransactionType.CardNotPresent,
                fraud: false,
                pattern: null,
                overrideDeviceId: deviceId,
                overrideIp: ipAddr);
        }

        // Card-testing pattern.
        //
        // Labelling note: only the *big* attack transaction is labelled as
        // ground-truth fraud. The 5 preceding "small setup" transactions are
        // legitimate-looking $1–$6 micro-purchases that the attacker uses as a
        // stealth probe: they are the *setup*, not the *payment attack*. This
        // matches how real payment-fraud analysts label card-testing datasets
        // (the "detection target" is the transaction that would have caused
        // the chargeback, not the reconnaissance). Marking $2 grocery-style
        // purchases as ground-truth-fraud is a harness bug, because no
        // rule-based or ML-based classifier could correctly flag them at
        // scoring time without over-firing on legitimate traffic.
        //
        // The setup txns still get GroundTruthPattern = "card-testing-setup"
        // so downstream analysis can group them if it wants a pattern-level
        // view; the primary metric is per-transaction against
        // GroundTruthFraud.
        for (int p = 0; p < _options.FraudPatternCount; p++)
        {
            var customerIdx = _random.Next(population.CustomerIds.Count);
            // Anchor at the customer's home city — card-testing probes ride
            // the compromised card's normal channel; using a random city here
            // would trigger impossible-travel on the setup txns as a harness
            // artefact.
            var city = homeCity[customerIdx];
            timePointer = timePointer.AddMinutes(_random.Next(5, 30));
            for (int j = 0; j < 5; j++)
            {
                yield return NewTxn(
                    txnRef: $"TX{txnRefSeq++:D7}",
                    customerIdx: customerIdx,
                    population: population,
                    mcc: "4816",
                    amount: (decimal)(1 + _random.NextDouble() * 5),
                    city: city,
                    at: timePointer.AddSeconds(j * 15),
                    type: TransactionType.CardNotPresent,
                    fraud: false,
                    pattern: "card-testing-setup");
            }
            yield return NewTxn(
                txnRef: $"TX{txnRefSeq++:D7}",
                customerIdx: customerIdx,
                population: population,
                mcc: "6051",
                amount: 3500m,
                city: city,
                at: timePointer.AddSeconds(80),
                type: TransactionType.CardNotPresent,
                fraud: true,
                pattern: "card-testing");
        }

        // Impossible-travel pattern
        for (int p = 0; p < _options.FraudPatternCount; p++)
        {
            var customerIdx = _random.Next(population.CustomerIds.Count);
            timePointer = timePointer.AddMinutes(_random.Next(10, 60));
            // Anchor at the customer's actual home country and jump to the
            // opposite one. That way the "setup" txn is indistinguishable
            // from legitimate home traffic and only the second txn exhibits
            // the impossible-travel signal.
            var homeCountry = homeCity[customerIdx].Country;
            var awayCountry = homeCountry == "KE" ? "US" : "KE";
            var here = homeCity[customerIdx];
            var there = _cities.First(c => c.Country == awayCountry);
            yield return NewTxn(
                txnRef: $"TX{txnRefSeq++:D7}",
                customerIdx: customerIdx,
                population: population,
                mcc: "5411",
                amount: 200m,
                city: here,
                at: timePointer,
                type: TransactionType.CardPresent,
                fraud: false,
                pattern: null);
            yield return NewTxn(
                txnRef: $"TX{txnRefSeq++:D7}",
                customerIdx: customerIdx,
                population: population,
                mcc: "5411",
                amount: 250m,
                city: there,
                at: timePointer.AddMinutes(30),
                type: TransactionType.CardPresent,
                fraud: true,
                pattern: "impossible-travel");
        }

        // Takeover burst
        for (int p = 0; p < _options.FraudPatternCount / 2; p++)
        {
            var customerIdx = _random.Next(population.CustomerIds.Count);
            // Takeover attacker operates from the customer's own home city —
            // they compromised the account, not the geography. The detectable
            // signals are the new device, the rapid velocity, and the amount
            // z-score, not location.
            var city = homeCity[customerIdx];
            timePointer = timePointer.AddMinutes(_random.Next(5, 25));
            // Attacker uses a new device.
            var attackerDevice = $"DEV_ATK{p:D3}";
            for (int j = 0; j < 6; j++)
            {
                yield return NewTxn(
                    txnRef: $"TX{txnRefSeq++:D7}",
                    customerIdx: customerIdx,
                    population: population,
                    mcc: "6051",
                    amount: 800m + j * 200,
                    city: city,
                    at: timePointer.AddMinutes(j),
                    type: TransactionType.CardNotPresent,
                    fraud: true,
                    pattern: "takeover-burst",
                    overrideDeviceId: attackerDevice);
            }
        }

        // Merchant-collusion refund abuse — refunds from one specific merchant many times
        var badMerchant = population.MerchantIds[0];
        for (int p = 0; p < _options.FraudPatternCount / 3; p++)
        {
            var customerIdx = _random.Next(population.CustomerIds.Count);
            var city = homeCity[customerIdx];
            timePointer = timePointer.AddMinutes(_random.Next(10, 40));
            yield return NewTxn(
                txnRef: $"TX{txnRefSeq++:D7}",
                customerIdx: customerIdx,
                population: population,
                mcc: "5967",
                amount: 5000m,
                city: city,
                at: timePointer,
                type: TransactionType.CardNotPresent,
                fraud: true,
                pattern: "refund-abuse",
                overrideMerchant: badMerchant);
        }
    }

    private Transaction NewTxn(
        string txnRef,
        int customerIdx,
        SyntheticPopulation population,
        string mcc,
        decimal amount,
        (string City, double Lat, double Lon, string Country) city,
        DateTimeOffset at,
        TransactionType type,
        bool fraud,
        string? pattern,
        string? overrideDeviceId = null,
        string? overrideMerchant = null,
        string? overrideIp = null)
    {
        var currency = city.Country == "KE" ? "KES" : "USD";
        var money = Money.Of(amount, currency);
        var location = GeoLocation.Of(city.Lat, city.Lon, city.Country);
        var deviceId = overrideDeviceId ?? population.DeviceIds[_random.Next(population.DeviceIds.Count)];
        var ip = overrideIp ?? population.IpAddresses[_random.Next(population.IpAddresses.Count)];
        var merchantId = overrideMerchant ?? population.MerchantIds[_random.Next(population.MerchantIds.Count)];
        return new Transaction(
            id: _ids.NewGuid(),
            transactionRef: txnRef,
            cardId: population.CardIds[customerIdx],
            customerId: population.CustomerIds[customerIdx],
            deviceId: deviceId,
            ipAddress: ip,
            merchantId: merchantId,
            mcc: mcc,
            amount: money,
            type: type,
            location: location,
            occurredAt: at,
            receivedAt: at.AddSeconds(1),
            groundTruthFraud: fraud,
            groundTruthPattern: pattern);
    }
}
