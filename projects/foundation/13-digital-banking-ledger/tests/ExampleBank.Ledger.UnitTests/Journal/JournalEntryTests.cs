using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Journal;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.UnitTests.Journal;

public sealed class JournalEntryTests
{
    private static readonly Guid AccountA = Guid.NewGuid();
    private static readonly Guid AccountB = Guid.NewGuid();

    private static Posting Debit(Guid account, long minor, Currency currency, int sequence) =>
        Posting.Create(account, PostingDirection.Debit, new Money(minor, currency), sequence);

    private static Posting Credit(Guid account, long minor, Currency currency, int sequence) =>
        Posting.Create(account, PostingDirection.Credit, new Money(minor, currency), sequence);

    private static JournalEntry Create(EntryType type, params Posting[] postings) => JournalEntry.Create(
        type, new DateOnly(2024, 1, 1), DateTimeOffset.UtcNow, "test", "unit-test", "corr-1", postings);

    [Fact]
    public void Create_BalancedTransfer_Succeeds()
    {
        var entry = Create(
            EntryType.Transfer,
            Debit(AccountA, 10_000, Currency.USD, 0),
            Credit(AccountB, 10_000, Currency.USD, 1));

        Assert.Equal(2, entry.Postings.Count);
        Assert.False(entry.IsSealed);
    }

    [Fact]
    public void Create_DebitsNotEqualCredits_ThrowsUnbalanced()
    {
        Assert.Throws<UnbalancedEntryException>(() => Create(
            EntryType.Transfer,
            Debit(AccountA, 10_000, Currency.USD, 0),
            Credit(AccountB, 9_000, Currency.USD, 1)));
    }

    [Fact]
    public void Create_NonFxEntryMixingCurrencies_ThrowsMixedCurrency()
    {
        Assert.Throws<MixedCurrencyException>(() => Create(
            EntryType.Transfer,
            Debit(AccountA, 10_000, Currency.USD, 0),
            Credit(AccountB, 10_000, Currency.KES, 1)));
    }

    [Fact]
    public void Create_FxConversionBalancedPerCurrency_Succeeds()
    {
        var clearingUsd = Guid.NewGuid();
        var clearingKes = Guid.NewGuid();

        var entry = Create(
            EntryType.FxConversion,
            Debit(AccountA, 10_000, Currency.USD, 0),
            Credit(clearingUsd, 10_000, Currency.USD, 1),
            Debit(clearingKes, 1_300_000, Currency.KES, 2),
            Credit(AccountB, 1_300_000, Currency.KES, 3));

        Assert.Equal(4, entry.Postings.Count);
    }

    [Fact]
    public void Create_FxConversionUnbalancedInOneCurrency_ThrowsUnbalanced()
    {
        var clearingUsd = Guid.NewGuid();
        var clearingKes = Guid.NewGuid();

        Assert.Throws<UnbalancedEntryException>(() => Create(
            EntryType.FxConversion,
            Debit(AccountA, 10_000, Currency.USD, 0),
            Credit(clearingUsd, 10_000, Currency.USD, 1),
            Debit(clearingKes, 1_300_000, Currency.KES, 2),
            Credit(AccountB, 1_299_999, Currency.KES, 3)));
    }

    [Fact]
    public void Create_FewerThanTwoPostings_ThrowsUnbalanced()
    {
        Assert.Throws<UnbalancedEntryException>(() => Create(
            EntryType.Adjustment,
            Debit(AccountA, 10_000, Currency.USD, 0)));
    }

    [Fact]
    public void Seal_AssignsSequenceAndHash()
    {
        var entry = Create(
            EntryType.Transfer,
            Debit(AccountA, 10_000, Currency.USD, 0),
            Credit(AccountB, 10_000, Currency.USD, 1));

        entry.Seal(1, LedgerHash.GenesisHash);

        Assert.True(entry.IsSealed);
        Assert.Equal(1, entry.SequenceNumber);
        Assert.False(string.IsNullOrEmpty(entry.Hash));
        Assert.Equal(LedgerHash.GenesisHash, entry.PreviousHash);
    }

    [Fact]
    public void Seal_CalledTwice_ThrowsAppendOnlyViolation()
    {
        var entry = Create(
            EntryType.Transfer,
            Debit(AccountA, 10_000, Currency.USD, 0),
            Credit(AccountB, 10_000, Currency.USD, 1));

        entry.Seal(1, LedgerHash.GenesisHash);

        Assert.Throws<AppendOnlyViolationException>(() => entry.Seal(2, "deadbeef"));
    }

    [Fact]
    public void Postings_NonPositiveAmount_ThrowsAtCreation()
    {
        Assert.Throws<DomainException>(() =>
            Posting.Create(AccountA, PostingDirection.Debit, new Money(0, Currency.USD), 0));
    }
}
