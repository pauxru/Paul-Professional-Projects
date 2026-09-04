namespace ExampleBank.Ledger.Domain.Journal;

public enum PostingDirection
{
    Debit = 1,
    Credit = 2,
}

public static class PostingDirectionExtensions
{
    public static PostingDirection Opposite(this PostingDirection direction) =>
        direction == PostingDirection.Debit ? PostingDirection.Credit : PostingDirection.Debit;
}

/// <summary>The economic reason a journal entry exists. Drives reporting and reconciliation.</summary>
public enum EntryType
{
    Transfer = 1,
    Fee = 2,
    Interest = 3,
    Adjustment = 4,
    Reversal = 5,
    FxConversion = 6,
    Settlement = 7,
}
