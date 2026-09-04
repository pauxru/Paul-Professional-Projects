using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Journal;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.Application.Reversals;

public sealed record ReversalRequest(
    Guid OriginalEntryId,
    long? AmountMinor = null,
    string? Reason = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null);

/// <summary>
/// Posts correction entries. A reversal references the original entry, produces mirror-image
/// postings, and can be full or partial — but the cumulative reversed amount can never exceed the
/// original, so an entry cannot be reversed twice beyond its value.
/// </summary>
public sealed class ReversalService
{
    private readonly LedgerCommandExecutor _executor;
    private readonly ILedgerUnitOfWorkFactory _uowFactory;
    private readonly IClock _clock;

    public ReversalService(LedgerCommandExecutor executor, ILedgerUnitOfWorkFactory uowFactory, IClock clock)
    {
        _executor = executor;
        _uowFactory = uowFactory;
        _clock = clock;
    }

    public async Task<EntryResult> ReverseAsync(ReversalRequest request, CancellationToken cancellationToken)
    {
        // Resolve the accounts to lock up front (all accounts referenced by the original entry).
        Guid[] accountIds;
        await using (var read = await _uowFactory.CreateAsync(cancellationToken))
        {
            var original = await read.Journal.GetByIdAsync(request.OriginalEntryId, cancellationToken)
                ?? throw new NotFoundException($"Journal entry {request.OriginalEntryId} not found.");
            accountIds = original.Postings.Select(p => p.AccountId).Distinct().ToArray();
        }

        var command = new EntryCommand(
            "reversal",
            request.IdempotencyKey,
            accountIds,
            (uow, ct) => BuildAsync(uow, request, ct));

        return await _executor.ExecuteAsync(command, cancellationToken);
    }

    private async Task<JournalEntry> BuildAsync(ILedgerUnitOfWork uow, ReversalRequest request, CancellationToken cancellationToken)
    {
        var original = await uow.Journal.GetByIdAsync(request.OriginalEntryId, cancellationToken)
            ?? throw new NotFoundException($"Journal entry {request.OriginalEntryId} not found.");

        if (original.Type == EntryType.Reversal)
        {
            throw new DomainException("reversal.of_reversal", "A reversal entry cannot itself be reversed.");
        }

        var reversals = await uow.Journal.GetReversalsOfAsync(original.Id, cancellationToken);
        var primaryCurrency = Currency.FromCode(original.Postings[0].Currency);

        var mirrorPostings = new List<Posting>();
        bool isSimpleTwoLegged = original.Postings.Count == 2
            && original.Postings.Select(p => p.Currency).Distinct().Count() == 1;

        if (request.AmountMinor is { } requestedAmount)
        {
            if (!isSimpleTwoLegged)
            {
                throw new DomainException("reversal.partial_unsupported",
                    "Partial reversals are only supported for simple two-legged, single-currency entries.");
            }

            long principal = original.TotalDebits(primaryCurrency).MinorUnits;
            long alreadyReversed = reversals.Sum(r => r.TotalDebits(primaryCurrency).MinorUnits);
            if (requestedAmount <= 0)
            {
                throw RequestValidationException.Single(nameof(request.AmountMinor), "Reversal amount must be positive.");
            }

            if (alreadyReversed + requestedAmount > principal)
            {
                throw new DomainException("reversal.exceeds_original",
                    $"Reversing {requestedAmount} would exceed the un-reversed remainder " +
                    $"({principal - alreadyReversed}) of entry {original.SequenceNumber}.");
            }

            int seq = 0;
            foreach (var p in original.Postings)
            {
                var amount = new Money(requestedAmount, Currency.FromCode(p.Currency));
                mirrorPostings.Add(Posting.Create(p.AccountId, p.Direction.Opposite(), amount, seq++));
            }
        }
        else
        {
            long alreadyReversed = reversals.Sum(r => r.TotalDebits(primaryCurrency).MinorUnits);
            if (alreadyReversed > 0)
            {
                throw new DomainException("reversal.exceeds_original",
                    $"Entry {original.SequenceNumber} has already been partially reversed; specify an explicit remaining amount.");
            }

            int seq = 0;
            foreach (var p in original.Postings)
            {
                mirrorPostings.Add(Posting.Create(p.AccountId, p.Direction.Opposite(), p.Amount, seq++));
            }
        }

        var accounts = (await uow.Accounts.GetManyAsync(
            mirrorPostings.Select(p => p.AccountId).Distinct(), cancellationToken)).ToDictionary(a => a.Id);

        var entry = JournalEntry.Create(
            EntryType.Reversal,
            _clock.Today,
            _clock.UtcNow,
            request.Reason ?? $"Reversal of entry {original.SequenceNumber}",
            "reversals",
            request.CorrelationId ?? original.CorrelationId,
            mirrorPostings,
            reference: original.Id.ToString(),
            idempotencyKey: request.IdempotencyKey,
            reversalOfEntryId: original.Id);

        foreach (var posting in entry.Postings)
        {
            accounts[posting.AccountId].ApplyPosting(posting.Direction, posting.AmountMinor);
        }

        foreach (var account in accounts.Values)
        {
            account.EnsureWithinOverdraft();
        }

        return entry;
    }
}
