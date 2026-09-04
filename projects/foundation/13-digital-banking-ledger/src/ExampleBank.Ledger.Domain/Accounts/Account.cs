using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Journal;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.Domain.Accounts;

/// <summary>
/// A chart-of-accounts node. Balances are cached as running debit/credit totals for O(1) reads,
/// but are always reconcilable against the sum of postings (see the integrity self-check).
/// The account is the unit of concurrency control: a <see cref="Version"/> optimistic token guards
/// against lost updates, and callers acquire a per-account lock before mutating it.
/// </summary>
public sealed class Account
{
    private Account() { } // EF

    public Guid Id { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public AccountType Type { get; private set; }
    public NormalBalance NormalBalance { get; private set; }
    public string Currency { get; private set; } = null!;
    public AccountStatus Status { get; private set; }
    public Guid? ParentId { get; private set; }

    /// <summary>A control account is a roll-up parent whose balance is the sum of its children.</summary>
    public bool IsControlAccount { get; private set; }

    /// <summary>Customer money accounts get available-balance (funds) enforcement; internal GLs do not.</summary>
    public bool IsCustomerAccount { get; private set; }

    /// <summary>How far the available balance may go negative, in minor units (>= 0).</summary>
    public long OverdraftLimitMinor { get; private set; }

    public long TotalDebitsMinor { get; private set; }
    public long TotalCreditsMinor { get; private set; }

    /// <summary>Sum of currently-active authorization holds, in minor units.</summary>
    public long HeldMinor { get; private set; }

    /// <summary>Optimistic-concurrency token; incremented on every mutation.</summary>
    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Currency CurrencyRef => Monetary.Currency.FromCode(Currency);

    /// <summary>Signed balance in the account's normal-side orientation (positive = "has funds").</summary>
    public long BalanceMinor => NormalBalance == NormalBalance.Debit
        ? TotalDebitsMinor - TotalCreditsMinor
        : TotalCreditsMinor - TotalDebitsMinor;

    /// <summary>The posting side that increases this account (its normal side).</summary>
    public PostingDirection IncreaseDirection =>
        NormalBalance == NormalBalance.Debit ? PostingDirection.Debit : PostingDirection.Credit;

    /// <summary>The posting side that decreases this account (opposite its normal side).</summary>
    public PostingDirection DecreaseDirection =>
        NormalBalance == NormalBalance.Debit ? PostingDirection.Credit : PostingDirection.Debit;

    /// <summary>Debit-positive signed balance (debits − credits) used for trial-balance summation.</summary>
    public long DebitSignedBalanceMinor => TotalDebitsMinor - TotalCreditsMinor;

    /// <summary>Cleared balance less active holds. Only meaningful for customer money accounts.</summary>
    public long AvailableMinor => BalanceMinor - HeldMinor;

    public Money Balance => new(BalanceMinor, CurrencyRef);
    public Money Available => new(AvailableMinor, CurrencyRef);

    public static Account Create(
        Guid id,
        string code,
        string name,
        AccountType type,
        Currency currency,
        DateTimeOffset createdAt,
        Guid? parentId = null,
        bool isControlAccount = false,
        bool isCustomerAccount = false,
        long overdraftLimitMinor = 0)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new DomainException("account.invalid_code", "Account code is required.");
        }

        if (overdraftLimitMinor < 0)
        {
            throw new DomainException("account.invalid_overdraft", "Overdraft limit cannot be negative.");
        }

        return new Account
        {
            Id = id,
            Code = code.Trim(),
            Name = name?.Trim() ?? code.Trim(),
            Type = type,
            NormalBalance = type.NormalBalance(),
            Currency = currency.Code,
            Status = AccountStatus.Active,
            ParentId = parentId,
            IsControlAccount = isControlAccount,
            IsCustomerAccount = isCustomerAccount,
            OverdraftLimitMinor = overdraftLimitMinor,
            CreatedAt = createdAt,
            Version = 0,
        };
    }

    public void EnsurePostable()
    {
        if (Status != AccountStatus.Active)
        {
            throw new DomainException(
                "account.not_active",
                $"Account {Code} is {Status} and cannot be posted to.");
        }

        if (IsControlAccount)
        {
            throw new DomainException(
                "account.control_account",
                $"Account {Code} is a control account; post to a leaf account instead.");
        }
    }

    /// <summary>Applies a single posting leg to the cached totals. Amount must be positive minor units.</summary>
    public void ApplyPosting(PostingDirection direction, long amountMinor)
    {
        if (amountMinor <= 0)
        {
            throw new DomainException("posting.non_positive", "Posting amount must be positive.");
        }

        if (direction == PostingDirection.Debit)
        {
            TotalDebitsMinor = checked(TotalDebitsMinor + amountMinor);
        }
        else
        {
            TotalCreditsMinor = checked(TotalCreditsMinor + amountMinor);
        }

        Version++;
    }

    /// <summary>Throws if withdrawing <paramref name="amountMinor"/> now would breach the overdraft floor.</summary>
    public void EnsureCanWithdraw(long amountMinor)
    {
        if (!IsCustomerAccount)
        {
            return; // internal GL accounts are not funds-constrained
        }

        var projectedAvailable = AvailableMinor - amountMinor;
        if (projectedAvailable < -OverdraftLimitMinor)
        {
            throw new InsufficientFundsException(
                $"Account {Code} available {AvailableMinor} minor units cannot fund {amountMinor} " +
                $"within overdraft limit {OverdraftLimitMinor}.");
        }
    }

    /// <summary>Post-condition guard: a customer account may never sit below its overdraft floor.</summary>
    public void EnsureWithinOverdraft()
    {
        if (IsCustomerAccount && AvailableMinor < -OverdraftLimitMinor)
        {
            throw new InsufficientFundsException(
                $"Account {Code} available {AvailableMinor} minor units breaches overdraft limit {OverdraftLimitMinor}.");
        }
    }

    public void PlaceHold(long amountMinor)
    {
        if (amountMinor <= 0)
        {
            throw new DomainException("hold.non_positive", "Hold amount must be positive.");
        }

        EnsureCanWithdraw(amountMinor);
        HeldMinor = checked(HeldMinor + amountMinor);
        Version++;
    }

    public void ReleaseHold(long amountMinor)
    {
        if (amountMinor <= 0)
        {
            return;
        }

        HeldMinor = Math.Max(0, HeldMinor - amountMinor);
        Version++;
    }

    public void Freeze()
    {
        if (Status == AccountStatus.Closed)
        {
            throw new DomainException("account.closed", $"Account {Code} is closed.");
        }

        Status = AccountStatus.Frozen;
        Version++;
    }

    public void Activate()
    {
        if (Status == AccountStatus.Closed)
        {
            throw new DomainException("account.closed", $"Account {Code} is closed and cannot be reactivated.");
        }

        Status = AccountStatus.Active;
        Version++;
    }

    /// <summary>Closing requires a zero balance and no active holds — money cannot be orphaned.</summary>
    public void Close()
    {
        if (BalanceMinor != 0 || HeldMinor != 0)
        {
            throw new DomainException(
                "account.non_zero_balance",
                $"Account {Code} must have a zero balance and no holds before closing " +
                $"(balance {BalanceMinor}, held {HeldMinor}).");
        }

        Status = AccountStatus.Closed;
        Version++;
    }
}
