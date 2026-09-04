using ExampleBank.Ledger.Domain.Journal;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.UnitTests.Journal;

public sealed class HashChainTests
{
    private static JournalEntry Entry(long amountMinor, string description = "test")
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        return JournalEntry.Create(
            EntryType.Transfer, new DateOnly(2024, 1, 1), DateTimeOffset.UtcNow, description,
            "unit-test", "corr-1",
            new[]
            {
                Posting.Create(a, PostingDirection.Debit, new Money(amountMinor, Currency.USD), 0),
                Posting.Create(b, PostingDirection.Credit, new Money(amountMinor, Currency.USD), 1),
            });
    }

    [Fact]
    public void RecomputeHash_OnUntamperedEntry_MatchesStoredHash()
    {
        var entry = Entry(10_000);
        entry.Seal(1, LedgerHash.GenesisHash);

        Assert.Equal(entry.Hash, entry.RecomputeHash());
    }

    [Fact]
    public void Hash_LinksToPreviousEntryHash()
    {
        var first = Entry(10_000);
        first.Seal(1, LedgerHash.GenesisHash);

        var second = Entry(20_000);
        second.Seal(2, first.Hash);

        Assert.Equal(first.Hash, second.PreviousHash);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void Hash_IsSensitiveToContent_TamperingIsDetectable()
    {
        // Two entries at the same chain position with different content must hash differently,
        // which is exactly what makes a retroactive edit detectable by a full-chain walk.
        var original = Entry(10_000);
        original.Seal(5, "prevhash");

        var tampered = Entry(10_001);
        tampered.Seal(5, "prevhash");

        Assert.NotEqual(original.Hash, tampered.Hash);
    }

    [Fact]
    public void Hash_IsSensitiveToPreviousHash()
    {
        var a = Entry(10_000);
        a.Seal(3, "prev-a");

        var b = Entry(10_000);
        b.Seal(3, "prev-b");

        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void Compute_IsDeterministic()
    {
        var hash1 = LedgerHash.Compute(LedgerHash.GenesisHash, "content");
        var hash2 = LedgerHash.Compute(LedgerHash.GenesisHash, "content");

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // SHA-256 hex
    }
}
