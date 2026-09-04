using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Domain.Accounts;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.Application.Accounts;

public sealed record CreateAccountRequest(
    string Code,
    string Name,
    string Type,
    string Currency,
    string? ParentCode = null,
    bool IsControlAccount = false,
    bool IsCustomerAccount = false,
    long OverdraftLimitMinor = 0);

/// <summary>Roll-up view: an account plus the summed balance of its subtree (for control accounts).</summary>
public sealed record AccountBalanceView(AccountDto Account, long RollupBalanceMinor, long OwnBalanceMinor);

public sealed class AccountService
{
    private readonly ILedgerUnitOfWorkFactory _uowFactory;
    private readonly IClock _clock;

    public AccountService(ILedgerUnitOfWorkFactory uowFactory, IClock clock)
    {
        _uowFactory = uowFactory;
        _clock = clock;
    }

    public async Task<AccountDto> CreateAsync(CreateAccountRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<AccountType>(request.Type, ignoreCase: true, out var type))
        {
            throw RequestValidationException.Single(nameof(request.Type), $"Unknown account type '{request.Type}'.");
        }

        if (!Currency.IsKnown(request.Currency))
        {
            throw RequestValidationException.Single(nameof(request.Currency), $"Unsupported currency '{request.Currency}'.");
        }

        if (request.OverdraftLimitMinor < 0)
        {
            throw RequestValidationException.Single(nameof(request.OverdraftLimitMinor), "Overdraft limit cannot be negative.");
        }

        await using var uow = await _uowFactory.CreateAsync(cancellationToken);

        if (await uow.Accounts.GetByCodeAsync(request.Code, cancellationToken) is not null)
        {
            throw new ConflictException($"Account code '{request.Code}' already exists.");
        }

        Guid? parentId = null;
        if (!string.IsNullOrWhiteSpace(request.ParentCode))
        {
            var parent = await uow.Accounts.GetByCodeAsync(request.ParentCode, cancellationToken)
                ?? throw new NotFoundException($"Parent account '{request.ParentCode}' not found.");
            if (!string.Equals(parent.Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
            {
                throw RequestValidationException.Single(nameof(request.Currency), "Child account currency must match its parent.");
            }

            parentId = parent.Id;
        }

        var account = Account.Create(
            Guid.NewGuid(), request.Code, request.Name, type, Currency.FromCode(request.Currency),
            _clock.UtcNow, parentId, request.IsControlAccount, request.IsCustomerAccount, request.OverdraftLimitMinor);

        await uow.Accounts.AddAsync(account, cancellationToken);
        await uow.SaveChangesAsync(cancellationToken);
        return AccountDto.From(account);
    }

    public async Task<AccountDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var uow = await _uowFactory.CreateAsync(cancellationToken);
        var account = await uow.Accounts.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Account {id} not found.");
        return AccountDto.From(account);
    }

    public async Task<IReadOnlyList<AccountDto>> ListAsync(CancellationToken cancellationToken)
    {
        await using var uow = await _uowFactory.CreateAsync(cancellationToken);
        var accounts = await uow.Accounts.ListAsync(cancellationToken);
        return accounts.Select(AccountDto.From).ToList();
    }

    /// <summary>Computes an account's own balance plus the rolled-up balance of its child subtree.</summary>
    public async Task<AccountBalanceView> GetBalanceAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var uow = await _uowFactory.CreateAsync(cancellationToken);
        var account = await uow.Accounts.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Account {id} not found.");

        var all = await uow.Accounts.ListAsync(cancellationToken);
        long rollup = SumSubtree(account.Id, all);
        return new AccountBalanceView(AccountDto.From(account), rollup, account.BalanceMinor);
    }

    private static long SumSubtree(Guid rootId, IReadOnlyList<Account> all)
    {
        var byParent = all.GroupBy(a => a.ParentId).ToDictionary(g => g.Key ?? Guid.Empty, g => g.ToList());
        long total = 0;
        var stack = new Stack<Guid>();
        stack.Push(rootId);
        var self = all.First(a => a.Id == rootId);
        total += self.BalanceMinor;
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!byParent.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                total += child.BalanceMinor;
                stack.Push(child.Id);
            }
        }

        return total;
    }

    public async Task<AccountDto> FreezeAsync(Guid id, CancellationToken cancellationToken)
        => await MutateAsync(id, a => a.Freeze(), cancellationToken);

    public async Task<AccountDto> ActivateAsync(Guid id, CancellationToken cancellationToken)
        => await MutateAsync(id, a => a.Activate(), cancellationToken);

    public async Task<AccountDto> CloseAsync(Guid id, CancellationToken cancellationToken)
        => await MutateAsync(id, a => a.Close(), cancellationToken);

    private async Task<AccountDto> MutateAsync(Guid id, Action<Account> mutate, CancellationToken cancellationToken)
    {
        await using var uow = await _uowFactory.CreateAsync(cancellationToken);
        var account = await uow.Accounts.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Account {id} not found.");
        mutate(account);
        await uow.SaveChangesAsync(cancellationToken);
        return AccountDto.From(account);
    }
}
