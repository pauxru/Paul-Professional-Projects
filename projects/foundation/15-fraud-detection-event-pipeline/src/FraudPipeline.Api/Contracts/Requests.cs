using System.ComponentModel.DataAnnotations;

namespace FraudPipeline.Api.Contracts;

public sealed class ScoreRequest
{
    [Required, StringLength(64)] public string TransactionRef { get; set; } = "";
    [Required, StringLength(64)] public string CardId { get; set; } = "";
    [Required, StringLength(64)] public string CustomerId { get; set; } = "";
    [Required, StringLength(64)] public string DeviceId { get; set; } = "";
    [Required, StringLength(64)] public string IpAddress { get; set; } = "";
    [Required, StringLength(64)] public string MerchantId { get; set; } = "";
    [Required, RegularExpression("^[0-9]{4}$")] public string Mcc { get; set; } = "";
    [Range(0.01, 100_000_000.0)] public decimal Amount { get; set; }
    [Required, StringLength(4)] public string Currency { get; set; } = "USD";
    [Required] public string Type { get; set; } = "CardNotPresent";
    [Range(-90.0, 90.0)] public double Latitude { get; set; }
    [Range(-180.0, 180.0)] public double Longitude { get; set; }
    [Required, StringLength(2)] public string Country { get; set; } = "US";
    public DateTimeOffset? OccurredAt { get; set; }
}

public sealed record ScoreResponse(
    string TransactionRef,
    int Score,
    string Decision,
    string RulesetVersion,
    IReadOnlyList<FiringSummary> RulesFired,
    double LatencyMs,
    bool BudgetExceeded,
    string Reasons);

public sealed record FiringSummary(string RuleId, string Kind, int Contribution, string Reason);

public sealed class IngestRequest
{
    [Required] public ScoreRequest Transaction { get; set; } = new();
    public bool AwaitScore { get; set; } = false;
}

public sealed record IngestResponse(string TransactionRef, string Status, int? Partition);

public sealed class CreateListEntryRequest
{
    [Required] public string Type { get; set; } = "Allow";
    [Required] public string Subject { get; set; } = "Card";
    [Required] public string Value { get; set; } = "";
    [Required] public string Reason { get; set; } = "";
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class ActivateRulesetRequest
{
    [Required] public string Version { get; set; } = "";
}

public sealed class ShadowRulesetRequest
{
    [Required] public string Version { get; set; } = "";
}

public sealed class SimulateRulesetRequest
{
    [Required] public string Version { get; set; } = "";
    [Range(1, 10000)] public int Limit { get; set; } = 500;
}

public sealed class CaseAssignRequest
{
    [Required] public string Investigator { get; set; } = "";
}

public sealed class CaseNoteRequest
{
    [Required] public string Author { get; set; } = "";
    [Required] public string Text { get; set; } = "";
}

public sealed class CaseDispositionRequest
{
    [Required] public string Disposition { get; set; } = "";
    [Required] public string Reason { get; set; } = "";
    [Required] public string By { get; set; } = "";
}

public sealed class CaseApprovalRequest
{
    [Required] public string Approver { get; set; } = "";
}

public sealed class TokenRequest
{
    [Required] public string Subject { get; set; } = "";
    [Required, MinLength(1)] public string[] Scopes { get; set; } = Array.Empty<string>();
}
