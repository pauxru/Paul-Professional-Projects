using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Holds;
using ExampleBank.Ledger.Domain.Journal;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.Application.Holds;

public sealed record PlaceHoldRequest(
    Guid AccountId,
    long AmountMinor,
    string Currency,
    int ExpiresInMinutes = 1440,
    string? Reference = null,
    string? IdempotencyKey = null);

public sealed record CaptureHoldRequest(
    Guid HoldId,
    Guid DestinationAccountId,
    long? CaptureMinor = null,
    string? Reference = null,
    string? IdempotencyKey = null);

public sealed record HoldResult(
    Guid Id,
    Guid AccountId,
    long AmountMinor,
    long CapturedMinor,
    long RemainingMinor,
    string Currency,
    string Status,
    string? Reference,
    DateTimeOffset PlacedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ResolvedAt)
{
    public static HoldResult From(Hold h) => new(
        h.Id, h.AccountId, h.AmountMinor, h.CapturedMinor, h.RemainingMinor, h.Currency,
        h.Status.ToString(), h.Reference, h.PlacedAt, h.ExpiresAt, h.ResolvedAt);
}

public sealed record CaptureResult(EntryResult Entry, HoldResult Hold);

/// <summary>
/// Manages authorization holds: placing (reduces available), capturing (posts and releases the
/// remainder), releasing, and — via the background sweeper — expiring. Available balance can never
/// be driven below the overdraft floor by a hold.
/// </summary>
public sealed class HoldService
{
    private readonly LedgerCommandExecutor _executor;
    private readonly ILedgerUnitOfWorkFactory _uowFactory;
    private readonly IClock _clock;
    private readonly ILedgerMetrics _metrics;

    public HoldService(
        LedgerCommandExecutor executor,
        ILedgerUnitOfWorkFactory uowFactory,
        IClock clock,
        ILedgerMetrics metrics)
    {
        _executor = executor;
        _uowFactory = uowFactory;
        _clock = clock;
        _metrics = metrics;
    }

    public Task<HoldResult> PlaceAsync(PlaceHoldRequest request, CancellationToken cancellationToken)
    {
        if (request.AmountMinor <= 0)
        {
            throw RequestValidationException.Single(nameof(request.AmountMinor), "Hold amount must be positive.");
        }

        if (!Currency.IsKnown(request.Currency))
        {
            throw RequestValidationException.Single(nameof(request.Currency), $"Unsupported currency '{request.Currency}'.");
        }

        return _executor.ExecuteCommandAsync<HoldResult>(
            "hold:place",
            request.IdempotencyKey,
            new[] { request.AccountId },
            async (uow, ct) =>
            {
                var account = await uow.Accounts.GetByIdAsync(request.AccountId, ct)
                    ?? throw new NotFoundException($"Account {request.AccountId} not found.");
                account.EnsurePostable();

                if (!account.IsCustomerAccount)
                {
                    throw RequestValidationException.Single(nameof(request.AccountId), "Holds may only be placed on customer accounts.");
                }

                if (!string.Equals(account.Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
                {
                    throw new MixedCurrencyException($"Hold currency {request.Currency} must match account {account.Code} ({account.Currency}).");
                }

                var amount = new Money(request.AmountMinor, Currency.FromCode(request.Currency));
                account.PlaceHold(amount.MinorUnits); // enforces available-balance / overdraft

                var hold = Hold.Create(
                    account.Id, amount, _clock.UtcNow,
                    _clock.UtcNow.AddMinutes(request.ExpiresInMinutes), request.Reference, request.IdempotencyKey);
                await uow.Holds.AddAsync(hold, ct);
                return (HoldResult.From(hold), hold.Id);
            },
            cancellationToken);
    }

    public async Task<CaptureResult> CaptureAsync(CaptureHoldRequest request, CancellationToken cancellationToken)
    {
        Guid accountId, destinationId;
        await using (var read = await _uowFactory.CreateAsync(cancellationToken))
        {
            var hold = await read.Holds.GetByIdAsync(request.HoldId, cancellationToken)
                ?? throw new NotFoundException($"Hold {request.HoldId} not found.");
            accountId = hold.AccountId;
            destinationId = request.DestinationAccountId;
        }

        Guid capturedHoldId = request.HoldId;
        var entry = await _executor.ExecuteAsync(
            new EntryCommand(
                "hold:capture",
                request.IdempotencyKey,
                new[] { accountId, destinationId },
                async (uow, ct) =>
                {
                    var hold = await uow.Holds.GetByIdAsync(request.HoldId, ct)
                        ?? throw new NotFoundException($"Hold {request.HoldId} not found.");
                    var account = await uow.Accounts.GetByIdAsync(hold.AccountId, ct)
                        ?? throw new NotFoundException($"Account {hold.AccountId} not found.");
                    var destination = await uow.Accounts.GetByIdAsync(destinationId, ct)
                        ?? throw new NotFoundException($"Destination account {destinationId} not found.");

                    account.EnsurePostable();
                    destination.EnsurePostable();
                    if (account.Currency != destination.Currency)
                    {
                        throw new MixedCurrencyException("Hold capture destination must share the account currency.");
                    }

                    long capture = request.CaptureMinor ?? hold.AmountMinor;
                    long released = hold.Capture(capture, _clock.UtcNow); // returns full original amount
                    account.ReleaseHold(released);

                    var amount = new Money(capture, account.CurrencyRef);
                    var postings = ValueMovement.Transfer(account, destination, amount);
                    account.EnsureWithinOverdraft();

                    return JournalEntry.Create(
                        EntryType.Settlement,
                        _clock.Today,
                        _clock.UtcNow,
                        $"Capture of hold {hold.Id}",
                        "holds",
                        Guid.NewGuid().ToString("n"),
                        postings,
                        request.Reference,
                        request.IdempotencyKey);
                }),
            cancellationToken);

        await using var readback = await _uowFactory.CreateAsync(cancellationToken);
        var updated = await readback.Holds.GetByIdAsync(capturedHoldId, cancellationToken);
        return new CaptureResult(entry, HoldResult.From(updated!));
    }

    public async Task<HoldResult> ReleaseAsync(Guid holdId, string? idempotencyKey, CancellationToken cancellationToken)
    {
        Guid accountId;
        await using (var read = await _uowFactory.CreateAsync(cancellationToken))
        {
            var existing = await read.Holds.GetByIdAsync(holdId, cancellationToken)
                ?? throw new NotFoundException($"Hold {holdId} not found.");
            accountId = existing.AccountId;
        }

        return await _executor.ExecuteCommandAsync<HoldResult>(
            "hold:release",
            idempotencyKey,
            new[] { accountId },
            async (uow, ct) =>
            {
                var hold = await uow.Holds.GetByIdAsync(holdId, ct)
                    ?? throw new NotFoundException($"Hold {holdId} not found.");
                var account = await uow.Accounts.GetByIdAsync(hold.AccountId, ct)
                    ?? throw new NotFoundException($"Account {hold.AccountId} not found.");

                hold.Release(_clock.UtcNow);
                account.ReleaseHold(hold.AmountMinor);
                return (HoldResult.From(hold), hold.Id);
            },
            cancellationToken);
    }

    public async Task<HoldResult> GetAsync(Guid holdId, CancellationToken cancellationToken)
    {
        await using var uow = await _uowFactory.CreateAsync(cancellationToken);
        var hold = await uow.Holds.GetByIdAsync(holdId, cancellationToken)
            ?? throw new NotFoundException($"Hold {holdId} not found.");
        return HoldResult.From(hold);
    }

    /// <summary>Expires all active holds past their expiry as of <paramref name="asOf"/>; returns the count.</summary>
    public async Task<int> ExpireDueHoldsAsync(DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        await using var read = await _uowFactory.CreateAsync(cancellationToken);
        var due = await read.Holds.GetExpiredActiveAsync(asOf, cancellationToken);
        int expired = 0;

        foreach (var dueHold in due)
        {
            try
            {
                await _executor.ExecuteCommandAsync<bool>(
                    "hold:expire",
                    idempotencyKey: null,
                    new[] { dueHold.AccountId },
                    async (uow, ct) =>
                    {
                        var hold = await uow.Holds.GetByIdAsync(dueHold.Id, ct);
                        if (hold is null || !hold.IsActive)
                        {
                            return (false, dueHold.Id);
                        }

                        var account = await uow.Accounts.GetByIdAsync(hold.AccountId, ct);
                        hold.Expire(asOf);
                        account?.ReleaseHold(hold.AmountMinor);
                        return (true, hold.Id);
                    },
                    cancellationToken);
                _metrics.HoldExpired();
                expired++;
            }
            catch (DomainException)
            {
                // Hold was concurrently resolved; skip.
            }
        }

        return expired;
    }
}
