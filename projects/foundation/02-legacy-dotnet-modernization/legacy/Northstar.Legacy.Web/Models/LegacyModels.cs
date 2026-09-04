namespace Northstar.Legacy.Web.Models;

public sealed class LegacyClaim
{
    public long Id { get; set; }
    public string ClaimReference { get; set; } = string.Empty;
    public long PolicyId { get; set; }
    public string PolicyholderName { get; set; } = string.Empty;
    public string Status { get; set; } = "Submitted";
    public decimal ClaimedAmount { get; set; }
    public decimal Deductible { get; set; }
    public decimal PolicyLimit { get; set; }
    public decimal ReserveAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public string? Adjuster { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class LegacyPolicy
{
    public long Id { get; set; }
    public string PolicyNumber { get; set; } = string.Empty;
    public string PolicyholderName { get; set; } = string.Empty;
    public decimal Deductible { get; set; }
    public decimal PolicyLimit { get; set; }
    public string Currency { get; set; } = "USD";
}

public sealed class LegacyPolicyholder
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public sealed class LegacyAdministrationViewModel
{
    public List<LegacyPolicyholder> Policyholders { get; set; } = [];
    public List<LegacyPolicy> Policies { get; set; } = [];
}

public sealed class LegacyClaimIntakeForm
{
    public string PolicyNumber { get; set; } = string.Empty;
    public string PolicyholderName { get; set; } = string.Empty;
    public decimal ClaimedAmount { get; set; }
    public string Currency { get; set; } = "USD";
}
