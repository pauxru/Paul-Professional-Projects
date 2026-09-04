namespace ExampleBank.Ledger.Domain.Accounts;

/// <summary>The five classical account classifications in double-entry bookkeeping.</summary>
public enum AccountType
{
    Asset = 1,
    Liability = 2,
    Equity = 3,
    Income = 4,
    Expense = 5,
}

/// <summary>The side on which an account increases.</summary>
public enum NormalBalance
{
    Debit = 1,
    Credit = 2,
}

public enum AccountStatus
{
    Active = 1,
    Frozen = 2,
    Closed = 3,
}

public static class AccountTypeExtensions
{
    /// <summary>
    /// Assets and Expenses increase on the debit side; Liabilities, Equity and Income increase
    /// on the credit side. This is the sign convention the whole ledger derives balances from.
    /// </summary>
    public static NormalBalance NormalBalance(this AccountType type) => type switch
    {
        AccountType.Asset or AccountType.Expense => Accounts.NormalBalance.Debit,
        _ => Accounts.NormalBalance.Credit,
    };
}
