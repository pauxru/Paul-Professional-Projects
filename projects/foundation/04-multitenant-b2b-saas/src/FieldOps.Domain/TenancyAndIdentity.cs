using System.Security.Cryptography;
using System.Text;

namespace FieldOps.Domain;

public sealed class Organization
{
    private Organization()
    {
    }

    public Organization(Guid id, string slug, string name, string region, SubscriptionPlan plan, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new DomainRuleException("Organization id is required.");
        if (string.IsNullOrWhiteSpace(slug)) throw new DomainRuleException("Organization slug is required.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainRuleException("Organization name is required.");

        Id = id;
        Slug = slug.Trim().ToLowerInvariant();
        Name = name.Trim();
        Region = string.IsNullOrWhiteSpace(region) ? "KE" : region.Trim().ToUpperInvariant();
        Plan = plan;
        Status = OrganizationStatus.Trial;
        SettingsJson = "{}";
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Slug { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public OrganizationStatus Status { get; private set; }
    public string SettingsJson { get; private set; } = "{}";
    public string Region { get; private set; } = string.Empty;
    public SubscriptionPlan Plan { get; private set; }
    public int ConsecutivePaymentFailures { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public void UpdateSettings(string settingsJson) =>
        SettingsJson = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson;

    public void ChangePlan(SubscriptionPlan plan)
    {
        Plan = plan;
        if (Status == OrganizationStatus.Trial)
        {
            Status = OrganizationStatus.Active;
        }
    }

    public void RecordPaymentFailure(int suspendAfterFailures)
    {
        ConsecutivePaymentFailures++;
        Status = ConsecutivePaymentFailures >= suspendAfterFailures
            ? OrganizationStatus.Suspended
            : OrganizationStatus.PastDue;
    }

    public void RecordPaymentSuccess()
    {
        ConsecutivePaymentFailures = 0;
        Status = OrganizationStatus.Active;
    }

    public void Suspend() => Status = OrganizationStatus.Suspended;
}

public sealed class AppUser
{
    private AppUser()
    {
    }

    public AppUser(Guid id, string email, string displayName, DateTimeOffset createdAt)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        Email = email.Trim().ToLowerInvariant();
        DisplayName = displayName.Trim();
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
}

public sealed class Membership : ITenantOwned
{
    private Membership()
    {
    }

    public Membership(Guid tenantId, Guid userId, MemberRole role, DateTimeOffset joinedAt)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        UserId = userId;
        Role = role;
        JoinedAt = joinedAt;
        IsActive = true;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; private set; }
    public MemberRole Role { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset JoinedAt { get; private set; }

    public void ChangeRole(MemberRole role) => Role = role;
    public void Deactivate() => IsActive = false;
}

public sealed class Team : ITenantOwned
{
    private Team()
    {
    }

    public Team(Guid tenantId, string name)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new DomainRuleException("Team name is required.")
            : name.Trim();
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; set; }
    public string Name { get; private set; } = string.Empty;
}

public sealed class TeamMembership : ITenantOwned
{
    private TeamMembership()
    {
    }

    public TeamMembership(Guid tenantId, Guid teamId, Guid userId)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        TeamId = teamId;
        UserId = userId;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; set; }
    public Guid TeamId { get; private set; }
    public Guid UserId { get; private set; }
}

public sealed class Invitation : ITenantOwned
{
    private Invitation()
    {
    }

    public Invitation(Guid tenantId, string email, MemberRole role, string rawToken, DateTimeOffset expiresAt, DateTimeOffset createdAt)
    {
        if (expiresAt <= createdAt) throw new DomainRuleException("Invitation expiry must be in the future.");
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Email = email.Trim().ToLowerInvariant();
        Role = role;
        TokenHash = Hash(rawToken);
        ExpiresAt = expiresAt;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; set; }
    public string Email { get; private set; } = string.Empty;
    public MemberRole Role { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }

    public bool Matches(string rawToken) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(TokenHash),
            Convert.FromHexString(Hash(rawToken)));

    public void Accept(string rawToken, DateTimeOffset now)
    {
        if (AcceptedAt is not null) throw new DomainRuleException("Invitation has already been accepted.");
        if (now > ExpiresAt) throw new DomainRuleException("Invitation has expired.");
        if (!Matches(rawToken)) throw new DomainRuleException("Invitation token is invalid.");
        AcceptedAt = now;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
