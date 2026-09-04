using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ReconEngine.Application.Abstractions;
using ReconEngine.Application.Common;
using ReconEngine.Application.Exceptions;
using ReconEngine.Application.Matching;
using ReconEngine.Application.Observability;
using ReconEngine.Domain.Abstractions;
using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;
using ReconEngine.Domain.Normalization;

namespace ReconEngine.Application.Reconciliation;

/// <summary>
/// Loads the working set, runs the pure <see cref="ReconciliationCalculator"/>, then persists the run as
/// an immutable snapshot. Re-running the same inputs is idempotent: prior matches for the working set are
/// replaced, exceptions are upserted by their stable key (so triage on an existing open exception is
/// preserved and no duplicate is created), and resolved discrepancies stay resolved.
/// </summary>
public sealed class ReconciliationOrchestrator
{
    private readonly IRecordStore _records;
    private readonly IRuleSetStore _ruleSets;
    private readonly IRunStore _runs;
    private readonly IMatchStore _matches;
    private readonly IExceptionStore _exceptions;
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;
    private readonly ReconciliationCalculator _calculator;
    private readonly ReconciliationOptions _options;

    public ReconciliationOrchestrator(
        IRecordStore records,
        IRuleSetStore ruleSets,
        IRunStore runs,
        IMatchStore matches,
        IExceptionStore exceptions,
        IUnitOfWork uow,
        IClock clock,
        ReconciliationCalculator calculator,
        IOptions<ReconciliationOptions> options)
    {
        _records = records;
        _ruleSets = ruleSets;
        _runs = runs;
        _matches = matches;
        _exceptions = exceptions;
        _uow = uow;
        _clock = clock;
        _calculator = calculator;
        _options = options.Value;
    }

    public async Task<ReconciliationRun> RunAsync(
        Guid? ruleSetId, DateOnly? from, DateOnly? to, string triggeredBy, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        using var runSpan = ReconDiagnostics.ActivitySource.StartActivity("recon.run");

        var ruleSet = ruleSetId is null
            ? await _ruleSets.GetActiveAsync(ct)
            : await _ruleSets.GetAsync(ruleSetId.Value, ct);
        if (ruleSet is null)
            throw new ConflictException("No ruleset available. Seed or create a ruleset first.");

        var def = ruleSet.GetDefinition();

        List<ReconRecord> internalRecords;
        List<ReconRecord> externalRecords;
        IReadOnlyList<ReconRecord> working;
        using (ReconDiagnostics.ActivitySource.StartActivity("recon.load"))
        {
            working = await _records.GetWorkingSetAsync(from, to, ct);
            internalRecords = working.Where(r => r.Source == RecordSource.Internal)
                .OrderBy(r => r.RowHash, StringComparer.Ordinal).ToList();
            externalRecords = working.Where(r => r.Source == RecordSource.External)
                .OrderBy(r => r.RowHash, StringComparer.Ordinal).ToList();
        }

        var carriedForward = working.Count(r => r.LastRunId != null);

        ReconciliationResult result;
        using (ReconDiagnostics.ActivitySource.StartActivity("recon.calculate"))
        {
            result = _calculator.Calculate(internalRecords, externalRecords, def, _options.HighSeverityAmountMinor);
        }

        var run = new ReconciliationRun
        {
            Id = Guid.NewGuid(),
            RuleSetId = ruleSet.Id,
            RuleSetVersionTag = ruleSet.VersionTag,
            Status = RunStatus.Running,
            WindowFrom = from,
            WindowTo = to,
            StartedAtUtc = _clock.UtcNow,
            TriggeredBy = triggeredBy,
            InputChecksum = RowHasher.Checksum(working.Select(r => r.RowHash)),
        };

        var matchEntities = BuildMatches(result, run.Id, run.RuleSetVersionTag);

        var workingIds = working.Select(r => r.Id).ToHashSet();
        var (toInsert, toRemove, currentOpenCount, breakdown) = await ReconcileExceptionsAsync(result, run.Id, workingIds, ct);

        using (ReconDiagnostics.ActivitySource.StartActivity("recon.persist"))
        {
            // Persist matches (idempotent: clear prior matches for the working set, then add fresh).
            await _matches.RemoveForRecordsAsync(workingIds, ct);
            await _matches.AddRangeAsync(matchEntities, ct);

            await _exceptions.RemoveRangeAsync(toRemove, ct);
            await _exceptions.AddRangeAsync(toInsert, ct);

            UpdateRecordStates(working, result, run.Id);
        }

        run.InternalRecordCount = internalRecords.Count;
        run.ExternalRecordCount = externalRecords.Count;
        run.MatchCount = matchEntities.Count;
        run.MatchedInternalCount = result.MatchedInternalIds.Count;
        run.MatchedExternalCount = result.MatchedExternalIds.Count;
        run.CarriedForwardCount = carriedForward;
        run.ExceptionCount = currentOpenCount;
        run.TotalsJson = JsonSerializer.Serialize(result.Totals);
        run.ExceptionBreakdownJson = JsonSerializer.Serialize(breakdown);
        run.BalanceAssertionPassed = result.BalancePassed;
        run.BalanceAssertionDetail = result.BalanceDetail;
        run.Status = result.BalancePassed ? RunStatus.Completed : RunStatus.Failed;
        run.CompletedAtUtc = _clock.UtcNow;
        stopwatch.Stop();
        run.DurationMs = stopwatch.ElapsedMilliseconds;

        await _runs.AddAsync(run, ct);
        await _uow.SaveChangesAsync(ct);

        if (!result.BalancePassed)
            throw new BalanceAssertionException(result.BalanceDetail ?? "Balance assertion failed.");

        return run;
    }

    private List<Match> BuildMatches(ReconciliationResult result, Guid runId, string versionTag)
    {
        var matches = new List<Match>(result.Matches.Count);
        foreach (var candidate in result.Matches)
        {
            var match = new Match
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                RuleId = candidate.RuleId,
                RuleSetVersionTag = versionTag,
                MatchType = candidate.Kind,
                Confidence = candidate.Confidence,
                Explanation = candidate.Explanation,
                Currency = candidate.Currency,
                InternalAmountMinor = candidate.InternalAmountMinor,
                ExternalAmountMinor = candidate.ExternalAmountMinor,
                ExpectedFeeMinor = candidate.ExpectedFeeMinor,
                FeeVarianceMinor = candidate.FeeVarianceMinor,
                CreatedAtUtc = _clock.UtcNow,
            };
            foreach (var r in candidate.Internals)
                match.Entries.Add(new MatchEntry { MatchId = match.Id, RecordId = r.Id, Side = RecordSource.Internal });
            foreach (var r in candidate.Externals)
                match.Entries.Add(new MatchEntry { MatchId = match.Id, RecordId = r.Id, Side = RecordSource.External });
            matches.Add(match);
        }
        return matches;
    }

    private async Task<(List<ReconciliationException> ToInsert, List<ReconciliationException> ToRemove, int CurrentOpen, Dictionary<string, int> Breakdown)>
        ReconcileExceptionsAsync(ReconciliationResult result, Guid runId, HashSet<Guid> workingIds, CancellationToken ct)
    {
        var existingOpen = await _exceptions.GetOpenAsync(ct);
        var inScopeExisting = existingOpen
            .Where(e => e.GetRecordIds().Any(workingIds.Contains))
            .ToList();

        var existingByKey = new Dictionary<string, ReconciliationException>(StringComparer.Ordinal);
        foreach (var e in inScopeExisting)
            existingByKey.TryAdd(e.ExceptionKey, e);

        var desiredByKey = new Dictionary<string, ExceptionDraft>(StringComparer.Ordinal);
        foreach (var d in result.Exceptions)
            desiredByKey[d.ExceptionKey] = d;

        var toInsert = new List<ReconciliationException>();
        foreach (var (key, draft) in desiredByKey)
        {
            if (existingByKey.ContainsKey(key))
                continue; // preserve the existing (possibly triaged) exception

            var ex = new ReconciliationException
            {
                Id = Guid.NewGuid(),
                ExceptionKey = key,
                CreatedByRunId = runId,
                Type = draft.Type,
                Severity = draft.Severity,
                SuggestedAction = draft.SuggestedAction,
                Currency = draft.Currency,
                AmountMinor = draft.AmountMinor,
                CreatedAtUtc = _clock.UtcNow,
                UpdatedAtUtc = _clock.UtcNow,
            };
            ex.SetRecordIds(draft.RecordIds);
            toInsert.Add(ex);
        }

        var desiredKeys = desiredByKey.Keys.ToHashSet(StringComparer.Ordinal);
        var toRemove = inScopeExisting.Where(e => !desiredKeys.Contains(e.ExceptionKey)).ToList();

        var currentOpen = inScopeExisting.Count(e => desiredKeys.Contains(e.ExceptionKey)) + toInsert.Count;
        var breakdown = result.ExceptionBreakdown.ToDictionary(kv => kv.Key, kv => kv.Value);

        return (toInsert, toRemove, currentOpen, breakdown);
    }

    private void UpdateRecordStates(IReadOnlyList<ReconRecord> working, ReconciliationResult result, Guid runId)
    {
        foreach (var r in working)
        {
            r.ReconStatus = result.MatchedInternalIds.Contains(r.Id) || result.MatchedExternalIds.Contains(r.Id)
                ? ReconStatus.Matched
                : ReconStatus.Exception;
            r.LastRunId = runId;
        }
    }
}
