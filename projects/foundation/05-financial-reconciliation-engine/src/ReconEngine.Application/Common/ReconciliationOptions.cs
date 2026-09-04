using System.ComponentModel.DataAnnotations;

namespace ReconEngine.Application.Common;

/// <summary>Bound from configuration and validated at startup.</summary>
public sealed class ReconciliationOptions
{
    public const string SectionName = "Reconciliation";

    /// <summary>Write-offs at or above this magnitude (minor units) require four-eyes approval.</summary>
    [Range(0, long.MaxValue)]
    public long WriteOffApprovalThresholdMinor { get; set; } = 100_000;

    /// <summary>Name of the ruleset seeded and used by default.</summary>
    [Required]
    public string DefaultRuleSetName { get; set; } = "default";

    /// <summary>Amount (minor units) at or above which a monetary discrepancy is treated as high severity.</summary>
    [Range(0, long.MaxValue)]
    public long HighSeverityAmountMinor { get; set; } = 50_000;
}
