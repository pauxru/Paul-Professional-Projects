using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Journal;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.Application.Fees;

public sealed record ChargeFeeRequest(
    Guid AccountId,
    string FeeScheduleCode,
    long BaseAmountMinor,
    string? Reference = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null);

/// <summary>Applies a fee schedule to a base amount and posts the fee from a customer to fee income.</summary>
public sealed class FeeService
{
    private readonly LedgerCommandExecutor _executor;
    private readonly ILedgerUnitOfWorkFactory _uowFactory;
    private readonly IClock _clock;

    public FeeService(LedgerCommandExecutor executor, ILedgerUnitOfWorkFactory uowFactory, IClock clock)
    {
        _executor = executor;
        _uowFactory = uowFactory;
        _clock = clock;
    }

    public async Task<long> PreviewAsync(string feeScheduleCode, long baseAmountMinor, CancellationToken cancellationToken)
    {
        await using var uow = await _uowFactory.CreateAsync(cancellationToken);
        var schedule = await uow.FeeSchedules.GetByCodeAsync(feeScheduleCode, cancellationToken)
            ?? throw new NotFoundException($"Fee schedule '{feeScheduleCode}' not found.");
        return schedule.Calculate(baseAmountMinor);
    }

    public async Task<EntryResult> ChargeAsync(ChargeFeeRequest request, CancellationToken cancellationToken)
    {
        Guid incomeAccountId;
        await using (var read = await _uowFactory.CreateAsync(cancellationToken))
        {
            var schedule = await read.FeeSchedules.GetByCodeAsync(request.FeeScheduleCode, cancellationToken)
                ?? throw new NotFoundException($"Fee schedule '{request.FeeScheduleCode}' not found.");
            incomeAccountId = schedule.IncomeAccountId;
        }

        var command = new EntryCommand(
            "fee",
            request.IdempotencyKey,
            new[] { request.AccountId, incomeAccountId },
            (uow, ct) => BuildAsync(uow, request, ct));

        return await _executor.ExecuteAsync(command, cancellationToken);
    }

    private async Task<JournalEntry> BuildAsync(ILedgerUnitOfWork uow, ChargeFeeRequest request, CancellationToken cancellationToken)
    {
        var schedule = await uow.FeeSchedules.GetByCodeAsync(request.FeeScheduleCode, cancellationToken)
            ?? throw new NotFoundException($"Fee schedule '{request.FeeScheduleCode}' not found.");
        var account = await uow.Accounts.GetByIdAsync(request.AccountId, cancellationToken)
            ?? throw new NotFoundException($"Account {request.AccountId} not found.");
        var income = await uow.Accounts.GetByIdAsync(schedule.IncomeAccountId, cancellationToken)
            ?? throw new NotFoundException($"Fee income account {schedule.IncomeAccountId} not found.");

        account.EnsurePostable();
        income.EnsurePostable();

        if (account.Currency != schedule.Currency || income.Currency != schedule.Currency)
        {
            throw new MixedCurrencyException("Fee schedule, customer account and income account currencies must match.");
        }

        long feeMinor = schedule.Calculate(request.BaseAmountMinor);
        if (feeMinor <= 0)
        {
            throw new DomainException("fee.zero", "Calculated fee is zero; nothing to post.");
        }

        var fee = new Money(feeMinor, Currency.FromCode(schedule.Currency));
        account.EnsureCanWithdraw(fee.MinorUnits);

        var postings = ValueMovement.DecreaseIncrease(account, income, fee);
        account.EnsureWithinOverdraft();

        return JournalEntry.Create(
            EntryType.Fee,
            _clock.Today,
            _clock.UtcNow,
            $"Fee '{schedule.Code}' on base {request.BaseAmountMinor} {schedule.Currency}",
            "fees",
            request.CorrelationId ?? Guid.NewGuid().ToString("n"),
            postings,
            request.Reference ?? schedule.Code,
            request.IdempotencyKey);
    }
}
