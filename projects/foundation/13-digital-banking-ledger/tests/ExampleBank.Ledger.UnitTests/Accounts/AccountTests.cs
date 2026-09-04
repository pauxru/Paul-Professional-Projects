using ExampleBank.Ledger.Domain.Accounts;
using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Journal;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.UnitTests.Accounts;

public sealed class AccountTests
{
    private static Account Customer(long overdraftLimitMinor = 0) => Account.Create(
        Guid.NewGuid(), "CUST-1", "Customer deposit", AccountType.Liability, Currency.KES,
        DateTimeOffset.UtcNow, isCustomerAccount: true, overdraftLimitMinor: overdraftLimitMinor);

    private static Account Internal(AccountType type = AccountType.Asset) => Account.Create(
        Guid.NewGuid(), "GL-1", "Ledger account", type, Currency.KES, DateTimeOffset.UtcNow);

    private static void Fund(Account customer, long minor) => customer.ApplyPosting(PostingDirection.Credit, minor);

    [Theory]
    [InlineData(AccountType.Asset, NormalBalance.Debit)]
    [InlineData(AccountType.Expense, NormalBalance.Debit)]
    [InlineData(AccountType.Liability, NormalBalance.Credit)]
    [InlineData(AccountType.Equity, NormalBalance.Credit)]
    [InlineData(AccountType.Income, NormalBalance.Credit)]
    public void Create_AssignsNormalBalance_ByAccountType(AccountType type, NormalBalance expected)
    {
        var account = Account.Create(Guid.NewGuid(), "A", "A", type, Currency.USD, DateTimeOffset.UtcNow);

        Assert.Equal(expected, account.NormalBalance);
    }

    [Fact]
    public void IncreaseAndDecreaseDirection_FollowNormalSide()
    {
        var liability = Customer();
        Assert.Equal(PostingDirection.Credit, liability.IncreaseDirection);
        Assert.Equal(PostingDirection.Debit, liability.DecreaseDirection);

        var asset = Internal(AccountType.Asset);
        Assert.Equal(PostingDirection.Debit, asset.IncreaseDirection);
        Assert.Equal(PostingDirection.Credit, asset.DecreaseDirection);
    }

    [Fact]
    public void ApplyPosting_OnCreditNormalAccount_IncreasesBalanceOnCredit()
    {
        var account = Customer();
        Fund(account, 10_000);

        Assert.Equal(10_000, account.BalanceMinor);
        Assert.Equal(10_000, account.AvailableMinor);
    }

    [Fact]
    public void ApplyPosting_NonPositiveAmount_Throws()
    {
        var account = Customer();
        Assert.Throws<DomainException>(() => account.ApplyPosting(PostingDirection.Credit, 0));
    }

    [Fact]
    public void EnsureCanWithdraw_WithinClearedFunds_DoesNotThrow()
    {
        var account = Customer();
        Fund(account, 10_000);

        account.EnsureCanWithdraw(4_000);
    }

    [Fact]
    public void EnsureCanWithdraw_ExactlyAtAvailable_DoesNotThrow()
    {
        var account = Customer();
        Fund(account, 10_000);

        account.EnsureCanWithdraw(10_000);
    }

    [Fact]
    public void EnsureCanWithdraw_AtOverdraftBoundary_DoesNotThrow()
    {
        var account = Customer(overdraftLimitMinor: 5_000);
        Fund(account, 10_000);

        // available 10_000 + overdraft 5_000 = 15_000 is the exact floor.
        account.EnsureCanWithdraw(15_000);
    }

    [Fact]
    public void EnsureCanWithdraw_OnePastOverdraftBoundary_ThrowsInsufficientFunds()
    {
        var account = Customer(overdraftLimitMinor: 5_000);
        Fund(account, 10_000);

        Assert.Throws<InsufficientFundsException>(() => account.EnsureCanWithdraw(15_001));
    }

    [Fact]
    public void EnsureCanWithdraw_OnInternalAccount_IsNeverConstrained()
    {
        var account = Internal(AccountType.Asset);

        // No funds, no overdraft — but internal GL accounts are not funds-checked.
        account.EnsureCanWithdraw(long.MaxValue / 2);
    }

    [Fact]
    public void PlaceHold_ReducesAvailable_ButNotClearedBalance()
    {
        var account = Customer();
        Fund(account, 10_000);

        account.PlaceHold(3_000);

        Assert.Equal(10_000, account.BalanceMinor);
        Assert.Equal(3_000, account.HeldMinor);
        Assert.Equal(7_000, account.AvailableMinor);
    }

    [Fact]
    public void PlaceHold_BeyondOverdraft_ThrowsInsufficientFunds()
    {
        var account = Customer(overdraftLimitMinor: 1_000);
        Fund(account, 10_000);

        Assert.Throws<InsufficientFundsException>(() => account.PlaceHold(11_001));
    }

    [Fact]
    public void ReleaseHold_RestoresAvailable()
    {
        var account = Customer();
        Fund(account, 10_000);
        account.PlaceHold(3_000);

        account.ReleaseHold(3_000);

        Assert.Equal(0, account.HeldMinor);
        Assert.Equal(10_000, account.AvailableMinor);
    }

    [Fact]
    public void Close_WithNonZeroBalance_Throws()
    {
        var account = Customer();
        Fund(account, 500);

        var ex = Assert.Throws<DomainException>(() => account.Close());
        Assert.Equal("account.non_zero_balance", ex.Code);
    }

    [Fact]
    public void Close_WithActiveHold_Throws()
    {
        var account = Customer(overdraftLimitMinor: 5_000);
        account.PlaceHold(1_000); // balance stays 0, but an active hold remains (allowed by overdraft)

        var ex = Assert.Throws<DomainException>(() => account.Close());
        Assert.Equal("account.non_zero_balance", ex.Code);
    }

    [Fact]
    public void Close_WithZeroBalanceAndNoHolds_Succeeds()
    {
        var account = Customer();

        account.Close();

        Assert.Equal(AccountStatus.Closed, account.Status);
    }

    [Fact]
    public void EnsurePostable_ControlAccount_Throws()
    {
        var control = Account.Create(
            Guid.NewGuid(), "DEPOSITS-KES", "Deposits", AccountType.Liability, Currency.KES,
            DateTimeOffset.UtcNow, isControlAccount: true);

        var ex = Assert.Throws<DomainException>(() => control.EnsurePostable());
        Assert.Equal("account.control_account", ex.Code);
    }

    [Fact]
    public void EnsurePostable_FrozenAccount_Throws()
    {
        var account = Customer();
        account.Freeze();

        var ex = Assert.Throws<DomainException>(() => account.EnsurePostable());
        Assert.Equal("account.not_active", ex.Code);
    }

    [Fact]
    public void FreezeThenActivate_RestoresActiveStatus()
    {
        var account = Customer();

        account.Freeze();
        Assert.Equal(AccountStatus.Frozen, account.Status);

        account.Activate();
        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Fact]
    public void Version_IncrementsOnEveryMutation()
    {
        var account = Customer();
        var v0 = account.Version;

        account.ApplyPosting(PostingDirection.Credit, 100);

        Assert.True(account.Version > v0);
    }
}
