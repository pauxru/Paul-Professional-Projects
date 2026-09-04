using System.Text.Json;
using ReconEngine.Application.Abstractions;
using ReconEngine.Application.Common;
using ReconEngine.Application.Reconciliation;
using ReconEngine.Domain.Abstractions;
using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;

namespace ReconEngine.Application.Reporting;

public sealed record RunSummaryReport(
    Guid RunId,
    string RuleSetVersionTag,
    RunStatus Status,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    long DurationMs,
    int InternalRecordCount,
    int ExternalRecordCount,
    int MatchCount,
    int MatchedInternalCount,
    int MatchedExternalCount,
    int CarriedForwardCount,
    int ExceptionCount,
    bool BalanceAssertionPassed,
    string? BalanceAssertionDetail,
    IReadOnlyList<CurrencyTotals> Totals,
    IReadOnlyDictionary<string, int> ExceptionBreakdown);

public sealed record AgingRow(string Currency, string Bucket, int Count, long AbsValueMinor);

public sealed record ValueByCurrencyRow(
    string Currency, long InternalMinor, long MatchedInternalMinor, long UnmatchedInternalMinor,
    long ExternalMinor, long MatchedExternalMinor, long UnmatchedExternalMinor);

public sealed record ValueByDayRow(DateOnly Date, string Currency, int MatchedCount, long MatchedMinor, int UnmatchedCount, long UnmatchedMinor);

public sealed record FeeReconciliationRow(string Currency, int FeeAdjustedMatches, long ExpectedFeeMinor, long FeeVarianceMinor, int VarianceCount);

public sealed record BalanceRow(string Currency, long InternalMinor, long MatchedPlusUnmatchedMinor, bool Balanced);

public sealed record BalanceReport(bool Passed, IReadOnlyList<BalanceRow> Rows);

/// <summary>
/// Builds the reconciliation reports from the immutable run snapshot and the current record/exception
/// state: the run summary, exception aging buckets, matched/unmatched value by currency and by day, fee
/// reconciliation, and a balance assertion that re-derives the partition invariant per currency.
/// </summary>
public sealed class ReportService
{
    private static readonly (string Label, int MaxDays)[] AgingBuckets =
    {
        ("0-1d", 1), ("1-3d", 3), ("3-7d", 7), ("7-30d", 30), ("30d+", int.MaxValue),
    };

    private readonly IRunStore _runs;
    private readonly IExceptionStore _exceptions;
    private readonly IRecordStore _records;
    private readonly IMatchStore _matches;
    private readonly IClock _clock;

    public ReportService(IRunStore runs, IExceptionStore exceptions, IRecordStore records, IMatchStore matches, IClock clock)
    {
        _runs = runs;
        _exceptions = exceptions;
        _records = records;
        _matches = matches;
        _clock = clock;
    }

    public async Task<RunSummaryReport> RunSummaryAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _runs.GetAsync(runId, ct)
            ?? throw new NotFoundException($"Run {runId} not found.");

        var totals = JsonSerializer.Deserialize<List<CurrencyTotals>>(run.TotalsJson) ?? new List<CurrencyTotals>();
        var breakdown = JsonSerializer.Deserialize<Dictionary<string, int>>(run.ExceptionBreakdownJson) ?? new Dictionary<string, int>();

        return new RunSummaryReport(
            run.Id, run.RuleSetVersionTag, run.Status, run.StartedAtUtc, run.CompletedAtUtc, run.DurationMs,
            run.InternalRecordCount, run.ExternalRecordCount, run.MatchCount, run.MatchedInternalCount,
            run.MatchedExternalCount, run.CarriedForwardCount, run.ExceptionCount,
            run.BalanceAssertionPassed, run.BalanceAssertionDetail, totals, breakdown);
    }

    public async Task<IReadOnlyList<AgingRow>> AgingAsync(CancellationToken ct = default)
    {
        var open = await _exceptions.GetOpenAsync(ct);
        var now = _clock.UtcNow;

        var rows = new List<AgingRow>();
        var grouped = open
            .GroupBy(e => (e.Currency, Bucket: BucketFor(now - e.CreatedAtUtc)));

        foreach (var g in grouped)
        {
            rows.Add(new AgingRow(
                g.Key.Currency,
                g.Key.Bucket,
                g.Count(),
                g.Sum(e => Math.Abs(e.AmountMinor))));
        }

        return rows
            .OrderBy(r => r.Currency, StringComparer.Ordinal)
            .ThenBy(r => Array.FindIndex(AgingBuckets, b => b.Label == r.Bucket))
            .ToList();
    }

    public async Task<IReadOnlyList<ValueByCurrencyRow>> ValueByCurrencyAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _runs.GetAsync(runId, ct)
            ?? throw new NotFoundException($"Run {runId} not found.");
        var totals = JsonSerializer.Deserialize<List<CurrencyTotals>>(run.TotalsJson) ?? new List<CurrencyTotals>();

        return totals.Select(t => new ValueByCurrencyRow(
            t.Currency, t.InternalMinor, t.MatchedInternalMinor, t.UnmatchedInternalMinor,
            t.ExternalMinor, t.MatchedExternalMinor, t.UnmatchedExternalMinor)).ToList();
    }

    public async Task<IReadOnlyList<ValueByDayRow>> ValueByDayAsync(Guid runId, CancellationToken ct = default)
    {
        var records = await _records.GetByLastRunAsync(runId, ct);

        return records
            .GroupBy(r => (r.ValueDate, r.Currency))
            .Select(g =>
            {
                var matched = g.Where(r => r.ReconStatus == ReconStatus.Matched).ToList();
                var unmatched = g.Where(r => r.ReconStatus != ReconStatus.Matched).ToList();
                return new ValueByDayRow(
                    g.Key.ValueDate, g.Key.Currency,
                    matched.Count, matched.Sum(r => r.AmountMinor),
                    unmatched.Count, unmatched.Sum(r => r.AmountMinor));
            })
            .OrderBy(r => r.Date)
            .ThenBy(r => r.Currency, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<FeeReconciliationRow>> FeeReconciliationAsync(Guid runId, CancellationToken ct = default)
    {
        var matches = await _matches.GetByRunAsync(runId, ct);

        return matches
            .Where(m => m.ExpectedFeeMinor is not null)
            .GroupBy(m => m.Currency)
            .Select(g => new FeeReconciliationRow(
                g.Key,
                g.Count(),
                g.Sum(m => m.ExpectedFeeMinor ?? 0),
                g.Sum(m => m.FeeVarianceMinor ?? 0),
                g.Count(m => (m.FeeVarianceMinor ?? 0) != 0)))
            .OrderBy(r => r.Currency, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<BalanceReport> BalanceAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _runs.GetAsync(runId, ct)
            ?? throw new NotFoundException($"Run {runId} not found.");
        var totals = JsonSerializer.Deserialize<List<CurrencyTotals>>(run.TotalsJson) ?? new List<CurrencyTotals>();

        var rows = totals.Select(t =>
        {
            var sum = t.MatchedInternalMinor + t.UnmatchedInternalMinor;
            return new BalanceRow(t.Currency, t.InternalMinor, sum, t.InternalMinor == sum);
        }).ToList();

        return new BalanceReport(rows.All(r => r.Balanced), rows);
    }

    private static string BucketFor(TimeSpan age)
    {
        var days = age.TotalDays;
        foreach (var (label, maxDays) in AgingBuckets)
        {
            if (days < maxDays)
                return label;
        }
        return AgingBuckets[^1].Label;
    }
}

