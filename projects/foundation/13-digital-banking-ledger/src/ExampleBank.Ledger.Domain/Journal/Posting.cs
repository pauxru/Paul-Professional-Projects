using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.Domain.Journal;

/// <summary>
/// One leg of a journal entry: a positive amount posted to a single account on the debit or
/// credit side. Postings are immutable once created — the ledger is append-only.
/// </summary>
public sealed class Posting
{
    private Posting() { } // EF

    public Guid Id { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public Guid AccountId { get; private set; }
    public PostingDirection Direction { get; private set; }
    public long AmountMinor { get; private set; }
    public string Currency { get; private set; } = null!;

    /// <summary>Ordinal within the entry, for deterministic hashing and display.</summary>
    public int Sequence { get; private set; }

    public Money Amount => new(AmountMinor, Monetary.Currency.FromCode(Currency));

    public static Posting Create(Guid accountId, PostingDirection direction, Money amount, int sequence)
    {
        if (amount.MinorUnits <= 0)
        {
            throw new DomainException("posting.non_positive", "Posting amount must be strictly positive.");
        }

        return new Posting
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Direction = direction,
            AmountMinor = amount.MinorUnits,
            Currency = amount.Currency.Code,
            Sequence = sequence,
        };
    }

    internal void AttachTo(Guid journalEntryId) => JournalEntryId = journalEntryId;
}
