using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Journal;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.Application.Fx;

public sealed record FxConvertRequest(
    Guid FromAccountId,
    Guid ToAccountId,
    long AmountMinor,
    DateOnly? AsOf = null,
    string? Reference = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null);

/// <summary>An FX quote: exact rational rate, converted amount and the conserved sub-unit remainder.</summary>
public sealed record FxQuote(
    string FromCurrency,
    string ToCurrency,
    long SourceMinor,
    long TargetMinor,
    long Numerator,
    long Denominator,
    long RemainderNumerator,
    long RemainderDenominator,
    DateOnly AsOf);

/// <summary>
/// Cross-currency conversion between two internal accounts. Posts a single balanced
/// <see cref="EntryType.FxConversion"/> entry (two legs per currency, via FX clearing accounts).
/// Conversion uses exact integer arithmetic; the sub-minor-unit remainder is conserved and reported.
/// </summary>
public sealed class FxService
{
    private readonly LedgerCommandExecutor _executor;
    private readonly ILedgerUnitOfWorkFactory _uowFactory;
    private readonly IFxRateProvider _rates;
    private readonly IClock _clock;

    public FxService(
        LedgerCommandExecutor executor,
        ILedgerUnitOfWorkFactory uowFactory,
        IFxRateProvider rates,
        IClock clock)
    {
        _executor = executor;
        _uowFactory = uowFactory;
        _rates = rates;
        _clock = clock;
    }

    /// <summary>Computes an FX quote without posting anything.</summary>
    public static FxQuote Quote(Currency from, Currency to, long sourceMinor, FxRate rate)
    {
        if (sourceMinor <= 0)
        {
            throw new DomainException("fx.non_positive", "FX amount must be positive.");
        }

        // target_minor = source_minor * num * scaleTo / (den * scaleFrom), tracked with exact remainder.
        long numerator = checked(sourceMinor * rate.Numerator * to.MinorUnitsPerMajor);
        long denominator = checked(rate.Denominator * from.MinorUnitsPerMajor);
        long targetMinor = numerator / denominator;
        long remainder = numerator - (targetMinor * denominator);

        return new FxQuote(
            from.Code, to.Code, sourceMinor, targetMinor,
            rate.Numerator, rate.Denominator, remainder, denominator, rate.AsOf);
    }

    public async Task<FxQuote> PreviewAsync(FxConvertRequest request, CancellationToken cancellationToken)
    {
        var (from, to) = await LoadCurrenciesAsync(request, cancellationToken);
        var rate = _rates.GetRate(from, to, request.AsOf ?? _clock.Today);
        return Quote(from, to, request.AmountMinor, rate);
    }

    public async Task<EntryResult> ConvertAsync(FxConvertRequest request, CancellationToken cancellationToken)
    {
        var (from, to) = await LoadCurrenciesAsync(request, cancellationToken);

        Guid clearingFromId, clearingToId;
        await using (var read = await _uowFactory.CreateAsync(cancellationToken))
        {
            clearingFromId = (await read.Accounts.GetByCodeAsync(WellKnownAccounts.FxClearing(from.Code), cancellationToken)
                ?? throw new NotFoundException($"FX clearing account for {from.Code} is not configured.")).Id;
            clearingToId = (await read.Accounts.GetByCodeAsync(WellKnownAccounts.FxClearing(to.Code), cancellationToken)
                ?? throw new NotFoundException($"FX clearing account for {to.Code} is not configured.")).Id;
        }

        var command = new EntryCommand(
            "fx",
            request.IdempotencyKey,
            new[] { request.FromAccountId, request.ToAccountId, clearingFromId, clearingToId },
            (uow, ct) => BuildAsync(uow, request, from, to, ct));

        return await _executor.ExecuteAsync(command, cancellationToken);
    }

    private async Task<(Currency From, Currency To)> LoadCurrenciesAsync(FxConvertRequest request, CancellationToken cancellationToken)
    {
        await using var read = await _uowFactory.CreateAsync(cancellationToken);
        var from = await read.Accounts.GetByIdAsync(request.FromAccountId, cancellationToken)
            ?? throw new NotFoundException($"Account {request.FromAccountId} not found.");
        var to = await read.Accounts.GetByIdAsync(request.ToAccountId, cancellationToken)
            ?? throw new NotFoundException($"Account {request.ToAccountId} not found.");

        if (from.Currency == to.Currency)
        {
            throw new DomainException("fx.same_currency", "FX conversion requires two different currencies.");
        }

        return (Currency.FromCode(from.Currency), Currency.FromCode(to.Currency));
    }

    private async Task<JournalEntry> BuildAsync(
        ILedgerUnitOfWork uow, FxConvertRequest request, Currency from, Currency to, CancellationToken cancellationToken)
    {
        var fromAccount = await uow.Accounts.GetByIdAsync(request.FromAccountId, cancellationToken)
            ?? throw new NotFoundException($"Account {request.FromAccountId} not found.");
        var toAccount = await uow.Accounts.GetByIdAsync(request.ToAccountId, cancellationToken)
            ?? throw new NotFoundException($"Account {request.ToAccountId} not found.");
        var clearingFrom = await uow.Accounts.GetByCodeAsync(WellKnownAccounts.FxClearing(from.Code), cancellationToken)
            ?? throw new NotFoundException($"FX clearing account for {from.Code} is not configured.");
        var clearingTo = await uow.Accounts.GetByCodeAsync(WellKnownAccounts.FxClearing(to.Code), cancellationToken)
            ?? throw new NotFoundException($"FX clearing account for {to.Code} is not configured.");

        fromAccount.EnsurePostable();
        toAccount.EnsurePostable();
        clearingFrom.EnsurePostable();
        clearingTo.EnsurePostable();

        var rate = _rates.GetRate(from, to, request.AsOf ?? _clock.Today);
        var quote = Quote(from, to, request.AmountMinor, rate);
        if (quote.TargetMinor <= 0)
        {
            throw new DomainException("fx.rounds_to_zero", "Converted amount rounds to zero.");
        }

        var sourceMoney = new Money(quote.SourceMinor, from);
        var targetMoney = new Money(quote.TargetMinor, to);

        fromAccount.EnsureCanWithdraw(sourceMoney.MinorUnits);

        // Source currency leg: debit customer (give up funds), credit FX clearing.
        var legFrom = ValueMovement.DebitCredit(fromAccount, clearingFrom, sourceMoney, startSequence: 0);
        // Target currency leg: debit FX clearing, credit customer (receive funds).
        var legTo = ValueMovement.DebitCredit(clearingTo, toAccount, targetMoney, startSequence: 2);
        fromAccount.EnsureWithinOverdraft();

        var postings = legFrom.Concat(legTo).ToList();

        // Encode the exact rate and conserved remainder into the reference for audit.
        var reference = request.Reference ??
            $"fx:{from.Code}>{to.Code}:num={rate.Numerator}:den={rate.Denominator}:rem={quote.RemainderNumerator}/{quote.RemainderDenominator}";

        return JournalEntry.Create(
            EntryType.FxConversion,
            request.AsOf ?? _clock.Today,
            _clock.UtcNow,
            $"FX {quote.SourceMinor} {from.Code} -> {quote.TargetMinor} {to.Code} @ {rate.Numerator}/{rate.Denominator}",
            "fx",
            request.CorrelationId ?? Guid.NewGuid().ToString("n"),
            postings,
            reference,
            request.IdempotencyKey);
    }
}
