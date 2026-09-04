namespace ExampleBank.Ledger.Application.Common;

public sealed record TrialBalanceLine(
    Guid AccountId,
    string Code,
    string Name,
    string Type,
    string Currency,
    long TotalDebitsMinor,
    long TotalCreditsMinor,
    long DebitBalanceMinor,
    long CreditBalanceMinor);

public sealed record TrialBalanceCurrencyTotal(
    string Currency,
    long TotalDebitsMinor,
    long TotalCreditsMinor,
    bool IsBalanced);

/// <summary>
/// A trial balance: every account's debit/credit totals grouped by currency. A correct ledger
/// balances to zero in every currency (Σ debits = Σ credits).
/// </summary>
public sealed record TrialBalanceResult(
    IReadOnlyList<TrialBalanceLine> Lines,
    IReadOnlyList<TrialBalanceCurrencyTotal> Totals,
    bool IsBalanced);
