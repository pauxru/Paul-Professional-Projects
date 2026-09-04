using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Domain.Accounts;
using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Journal;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.Application.Transfers;

public sealed record TransferRequest(
    Guid FromAccountId,
    Guid ToAccountId,
    long AmountMinor,
    string Currency,
    string Description,
    string? Reference = null,
    string SourceSystem = "transfers",
    string? CorrelationId = null,
    DateOnly? ValueDate = null,
    string? IdempotencyKey = null);

/// <summary>
/// Moves cleared funds between two internal accounts as a single balanced journal entry, after
/// checking the source account's <em>available</em> balance against its overdraft limit.
/// </summary>
public sealed class TransferService
{
    private readonly LedgerCommandExecutor _executor;
    private readonly IClock _clock;

    public TransferService(LedgerCommandExecutor executor, IClock clock)
    {
        _executor = executor;
        _clock = clock;
    }

    public Task<EntryResult> TransferAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        if (request.AmountMinor <= 0)
        {
            throw RequestValidationException.Single(nameof(request.AmountMinor), "Transfer amount must be positive.");
        }

        if (request.FromAccountId == request.ToAccountId)
        {
            throw RequestValidationException.Single(nameof(request.ToAccountId), "Cannot transfer to the same account.");
        }

        if (!Currency.IsKnown(request.Currency))
        {
            throw RequestValidationException.Single(nameof(request.Currency), $"Unsupported currency '{request.Currency}'.");
        }

        var currency = Currency.FromCode(request.Currency);
        var command = new EntryCommand(
            Scope: "transfer",
            IdempotencyKey: request.IdempotencyKey,
            AccountsToLock: new[] { request.FromAccountId, request.ToAccountId },
            Build: (uow, ct) => BuildAsync(uow, request, currency, ct));

        return _executor.ExecuteAsync(command, cancellationToken);
    }

    private async Task<JournalEntry> BuildAsync(
        ILedgerUnitOfWork uow, TransferRequest request, Currency currency, CancellationToken cancellationToken)
    {
        var from = await uow.Accounts.GetByIdAsync(request.FromAccountId, cancellationToken)
            ?? throw new NotFoundException($"Account {request.FromAccountId} not found.");
        var to = await uow.Accounts.GetByIdAsync(request.ToAccountId, cancellationToken)
            ?? throw new NotFoundException($"Account {request.ToAccountId} not found.");

        from.EnsurePostable();
        to.EnsurePostable();

        if (from.Currency != currency.Code || to.Currency != currency.Code)
        {
            throw new MixedCurrencyException(
                $"Transfer currency {currency.Code} must match both accounts ({from.Currency}, {to.Currency}).");
        }

        var amount = new Money(request.AmountMinor, currency);
        from.EnsureCanWithdraw(amount.MinorUnits);

        // Value leaves the source (decrease) and arrives at the destination (increase),
        // with debit/credit directions derived from each account's normal side.
        var postings = ValueMovement.Transfer(from, to, amount);
        from.EnsureWithinOverdraft();

        return JournalEntry.Create(
            EntryType.Transfer,
            request.ValueDate ?? _clock.Today,
            _clock.UtcNow,
            request.Description,
            request.SourceSystem,
            request.CorrelationId ?? Guid.NewGuid().ToString("n"),
            postings,
            request.Reference,
            request.IdempotencyKey);
    }
}
