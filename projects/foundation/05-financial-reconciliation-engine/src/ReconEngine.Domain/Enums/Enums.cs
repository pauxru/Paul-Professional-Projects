namespace ReconEngine.Domain.Enums;

/// <summary>Which side of the reconciliation a record originates from.</summary>
public enum RecordSource
{
    /// <summary>The organisation's own ledger / transaction system.</summary>
    Internal = 0,

    /// <summary>The external settlement file from the PSP / bank.</summary>
    External = 1,
}

/// <summary>Business status of a single transaction / settlement line.</summary>
public enum TransactionStatus
{
    Unknown = 0,
    Pending = 1,
    Captured = 2,
    Settled = 3,
    Refunded = 4,
    Reversed = 5,
    Failed = 6,
    ChargedBack = 7,
}

/// <summary>Reconciliation state of an individual record; drives carry-forward and idempotent re-runs.</summary>
public enum ReconStatus
{
    /// <summary>Not yet reconciled, or eligible to be re-evaluated on the next run.</summary>
    Pending = 0,

    /// <summary>Matched (automatically or via a manual resolution) and excluded from future runs.</summary>
    Matched = 1,

    /// <summary>Currently attached to an open reconciliation exception.</summary>
    Exception = 2,
}

/// <summary>Shape of a match produced by the engine.</summary>
public enum MatchKind
{
    OneToOne = 0,
    ManyToOne = 1,   // many internal transactions settled by one external line
    OneToMany = 2,   // one internal transaction split across many external lines
    FeeAdjusted = 3, // internal = external + fee
    Refund = 4,      // negative amount pairing to an original capture
}

/// <summary>The catalogue of reconciliation exception classes.</summary>
public enum ExceptionType
{
    MissingInExternal = 0,
    MissingInInternal = 1,
    AmountMismatch = 2,
    CurrencyMismatch = 3,
    DuplicateInternal = 4,
    DuplicateExternal = 5,
    StatusMismatch = 6,
    FeeVariance = 7,
    DateOutOfWindow = 8,
    Unmatched = 9,
}

public enum ExceptionSeverity
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3,
}

/// <summary>Lifecycle state of an exception in the manual-resolution workflow.</summary>
public enum ExceptionStatus
{
    Open = 0,
    Assigned = 1,
    PendingApproval = 2, // four-eyes: awaiting a second reviewer
    Resolved = 3,
    Reopened = 4,
}

/// <summary>Reason codes captured when an analyst resolves an exception.</summary>
public enum ResolutionReasonCode
{
    WriteOff = 0,
    ManualMatch = 1,
    RaiseWithProvider = 2,
    Reprocess = 3,
    Ignore = 4,
}

/// <summary>Terminal outcome of a reconciliation run.</summary>
public enum RunStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
}
