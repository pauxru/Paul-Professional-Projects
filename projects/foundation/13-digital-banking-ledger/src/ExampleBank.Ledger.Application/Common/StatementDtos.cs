namespace ExampleBank.Ledger.Application.Common;

/// <summary>A single account movement (one posting) enriched with its entry's descriptive fields.</summary>
public sealed record AccountMovement(
    Guid EntryId,
    long SequenceNumber,
    DateOnly ValueDate,
    DateTimeOffset BookingTimestamp,
    string Description,
    string? Reference,
    string Direction,
    long AmountMinor,
    string Currency);

/// <summary>A statement line: a movement plus the running balance after it is applied.</summary>
public sealed record StatementLine(
    Guid EntryId,
    long SequenceNumber,
    DateOnly ValueDate,
    DateTimeOffset BookingTimestamp,
    string Description,
    string? Reference,
    string Direction,
    long AmountMinor,
    long SignedAmountMinor,
    long RunningBalanceMinor);

/// <summary>
/// A period statement that ties out exactly: opening + Σ(signed movements) = closing.
/// </summary>
public sealed record StatementResult(
    Guid AccountId,
    string AccountCode,
    string Currency,
    DateOnly FromDate,
    DateOnly ToDate,
    long OpeningBalanceMinor,
    long ClosingBalanceMinor,
    long TotalMovements,
    IReadOnlyList<StatementLine> Lines,
    int Page,
    int PageSize);
