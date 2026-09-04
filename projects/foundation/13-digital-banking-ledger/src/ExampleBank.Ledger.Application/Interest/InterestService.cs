using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Journal;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.Application.Interest;

public sealed record CapitalizeInterestRequest(
    Guid AccountId,
    string? Reference = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null);

/// <summary>
/// Daily interest accrual and periodic capitalisation. Accrual only updates the running accrual
/// record; capitalisation posts a balanced ledger entry (debit interest expense, credit customer).
/// </summary>
public sealed class InterestService
{
    private readonly LedgerCommandExecutor _executor;
    private readonly ILedgerUnitOfWorkFactory _uowFactory;
    private readonly IClock _clock;

    public InterestService(LedgerCommandExecutor executor, ILedgerUnitOfWorkFactory uowFactory, IClock clock)
    {
        _executor = executor;
        _uowFactory = uowFactory;
        _clock = clock;
    }

    /// <summary>Accrues interest for a single account up to <paramref name="asOf"/>. Returns the amount accrued.</summary>
    public Task<long> AccrueAsync(Guid accountId, DateOnly asOf, CancellationToken cancellationToken)
    {
        return _executor.ExecuteCommandAsync<long>(
            "interest-accrual",
            idempotencyKey: null,
            new[] { accountId },
            async (uow, ct) =>
            {
                var accrual = await uow.InterestAccruals.GetByAccountAsync(accountId, ct)
                    ?? throw new NotFoundException($"No interest accrual configured for account {accountId}.");
                var account = await uow.Accounts.GetByIdAsync(accountId, ct)
                    ?? throw new NotFoundException($"Account {accountId} not found.");

                long accrued = accrual.AccrueTo(account.BalanceMinor, asOf);
                return (accrued, accrual.Id);
            },
            cancellationToken);
    }

    /// <summary>Accrues interest for every configured account up to <paramref name="asOf"/>.</summary>
    public async Task<long> AccrueAllAsync(DateOnly asOf, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> accountIds;
        await using (var uow = await _uowFactory.CreateAsync(cancellationToken))
        {
            var accruals = await uow.InterestAccruals.ListAsync(cancellationToken);
            accountIds = accruals.Select(a => a.AccountId).ToList();
        }

        long total = 0;
        foreach (var accountId in accountIds)
        {
            total += await AccrueAsync(accountId, asOf, cancellationToken);
        }

        return total;
    }

    /// <summary>Capitalises accrued interest for an account, posting it to the ledger and resetting the accrual.</summary>
    public async Task<EntryResult> CapitalizeAsync(CapitalizeInterestRequest request, CancellationToken cancellationToken)
    {
        Guid counterAccountId;
        await using (var read = await _uowFactory.CreateAsync(cancellationToken))
        {
            var accrual = await read.InterestAccruals.GetByAccountAsync(request.AccountId, cancellationToken)
                ?? throw new NotFoundException($"No interest accrual configured for account {request.AccountId}.");
            counterAccountId = accrual.CounterAccountId;
        }

        var command = new EntryCommand(
            "interest-capitalize",
            request.IdempotencyKey,
            new[] { request.AccountId, counterAccountId },
            (uow, ct) => BuildAsync(uow, request, ct));

        return await _executor.ExecuteAsync(command, cancellationToken);
    }

    private async Task<JournalEntry> BuildAsync(ILedgerUnitOfWork uow, CapitalizeInterestRequest request, CancellationToken cancellationToken)
    {
        var accrual = await uow.InterestAccruals.GetByAccountAsync(request.AccountId, cancellationToken)
            ?? throw new NotFoundException($"No interest accrual configured for account {request.AccountId}.");
        var account = await uow.Accounts.GetByIdAsync(request.AccountId, cancellationToken)
            ?? throw new NotFoundException($"Account {request.AccountId} not found.");
        var counter = await uow.Accounts.GetByIdAsync(accrual.CounterAccountId, cancellationToken)
            ?? throw new NotFoundException($"Interest counter account {accrual.CounterAccountId} not found.");

        account.EnsurePostable();
        counter.EnsurePostable();

        if (account.Currency != accrual.Currency || counter.Currency != accrual.Currency)
        {
            throw new MixedCurrencyException("Interest account and counter account currencies must match the accrual.");
        }

        long amountMinor = accrual.Capitalize();
        if (amountMinor <= 0)
        {
            throw new DomainException("interest.nothing_to_capitalize", "No accrued interest to capitalise.");
        }

        var amount = new Money(amountMinor, Currency.FromCode(accrual.Currency));

        // Bank pays interest: debit interest expense (counter), credit customer deposit (account).
        var postings = ValueMovement.DebitCredit(counter, account, amount);

        return JournalEntry.Create(
            EntryType.Interest,
            _clock.Today,
            _clock.UtcNow,
            $"Interest capitalisation for account {account.Code}",
            "interest",
            request.CorrelationId ?? Guid.NewGuid().ToString("n"),
            postings,
            request.Reference,
            request.IdempotencyKey);
    }
}
