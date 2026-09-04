namespace Idp.Domain.Documents;

/// <summary>Business classification of an ingested document.</summary>
public enum DocumentType
{
    Unknown = 0,
    Invoice = 1,
    PurchaseOrder = 2,
    DeliveryNote = 3
}

/// <summary>
/// Explicit pipeline states. Transitions are guarded by <see cref="PipelineStateMachine"/>.
/// Received -> Classified -> Extracted -> Validated -> (AutoApproved | InReview) -> Corrected
///   -> Exported | Rejected | Failed.
/// </summary>
public enum PipelineState
{
    Received = 0,
    Classified = 1,
    Extracted = 2,
    Validated = 3,
    AutoApproved = 4,
    InReview = 5,
    Corrected = 6,
    Exported = 7,
    Rejected = 8,
    Failed = 9
}

/// <summary>Routing decision produced by confidence aggregation and the guardrail layer.</summary>
public enum RoutingDecision
{
    AutoApprove = 0,
    Review = 1,
    Reject = 2
}

/// <summary>Which extraction strategy produced a field value (drives per-field confidence).</summary>
public enum ExtractionStrategy
{
    None = 0,
    Anchor = 1,
    LearnedAnchor = 2,
    Regex = 3,
    Positional = 4,
    TableCell = 5,
    Derived = 6
}

/// <summary>Outcome of a single deterministic validation rule.</summary>
public enum ValidationOutcome
{
    Pass = 0,
    Warn = 1,
    Fail = 2
}
