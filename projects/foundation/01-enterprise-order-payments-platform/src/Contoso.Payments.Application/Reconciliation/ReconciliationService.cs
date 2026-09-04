using Microsoft.EntityFrameworkCore;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Domain.Payments;

namespace Contoso.Payments.Application.Reconciliation;

/// <summary>
/// Compares a provider settlement file against internal payment intents and captures a run
/// summary plus one <see cref="ReconciliationDiscrepancy"/> row per defect found.
/// </summary>
public sealed class ReconciliationService
{
    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public ReconciliationService(IAppDbContext db, IClock clock, IIdGenerator ids)
    {
        _db = db;
        _clock = clock;
        _ids = ids;
    }

    public async Task<ReconciliationRun> RunAsync(string sourceFileName, IReadOnlyList<SettlementRow> providerRows, CancellationToken ct)
    {
        var run = new ReconciliationRun
        {
            Id = _ids.NewGuid(),
            StartedAtUtc = _clock.UtcNow,
            SourceFileName = sourceFileName,
            TotalProviderRows = providerRows.Count
        };

        var internalIntents = await _db.PaymentIntents.AsNoTracking()
            .Where(p => p.Status == PaymentIntentStatus.Captured ||
                        p.Status == PaymentIntentStatus.Authorized ||
                        p.Status == PaymentIntentStatus.Voided ||
                        p.Status == PaymentIntentStatus.Failed)
            .ToListAsync(ct);
        run.TotalInternalRows = internalIntents.Count;

        var providerById = providerRows
            .Where(r => r.PaymentIntentId is not null)
            .GroupBy(r => r.PaymentIntentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var providerByRef = providerRows
            .GroupBy(r => r.ProviderReference)
            .ToDictionary(g => g.Key, g => g.ToList());

        var discrepancies = new List<ReconciliationDiscrepancy>();

        foreach (var intent in internalIntents)
        {
            if (!providerById.TryGetValue(intent.Id, out var matches))
            {
                discrepancies.Add(new ReconciliationDiscrepancy
                {
                    Id = _ids.NewGuid(),
                    RunId = run.Id,
                    Kind = DiscrepancyKind.MissingInProvider,
                    PaymentIntentId = intent.Id.ToString(),
                    InternalMinorUnits = intent.CapturedAmount.ToMinorUnits(),
                    Currency = intent.Amount.Currency,
                    InternalStatus = intent.Status.ToString(),
                    Notes = "Internal intent has no matching provider row"
                });
                run.MissingInProviderCount++;
                continue;
            }
            if (matches.Count > 1)
            {
                discrepancies.Add(new ReconciliationDiscrepancy
                {
                    Id = _ids.NewGuid(),
                    RunId = run.Id,
                    Kind = DiscrepancyKind.DuplicateInProvider,
                    PaymentIntentId = intent.Id.ToString(),
                    Currency = intent.Amount.Currency,
                    Notes = $"Provider file contains {matches.Count} rows for this intent"
                });
                run.DuplicateCount++;
            }
            var match = matches[0];
            var internalAmount = intent.Status == PaymentIntentStatus.Captured
                ? intent.CapturedAmount.ToMinorUnits()
                : intent.Amount.ToMinorUnits();
            if (match.AmountMinor != internalAmount)
            {
                discrepancies.Add(new ReconciliationDiscrepancy
                {
                    Id = _ids.NewGuid(),
                    RunId = run.Id,
                    Kind = DiscrepancyKind.AmountMismatch,
                    PaymentIntentId = intent.Id.ToString(),
                    ProviderReference = match.ProviderReference,
                    InternalMinorUnits = internalAmount,
                    ProviderMinorUnits = match.AmountMinor,
                    Currency = intent.Amount.Currency,
                    Notes = "Amount differs between provider and internal"
                });
                run.AmountMismatchCount++;
                continue;
            }
            var normalizedProviderStatus = NormalizeProviderStatus(match.Status);
            var normalizedInternalStatus = intent.Status.ToString();
            if (!string.Equals(normalizedProviderStatus, normalizedInternalStatus, StringComparison.OrdinalIgnoreCase))
            {
                discrepancies.Add(new ReconciliationDiscrepancy
                {
                    Id = _ids.NewGuid(),
                    RunId = run.Id,
                    Kind = DiscrepancyKind.StatusMismatch,
                    PaymentIntentId = intent.Id.ToString(),
                    ProviderReference = match.ProviderReference,
                    Currency = intent.Amount.Currency,
                    InternalStatus = normalizedInternalStatus,
                    ProviderStatus = match.Status,
                    Notes = "Status differs"
                });
                run.StatusMismatchCount++;
                continue;
            }
            run.MatchedCount++;
        }

        var internalIds = internalIntents.Select(i => i.Id).ToHashSet();
        foreach (var row in providerRows)
        {
            if (row.PaymentIntentId is null || !internalIds.Contains(row.PaymentIntentId.Value))
            {
                discrepancies.Add(new ReconciliationDiscrepancy
                {
                    Id = _ids.NewGuid(),
                    RunId = run.Id,
                    Kind = DiscrepancyKind.MissingInternally,
                    ProviderReference = row.ProviderReference,
                    PaymentIntentId = row.PaymentIntentId?.ToString(),
                    ProviderMinorUnits = row.AmountMinor,
                    Currency = row.Currency,
                    ProviderStatus = row.Status,
                    Notes = "Provider reports a payment that we have no record of"
                });
                run.MissingInternallyCount++;
            }
        }

        run.CompletedAtUtc = _clock.UtcNow;
        _db.ReconciliationRuns.Add(run);
        foreach (var d in discrepancies) _db.ReconciliationDiscrepancies.Add(d);
        await _db.SaveChangesAsync(ct);
        return run;
    }

    private static string NormalizeProviderStatus(string providerStatus) => providerStatus switch
    {
        "Captured" or "captured" or "CAPTURED" => "Captured",
        "Authorized" or "authorized" or "AUTHORIZED" => "Authorized",
        "Failed" or "failed" or "FAILED" => "Failed",
        "Voided" or "voided" or "VOIDED" => "Voided",
        _ => providerStatus
    };
}
