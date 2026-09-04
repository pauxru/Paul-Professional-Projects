namespace ExampleBank.Ledger.Domain.Common;

/// <summary>
/// Raised when a ledger invariant would be violated. These map to HTTP 422 at the edge:
/// the request was well-formed but the domain refused it (e.g. an unbalanced entry).
/// </summary>
public class DomainException : Exception
{
    public string Code { get; }

    public DomainException(string code, string message) : base(message)
    {
        Code = code;
    }
}

/// <summary>Debits did not equal credits for a currency inside a single journal entry.</summary>
public sealed class UnbalancedEntryException(string message)
    : DomainException("ledger.unbalanced_entry", message);

/// <summary>A non-FX entry referenced more than one currency.</summary>
public sealed class MixedCurrencyException(string message)
    : DomainException("ledger.mixed_currency", message);

/// <summary>An operation would push an account past its overdraft limit.</summary>
public sealed class InsufficientFundsException(string message)
    : DomainException("ledger.insufficient_funds", message);

/// <summary>An attempt was made to mutate an append-only ledger row.</summary>
public sealed class AppendOnlyViolationException(string message)
    : DomainException("ledger.append_only_violation", message);
