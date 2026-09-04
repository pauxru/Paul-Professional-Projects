using ExampleBank.Ledger.Domain.Accounts;
using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Journal;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.Application.Common;

/// <summary>
/// Helpers that build balanced two-legged postings while keeping debit/credit directions correct
/// for each account's normal side, and applying the effect to the cached balances.
/// </summary>
public static class ValueMovement
{
    /// <summary>
    /// Moves <paramref name="amount"/> from <paramref name="source"/> (decreased) to
    /// <paramref name="destination"/> (increased). Both accounts must share a normal balance side so
    /// the entry balances (one debit, one credit) and the semantics are a genuine transfer of value.
    /// </summary>
    public static IReadOnlyList<Posting> Transfer(Account source, Account destination, Money amount, int startSequence = 0)
    {
        if (source.NormalBalance != destination.NormalBalance)
        {
            throw new DomainException(
                "posting.normal_side_mismatch",
                $"Value transfer requires accounts on the same normal side ({source.Code}:{source.NormalBalance}, {destination.Code}:{destination.NormalBalance}).");
        }

        var srcDir = source.DecreaseDirection;
        var dstDir = destination.IncreaseDirection; // opposite of srcDir when normal sides match

        var srcPosting = Posting.Create(source.Id, srcDir, amount, startSequence);
        var dstPosting = Posting.Create(destination.Id, dstDir, amount, startSequence + 1);

        source.ApplyPosting(srcDir, amount.MinorUnits);
        destination.ApplyPosting(dstDir, amount.MinorUnits);
        return new[] { srcPosting, dstPosting };
    }

    /// <summary>
    /// Books a charge that decreases <paramref name="payer"/> and increases a counterpart
    /// (e.g. income/expense) account, e.g. a fee or interest posting.
    /// </summary>
    public static IReadOnlyList<Posting> DecreaseIncrease(
        Account decrease, Account increase, Money amount, int startSequence = 0)
    {
        var decDir = decrease.DecreaseDirection;
        var incDir = increase.IncreaseDirection;
        if (decDir == incDir)
        {
            throw new DomainException(
                "posting.unbalanced_pair",
                $"Accounts {decrease.Code} and {increase.Code} cannot form a balanced pair for this operation.");
        }

        var decPosting = Posting.Create(decrease.Id, decDir, amount, startSequence);
        var incPosting = Posting.Create(increase.Id, incDir, amount, startSequence + 1);
        decrease.ApplyPosting(decDir, amount.MinorUnits);
        increase.ApplyPosting(incDir, amount.MinorUnits);
        return new[] { decPosting, incPosting };
    }

    /// <summary>
    /// Books an explicit debit leg and credit leg (the most general balanced pair). Applies each
    /// posting to the corresponding account's cached balances. Always balanced by construction.
    /// </summary>
    public static IReadOnlyList<Posting> DebitCredit(Account debit, Account credit, Money amount, int startSequence = 0)
    {
        var debitPosting = Posting.Create(debit.Id, PostingDirection.Debit, amount, startSequence);
        var creditPosting = Posting.Create(credit.Id, PostingDirection.Credit, amount, startSequence + 1);
        debit.ApplyPosting(PostingDirection.Debit, amount.MinorUnits);
        credit.ApplyPosting(PostingDirection.Credit, amount.MinorUnits);
        return new[] { debitPosting, creditPosting };
    }
}
