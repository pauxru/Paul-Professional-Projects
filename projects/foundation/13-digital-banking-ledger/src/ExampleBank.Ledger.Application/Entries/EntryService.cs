using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Journal;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.Application.Entries;

public sealed record PostingRequest(Guid AccountId, string Direction, long AmountMinor, string Currency);

public sealed record PostEntryRequest(
    string Type,
    IReadOnlyList<PostingRequest> Postings,
    string Description,
    string? Reference = null,
    string SourceSystem = "manual",
    string? CorrelationId = null,
    DateOnly? ValueDate = null,
    string? IdempotencyKey = null);

/// <summary>Posts, retrieves and lists arbitrary balanced journal entries.</summary>
public sealed class EntryService
{
    private static readonly HashSet<EntryType> DirectlyPostable =
        [EntryType.Transfer, EntryType.Fee, EntryType.Interest, EntryType.Adjustment, EntryType.Settlement];

    private readonly LedgerCommandExecutor _executor;
    private readonly ILedgerUnitOfWorkFactory _uowFactory;
    private readonly IClock _clock;

    public EntryService(LedgerCommandExecutor executor, ILedgerUnitOfWorkFactory uowFactory, IClock clock)
    {
        _executor = executor;
        _uowFactory = uowFactory;
        _clock = clock;
    }

    public Task<EntryResult> PostAsync(PostEntryRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<EntryType>(request.Type, ignoreCase: true, out var entryType) || !DirectlyPostable.Contains(entryType))
        {
            throw RequestValidationException.Single(
                nameof(request.Type),
                $"Entry type '{request.Type}' cannot be posted here. Use the dedicated reversal/FX endpoints for those.");
        }

        if (request.Postings is null || request.Postings.Count < 2)
        {
            throw RequestValidationException.Single(nameof(request.Postings), "An entry needs at least two postings.");
        }

        var accountIds = request.Postings.Select(p => p.AccountId).Distinct().ToArray();
        var command = new EntryCommand(
            Scope: $"entry:{entryType}",
            IdempotencyKey: request.IdempotencyKey,
            AccountsToLock: accountIds,
            Build: (uow, ct) => BuildAsync(uow, request, entryType, ct));

        return _executor.ExecuteAsync(command, cancellationToken);
    }

    private async Task<JournalEntry> BuildAsync(
        ILedgerUnitOfWork uow, PostEntryRequest request, EntryType entryType, CancellationToken cancellationToken)
    {
        var accountIds = request.Postings.Select(p => p.AccountId).Distinct().ToArray();
        var accounts = (await uow.Accounts.GetManyAsync(accountIds, cancellationToken)).ToDictionary(a => a.Id);

        var postings = new List<Posting>(request.Postings.Count);
        int sequence = 0;
        foreach (var pr in request.Postings)
        {
            if (!accounts.TryGetValue(pr.AccountId, out var account))
            {
                throw new NotFoundException($"Account {pr.AccountId} not found.");
            }

            if (!Enum.TryParse<PostingDirection>(pr.Direction, ignoreCase: true, out var direction))
            {
                throw RequestValidationException.Single(nameof(pr.Direction), $"Invalid direction '{pr.Direction}'.");
            }

            if (!Currency.IsKnown(pr.Currency))
            {
                throw RequestValidationException.Single(nameof(pr.Currency), $"Unsupported currency '{pr.Currency}'.");
            }

            if (!string.Equals(account.Currency, pr.Currency, StringComparison.OrdinalIgnoreCase))
            {
                throw new MixedCurrencyException($"Posting currency {pr.Currency} does not match account {account.Code} ({account.Currency}).");
            }

            account.EnsurePostable();
            var amount = new Money(pr.AmountMinor, Currency.FromCode(pr.Currency));
            postings.Add(Posting.Create(pr.AccountId, direction, amount, sequence++));
        }

        var entry = JournalEntry.Create(
            entryType,
            request.ValueDate ?? _clock.Today,
            _clock.UtcNow,
            request.Description,
            request.SourceSystem,
            request.CorrelationId ?? Guid.NewGuid().ToString("n"),
            postings,
            request.Reference,
            request.IdempotencyKey);

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

    public async Task<EntryResult> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var uow = await _uowFactory.CreateAsync(cancellationToken);
        var entry = await uow.Journal.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Journal entry {id} not found.");
        return EntryResult.From(entry);
    }

    public async Task<PagedResult<EntryResult>> ListAsync(PageRequest page, CancellationToken cancellationToken)
    {
        await using var uow = await _uowFactory.CreateAsync(cancellationToken);
        var entries = await uow.Journal.ListAsync(page, cancellationToken);
        var total = await uow.Journal.CountAsync(cancellationToken);
        return new PagedResult<EntryResult>(entries.Select(EntryResult.From).ToList(), page.Page, page.PageSize, total);
    }
}
