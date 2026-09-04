using ZeroTrust.Domain.Common;

namespace ZeroTrust.Domain.Customer;

public sealed class Statement : Entity
{
    public Guid AccountId { get; private set; }
    public string OwnerSubject { get; private set; } = default!;
    public int Year { get; private set; }
    public int Month { get; private set; }
    public decimal OpeningBalanceMinorUnits { get; private set; }
    public decimal ClosingBalanceMinorUnits { get; private set; }
    public string Currency { get; private set; } = "USD";

    private Statement() { }

    public Statement(Guid accountId, string ownerSubject, int year, int month, decimal opening, decimal closing, string currency)
    {
        AccountId = accountId;
        OwnerSubject = ownerSubject;
        Year = year;
        Month = month;
        OpeningBalanceMinorUnits = opening;
        ClosingBalanceMinorUnits = closing;
        Currency = currency;
    }
}
