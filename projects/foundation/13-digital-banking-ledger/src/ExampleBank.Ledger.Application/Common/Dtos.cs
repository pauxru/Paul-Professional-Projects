using ExampleBank.Ledger.Domain.Accounts;
using ExampleBank.Ledger.Domain.Journal;

namespace ExampleBank.Ledger.Application.Common;

public sealed record PostingDto(
    Guid AccountId,
    string Direction,
    long AmountMinor,
    string Currency)
{
    public static PostingDto From(Posting p) =>
        new(p.AccountId, p.Direction.ToString(), p.AmountMinor, p.Currency);
}

/// <summary>The canonical response for any posted journal entry.</summary>
public sealed record EntryResult(
    Guid Id,
    long SequenceNumber,
    string Type,
    DateOnly ValueDate,
    DateTimeOffset BookingTimestamp,
    string Description,
    string? Reference,
    string SourceSystem,
    string CorrelationId,
    Guid? ReversalOfEntryId,
    string PreviousHash,
    string Hash,
    IReadOnlyList<PostingDto> Postings)
{
    public static EntryResult From(JournalEntry e) => new(
        e.Id,
        e.SequenceNumber,
        e.Type.ToString(),
        e.ValueDate,
        e.BookingTimestamp,
        e.Description,
        e.Reference,
        e.SourceSystem,
        e.CorrelationId,
        e.ReversalOfEntryId,
        e.PreviousHash,
        e.Hash,
        e.Postings.OrderBy(p => p.Sequence).Select(PostingDto.From).ToList());
}

public sealed record AccountDto(
    Guid Id,
    string Code,
    string Name,
    string Type,
    string NormalBalance,
    string Currency,
    string Status,
    Guid? ParentId,
    bool IsControlAccount,
    bool IsCustomerAccount,
    long OverdraftLimitMinor,
    long BalanceMinor,
    long AvailableMinor,
    long HeldMinor,
    long Version)
{
    public static AccountDto From(Account a) => new(
        a.Id, a.Code, a.Name, a.Type.ToString(), a.NormalBalance.ToString(), a.Currency,
        a.Status.ToString(), a.ParentId, a.IsControlAccount, a.IsCustomerAccount,
        a.OverdraftLimitMinor, a.BalanceMinor, a.AvailableMinor, a.HeldMinor, a.Version);
}
