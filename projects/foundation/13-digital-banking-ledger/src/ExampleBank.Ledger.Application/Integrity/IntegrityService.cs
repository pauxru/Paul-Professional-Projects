using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Domain.Journal;

namespace ExampleBank.Ledger.Application.Integrity;

/// <summary>
/// Verifies ledger integrity: (1) the append-only hash chain is intact and untampered, and
/// (2) each account's cached balance reconciles exactly with the sum of its postings.
/// </summary>
public sealed class IntegrityService
{
    private readonly ILedgerUnitOfWorkFactory _uowFactory;

    public IntegrityService(ILedgerUnitOfWorkFactory uowFactory) => _uowFactory = uowFactory;

    public async Task<IntegrityReport> VerifyAsync(CancellationToken cancellationToken)
    {
        await using var uow = await _uowFactory.CreateAsync(cancellationToken);
        var chain = await VerifyChainAsync(uow, cancellationToken);
        var reconciliation = await ReconcileAsync(uow, cancellationToken);
        return new IntegrityReport(chain, reconciliation);
    }

    public async Task<ChainVerificationResult> VerifyChainAsync(CancellationToken cancellationToken)
    {
        await using var uow = await _uowFactory.CreateAsync(cancellationToken);
        return await VerifyChainAsync(uow, cancellationToken);
    }

    private static async Task<ChainVerificationResult> VerifyChainAsync(ILedgerUnitOfWork uow, CancellationToken cancellationToken)
    {
        var entries = await uow.Journal.GetChainAsync(cancellationToken);
        string previousHash = LedgerHash.GenesisHash;
        long expectedSequence = 1;
        int entriesChecked = 0;

        foreach (var entry in entries)
        {
            entriesChecked++;

            if (entry.SequenceNumber != expectedSequence)
            {
                return new ChainVerificationResult(false, entriesChecked, entry.SequenceNumber,
                    $"Sequence gap: expected {expectedSequence}, found {entry.SequenceNumber}.");
            }

            if (entry.PreviousHash != previousHash)
            {
                return new ChainVerificationResult(false, entriesChecked, entry.SequenceNumber,
                    $"Broken link at sequence {entry.SequenceNumber}: stored previous hash does not match prior entry.");
            }

            var recomputed = entry.RecomputeHash();
            if (recomputed != entry.Hash)
            {
                return new ChainVerificationResult(false, entriesChecked, entry.SequenceNumber,
                    $"Tampered content at sequence {entry.SequenceNumber}: recomputed hash does not match stored hash.");
            }

            previousHash = entry.Hash;
            expectedSequence++;
        }

        return new ChainVerificationResult(true, entriesChecked, null, null);
    }

    private static async Task<ReconciliationResult> ReconcileAsync(ILedgerUnitOfWork uow, CancellationToken cancellationToken)
    {
        var accounts = await uow.Accounts.ListAsync(cancellationToken);
        var discrepancies = new List<AccountReconciliation>();
        int checkedCount = 0;

        foreach (var account in accounts.Where(a => !a.IsControlAccount))
        {
            checkedCount++;
            var (derivedDebits, derivedCredits) =
                await uow.Journal.SumPostingsForAccountAsync(account.Id, cancellationToken);

            bool reconciled = derivedDebits == account.TotalDebitsMinor
                && derivedCredits == account.TotalCreditsMinor;

            if (!reconciled)
            {
                discrepancies.Add(new AccountReconciliation(
                    account.Id, account.Code,
                    account.TotalDebitsMinor, account.TotalCreditsMinor,
                    derivedDebits, derivedCredits, false));
            }
        }

        return new ReconciliationResult(discrepancies.Count == 0, checkedCount, discrepancies);
    }
}
