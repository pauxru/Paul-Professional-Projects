using ZeroTrust.Domain.Common;

namespace ZeroTrust.Domain.Customer;

public sealed class Account : Entity
{
    public string AccountNumber { get; private set; } = default!;
    public string OwnerSubject { get; private set; } = default!;
    public string Nickname { get; private set; } = default!;
    public string Currency { get; private set; } = "USD";
    public decimal BalanceMinorUnits { get; private set; }

    private Account() { }

    public Account(string accountNumber, string ownerSubject, string nickname, string currency, decimal balanceMinorUnits)
    {
        AccountNumber = accountNumber;
        OwnerSubject = ownerSubject;
        Nickname = nickname;
        Currency = currency;
        BalanceMinorUnits = balanceMinorUnits;
    }

    public bool IsOwnedBy(string subject) =>
        !string.IsNullOrEmpty(subject) &&
        string.Equals(OwnerSubject, subject, StringComparison.OrdinalIgnoreCase);
}
