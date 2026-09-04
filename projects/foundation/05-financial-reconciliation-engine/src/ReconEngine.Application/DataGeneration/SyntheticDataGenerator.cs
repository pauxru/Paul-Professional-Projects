using ReconEngine.Application.Ingestion;
using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;
using ReconEngine.Domain.Normalization;
using ReconEngine.Domain.ValueObjects;

namespace ReconEngine.Application.DataGeneration;

/// <summary>An in-memory generated dataset: the two record sets plus the ground-truth manifest.</summary>
public sealed record GeneratedDataset(
    IReadOnlyList<ReconRecord> Internal,
    IReadOnlyList<ReconRecord> External,
    DefectManifest Manifest);

/// <summary>
/// Deterministic synthetic data generator. It emits a matched internal + external pair with a
/// <b>controlled, known</b> number of each defect class. The defects are deliberately isolated so the
/// reconciliation pipeline detects exactly the injected counts:
/// <list type="bullet">
///   <item>clean pairs are removed by the priority-1 exact-reference rule before any fuzzy rule runs,
///   so only defects remain in the candidate pool;</item>
///   <item>each fuzzy-detectable defect sits on its own value-date "slot" (8 days apart, wider than the
///   fuzzy/subset windows) so no two defects can cross-match;</item>
///   <item>duplicates ride on clean pairs (they collapse under the row hash before matching);</item>
///   <item>status mismatches exact-match then flag, so they never pollute the fuzzy pool.</item>
/// </list>
/// This isolation-by-construction is what makes exact-count assertions honest rather than flaky.
/// </summary>
public sealed class SyntheticDataGenerator
{
    // Fixed defect volumes (independent of --rows) so a tiny test set and a 250k perf set share the
    // same known counts. Clean pairs absorb the remainder of --rows.
    private const int DupInternalCount = 50;
    private const int DupExternalCount = 30;
    private const int AmountMismatchCount = 40;
    private const int MissingExternalCount = 30;
    private const int MissingInternalCount = 20;
    private const int CurrencyMismatchCount = 20;
    private const int StatusMismatchCount = 20;
    private const int DateOutOfWindowCount = 15;
    private const int FeeCleanCount = 100;
    private const int FeeVarianceCount = 25;
    private const int RefundCount = 25;

    private static readonly string[] Currencies = { "KES", "USD", "EUR" };

    private static readonly ReferenceCanonicalizationRules Canon =
        BuiltInProfiles.InternalCsv.Canonicalization;

    private static readonly DateOnly CleanRegionStart = new(2023, 1, 2);
    private static readonly DateOnly SlotRegionStart = new(2024, 1, 1);
    private static readonly DateOnly DateOutRegionStart = new(2030, 1, 1);
    private static readonly DateTime IngestedAt = new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private int _nextTxnId = 1;
    private int _nextMerchantId = 1;
    private int _internalLine = 1;
    private int _externalLine = 1;
    private int _slot;

    public GeneratedDataset Generate(GenerationOptions opt)
    {
        _nextTxnId = 1;
        _nextMerchantId = 1;
        _internalLine = 1;
        _externalLine = 1;
        _slot = 0;

        var rng = new Random(opt.Seed);
        var internals = new List<ReconRecord>();
        var externals = new List<ReconRecord>();

        var dupInt = opt.Duplicates ? DupInternalCount : 0;
        var dupExt = opt.Duplicates ? DupExternalCount : 0;
        var am = opt.AmountMismatch ? AmountMismatchCount : 0;
        var me = opt.Missing ? MissingExternalCount : 0;
        var mi = opt.Missing ? MissingInternalCount : 0;
        var cm = opt.CurrencyMismatch ? CurrencyMismatchCount : 0;
        var sm = opt.StatusMismatch ? StatusMismatchCount : 0;
        var dw = opt.DateOutOfWindow ? DateOutOfWindowCount : 0;
        var fc = opt.Fees ? FeeCleanCount : 0;
        var fv = opt.Fees ? FeeVarianceCount : 0;
        var rf = opt.Refunds ? RefundCount : 0;

        var primaryDefects = am + me + cm + sm + dw + fc + fv + rf;
        var clean = Math.Max(0, opt.Rows - primaryDefects);

        // 1) Clean, exactly-reconciling pairs (the bulk). A prefix of them also carries duplicate copies
        //    so those defects ride real matched pairs (the original still reconciles).
        for (var i = 0; i < clean; i++)
        {
            var currency = Currencies[i % Currencies.Length];
            var amount = RandomAmount(rng, 100_00, 5_000_00);
            var day = CleanRegionStart.AddDays(i % 90);
            var (txnRef, stlRef, merchant) = NextPairRefs();

            internals.Add(BuildInternal(txnRef, merchant, amount, currency, null, day, TransactionStatus.Captured));
            externals.Add(BuildExternal(stlRef, merchant, amount, currency, null, day, TransactionStatus.Settled));

            // Duplicate internal: an identical extra copy collapses under the row hash → DuplicateInternal.
            if (i < dupInt)
                internals.Add(BuildInternal(txnRef, merchant, amount, currency, null, day, TransactionStatus.Captured));

            // Duplicate external: an identical extra settlement line → DuplicateExternal.
            if (i >= dupInt && i < dupInt + dupExt)
                externals.Add(BuildExternal(stlRef, merchant, amount, currency, null, day, TransactionStatus.Settled));
        }

        // 1b) Status mismatch: reconciles by reference + amount + currency (so it is a match), but the
        //     lifecycle states contradict (internal reversed vs external settled) → StatusMismatch flag.
        for (var i = 0; i < sm; i++)
        {
            var currency = Currencies[i % Currencies.Length];
            var amount = RandomAmount(rng, 100_00, 900_00);
            var day = CleanRegionStart.AddDays(i % 90);
            var (txnRef, stlRef, merchant) = NextPairRefs();
            internals.Add(BuildInternal(txnRef, merchant, amount, currency, null, day, TransactionStatus.Reversed));
            externals.Add(BuildExternal(stlRef, merchant, amount, currency, null, day, TransactionStatus.Settled));
        }

        // 2) Amount mismatch: same canonical reference, amount differs beyond every tolerance.
        for (var i = 0; i < am; i++)
        {
            var currency = Currencies[i % Currencies.Length];
            var g1 = RandomAmount(rng, 200_00, 900_00);
            var g2 = g1 + 15_00; // 15.00 apart — outside both the fuzzy (0.50) and composite (0.00) tolerances
            var day = NextSlotDay();
            var (txnRef, stlRef, _) = NextPairRefs();
            internals.Add(BuildInternal(txnRef, NextMerchant(), g1, currency, null, day, TransactionStatus.Captured));
            externals.Add(BuildExternal(stlRef, NextMerchant(), g2, currency, null, day, TransactionStatus.Settled));
        }

        // 3) Missing in external: an internal transaction the PSP never settled.
        for (var i = 0; i < me; i++)
        {
            var currency = Currencies[i % Currencies.Length];
            var day = NextSlotDay();
            internals.Add(BuildInternal(NextTxnRef(), NextMerchant(), RandomAmount(rng, 100_00, 900_00), currency, null, day, TransactionStatus.Captured));
        }

        // 4) Missing in internal: a settlement line with no matching internal record.
        for (var i = 0; i < mi; i++)
        {
            var currency = Currencies[i % Currencies.Length];
            var day = NextSlotDay();
            externals.Add(BuildExternal(NextStlRef(), NextMerchant(), RandomAmount(rng, 100_00, 900_00), currency, null, day, TransactionStatus.Settled));
        }

        // 5) Currency mismatch: same reference, different currency (must never net across currencies).
        for (var i = 0; i < cm; i++)
        {
            var amount = RandomAmount(rng, 100_00, 900_00);
            var day = NextSlotDay();
            var (txnRef, stlRef, _) = NextPairRefs();
            internals.Add(BuildInternal(txnRef, NextMerchant(), amount, "KES", null, day, TransactionStatus.Captured));
            externals.Add(BuildExternal(stlRef, NextMerchant(), amount, "USD", null, day, TransactionStatus.Settled));
        }

        // 6) Fee-adjusted clean: internal gross == external net + fee, fee exactly on schedule.
        for (var i = 0; i < fc; i++)
        {
            var currency = Currencies[i % Currencies.Length];
            var gross = RandomAmount(rng, 1_000_00, 5_000_00);
            var fee = FeeSchedule.Default.ExpectedFeeMinor(gross, currency);
            var net = gross - fee;
            var day = NextSlotDay();
            var (txnRef, stlRef, _) = NextPairRefs();
            internals.Add(BuildInternal(txnRef, NextMerchant(), gross, currency, null, day, TransactionStatus.Captured));
            externals.Add(BuildExternal(stlRef, NextMerchant(), net, currency, fee, day, TransactionStatus.Settled));
        }

        // 7) Fee variance: fee deviates from the schedule by more than tolerance → still matched, but flagged.
        for (var i = 0; i < fv; i++)
        {
            var currency = Currencies[i % Currencies.Length];
            var gross = RandomAmount(rng, 1_000_00, 5_000_00);
            var expected = FeeSchedule.Default.ExpectedFeeMinor(gross, currency);
            var actual = expected + 2_00; // 2.00 over the expected fee — a clear variance
            var net = gross - actual;
            var day = NextSlotDay();
            var (txnRef, stlRef, _) = NextPairRefs();
            internals.Add(BuildInternal(txnRef, NextMerchant(), gross, currency, null, day, TransactionStatus.Captured));
            externals.Add(BuildExternal(stlRef, NextMerchant(), net, currency, actual, day, TransactionStatus.Settled));
        }

        // 8) Refund: negative internal pairs to a negative settlement of the same magnitude.
        for (var i = 0; i < rf; i++)
        {
            var currency = Currencies[i % Currencies.Length];
            var magnitude = RandomAmount(rng, 100_00, 900_00);
            var day = NextSlotDay();
            // Distinct references so the refund is matched by the dedicated rule, not the exact rule.
            var txnRef = NextTxnRef();
            var stlRef = NextStlRef();
            internals.Add(BuildInternal(txnRef, NextMerchant(), -magnitude, currency, null, day, TransactionStatus.Refunded));
            externals.Add(BuildExternal(stlRef, NextMerchant(), -magnitude, currency, null, day, TransactionStatus.Refunded));
        }

        // 9) Date out of window: shared merchant reference + equal amount but settled far outside the window.
        for (var i = 0; i < dw; i++)
        {
            var currency = Currencies[i % Currencies.Length];
            var amount = RandomAmount(rng, 100_00, 900_00);
            var internalDay = DateOutRegionStart.AddDays(i * 30);
            var externalDay = internalDay.AddDays(10); // beyond composite (2) and fuzzy (3) windows
            var merchant = NextMerchant();
            internals.Add(BuildInternal(NextTxnRef(), merchant, amount, currency, null, internalDay, TransactionStatus.Captured));
            externals.Add(BuildExternal(NextStlRef(), merchant, amount, currency, null, externalDay, TransactionStatus.Settled));
        }

        var manifest = new DefectManifest
        {
            Rows = opt.Rows,
            Seed = opt.Seed,
            CleanPairs = clean,
            DuplicateInternal = dupInt,
            DuplicateExternal = dupExt,
            AmountMismatch = am,
            MissingInExternal = me,
            MissingInInternal = mi,
            CurrencyMismatch = cm,
            StatusMismatch = sm,
            DateOutOfWindow = dw,
            FeeAdjustedClean = fc,
            FeeVariance = fv,
            Refunds = rf,
            TotalInternalRows = internals.Count,
            TotalExternalRows = externals.Count,
        };

        return new GeneratedDataset(internals, externals, manifest);
    }

    private static long RandomAmount(Random rng, long minMinor, long maxMinor) =>
        rng.NextInt64(minMinor, maxMinor + 1);

    private (string txnRef, string stlRef, string merchant) NextPairRefs()
    {
        var id = _nextTxnId++;
        var merchant = $"MOID_{_nextMerchantId++:D9}";
        return ($"TXN_{id:D9}", $"STL_{id:D9}", merchant);
    }

    private string NextTxnRef() => $"TXN_{_nextTxnId++:D9}";
    private string NextStlRef() => $"STL_{_nextTxnId++:D9}";
    private string NextMerchant() => $"MOID_{_nextMerchantId++:D9}";

    /// <summary>Hand out a fresh, isolated value-date slot (8 days apart, wider than any match window).</summary>
    private DateOnly NextSlotDay() => SlotRegionStart.AddDays(_slot++ * 8);

    private ReconRecord BuildInternal(
        string rawRef, string merchant, long amountMinor, string currency, long? feeMinor, DateOnly day, TransactionStatus status) =>
        Build(RecordSource.Internal, rawRef, merchant, amountMinor, currency, feeMinor, day, status, "+03:00", ref _internalLine);

    private ReconRecord BuildExternal(
        string rawRef, string merchant, long amountMinor, string currency, long? feeMinor, DateOnly day, TransactionStatus status) =>
        Build(RecordSource.External, rawRef, merchant, amountMinor, currency, feeMinor, day, status, "+00:00", ref _externalLine);

    private static ReconRecord Build(
        RecordSource source, string rawRef, string merchant, long amountMinor, string currency,
        long? feeMinor, DateOnly day, TransactionStatus status, string offset, ref int line)
    {
        var canonical = ReferenceCanonicalizer.Canonicalize(rawRef, Canon);
        // Internal rows are stamped at local noon (Nairobi +03:00 → 09:00 UTC same day); external rows at
        // UTC midnight. Both therefore land on the same UTC value date, which is what the windows use.
        var utc = source == RecordSource.Internal
            ? new DateTime(day.Year, day.Month, day.Day, 9, 0, 0, DateTimeKind.Utc)
            : new DateTime(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Utc);

        var record = new ReconRecord
        {
            Source = source,
            RawReference = rawRef,
            CanonicalReference = canonical,
            CounterpartyReference = ReferenceCanonicalizer.Canonicalize(merchant, Canon),
            AmountMinor = amountMinor,
            Currency = currency,
            FeeMinor = feeMinor,
            TransactionDateUtc = utc,
            ValueDate = day,
            SourceTimeZone = offset,
            Status = status,
            LineNumber = line++,
            IngestedAtUtc = IngestedAt,
            ReconStatus = ReconStatus.Pending,
        };
        record.RowHash = RowHasher.Hash(source.ToString(), canonical, amountMinor, currency, day, status.ToString());
        return record;
    }
}
