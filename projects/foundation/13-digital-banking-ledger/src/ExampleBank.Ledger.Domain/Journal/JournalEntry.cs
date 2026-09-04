using System.Globalization;
using System.Text;
using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.Domain.Journal;

/// <summary>
/// The ledger aggregate root. A journal entry bundles 2..N postings that are balanced per
/// currency and, once sealed into the hash chain, is treated as immutable. Corrections are made
/// only by appending a <see cref="EntryType.Reversal"/> entry that references the original.
/// </summary>
public sealed class JournalEntry
{
    private readonly List<Posting> _postings = new();

    private JournalEntry() { } // EF

    public Guid Id { get; private set; }

    /// <summary>Monotonic position in the append-only chain; assigned when the entry is sealed.</summary>
    public long SequenceNumber { get; private set; }

    public EntryType Type { get; private set; }
    public DateOnly ValueDate { get; private set; }
    public DateTimeOffset BookingTimestamp { get; private set; }
    public string Description { get; private set; } = null!;
    public string? Reference { get; private set; }
    public string SourceSystem { get; private set; } = null!;
    public string CorrelationId { get; private set; } = null!;
    public string? IdempotencyKey { get; private set; }

    /// <summary>Set on reversal entries: the id of the entry being (partially) reversed.</summary>
    public Guid? ReversalOfEntryId { get; private set; }

    public string PreviousHash { get; private set; } = LedgerHash.GenesisHash;
    public string Hash { get; private set; } = string.Empty;

    public IReadOnlyList<Posting> Postings => _postings;

    public bool IsSealed => !string.IsNullOrEmpty(Hash);

    public static JournalEntry Create(
        EntryType type,
        DateOnly valueDate,
        DateTimeOffset bookingTimestamp,
        string description,
        string sourceSystem,
        string correlationId,
        IReadOnlyCollection<Posting> postings,
        string? reference = null,
        string? idempotencyKey = null,
        Guid? reversalOfEntryId = null)
    {
        if (postings is null || postings.Count < 2)
        {
            throw new UnbalancedEntryException("A journal entry requires at least two postings.");
        }

        ValidateBalanced(type, postings);

        var entry = new JournalEntry
        {
            Id = Guid.NewGuid(),
            Type = type,
            ValueDate = valueDate,
            BookingTimestamp = bookingTimestamp,
            Description = string.IsNullOrWhiteSpace(description) ? type.ToString() : description.Trim(),
            SourceSystem = string.IsNullOrWhiteSpace(sourceSystem) ? "ledger" : sourceSystem.Trim(),
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("n") : correlationId.Trim(),
            Reference = reference?.Trim(),
            IdempotencyKey = idempotencyKey,
            ReversalOfEntryId = reversalOfEntryId,
        };

        var ordered = postings.OrderBy(p => p.Sequence).ToList();
        foreach (var posting in ordered)
        {
            posting.AttachTo(entry.Id);
            entry._postings.Add(posting);
        }

        return entry;
    }

    /// <summary>
    /// Enforces the double-entry invariant: within each currency, total debits must equal total
    /// credits. Non-FX entries may only reference a single currency; FX entries may span
    /// currencies but each currency must still balance independently.
    /// </summary>
    private static void ValidateBalanced(EntryType type, IReadOnlyCollection<Posting> postings)
    {
        var byCurrency = postings.GroupBy(p => p.Currency).ToList();

        if (type != EntryType.FxConversion && byCurrency.Count > 1)
        {
            throw new MixedCurrencyException(
                $"Entry type {type} may not mix currencies ({string.Join(", ", byCurrency.Select(g => g.Key))}). " +
                "Use an FxConversion entry for cross-currency movements.");
        }

        foreach (var group in byCurrency)
        {
            long debits = group.Where(p => p.Direction == PostingDirection.Debit).Sum(p => p.AmountMinor);
            long credits = group.Where(p => p.Direction == PostingDirection.Credit).Sum(p => p.AmountMinor);
            if (debits != credits)
            {
                throw new UnbalancedEntryException(
                    $"Entry is unbalanced in {group.Key}: debits {debits} != credits {credits}.");
            }
        }
    }

    /// <summary>
    /// Finalises the entry's position in the hash chain. Called exactly once, at append time,
    /// when its sequence number and the previous entry's hash are known.
    /// </summary>
    public void Seal(long sequenceNumber, string previousHash)
    {
        if (IsSealed)
        {
            throw new AppendOnlyViolationException($"Entry {Id} is already sealed.");
        }

        SequenceNumber = sequenceNumber;
        PreviousHash = previousHash;
        Hash = LedgerHash.Compute(previousHash, ComputeCanonicalContent());
    }

    /// <summary>Deterministic, culture-invariant serialization of the entry for hashing.</summary>
    public string ComputeCanonicalContent()
    {
        var sb = new StringBuilder();
        sb.Append(SequenceNumber).Append('|');
        sb.Append((int)Type).Append('|');
        sb.Append(ValueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(BookingTimestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(Description).Append('|');
        sb.Append(Reference ?? string.Empty).Append('|');
        sb.Append(SourceSystem).Append('|');
        sb.Append(CorrelationId).Append('|');
        sb.Append(ReversalOfEntryId?.ToString("n") ?? string.Empty).Append('|');

        foreach (var posting in _postings.OrderBy(p => p.Sequence))
        {
            sb.Append('#').Append(posting.Sequence).Append(':')
              .Append(posting.AccountId.ToString("n")).Append(':')
              .Append((int)posting.Direction).Append(':')
              .Append(posting.AmountMinor).Append(':')
              .Append(posting.Currency).Append(';');
        }

        return sb.ToString();
    }

    /// <summary>Recomputes the hash from stored content to verify it has not been tampered with.</summary>
    public string RecomputeHash() => LedgerHash.Compute(PreviousHash, ComputeCanonicalContent());

    public Money TotalDebits(Currency currency) => new(
        _postings.Where(p => p.Currency == currency.Code && p.Direction == PostingDirection.Debit).Sum(p => p.AmountMinor),
        currency);
}
