using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FieldOps.Domain;

namespace FieldOps.Application;

public sealed record InvitationCreated(Invitation Invitation, string Token);

public sealed class InvitationService(
    ITenantContext tenantContext,
    IInvitationRepository invitations,
    IMembershipRepository memberships,
    IClock clock)
{
    public async Task<InvitationCreated> CreateAsync(
        string email,
        MemberRole role,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var invitation = new Invitation(
            tenantContext.RequiredTenantId, email, role, token, clock.UtcNow.Add(lifetime), clock.UtcNow);
        await invitations.AddAsync(invitation, cancellationToken);
        await invitations.SaveAsync(cancellationToken);
        return new(invitation, token);
    }

    public async Task<Membership> AcceptAsync(string token, Guid userId, CancellationToken cancellationToken)
    {
        var invitation = await invitations.FindByTokenAsync(token, cancellationToken)
            ?? throw new KeyNotFoundException("Invitation was not found.");
        TenantGuard.EnsureSameTenant(tenantContext.RequiredTenantId, invitation);
        invitation.Accept(token, clock.UtcNow);
        var membership = new Membership(invitation.TenantId, userId, invitation.Role, clock.UtcNow);
        await memberships.AddAsync(membership, cancellationToken);
        await invitations.SaveAsync(cancellationToken);
        await memberships.SaveAsync(cancellationToken);
        return membership;
    }
}

public static class AuditHash
{
    public static string? Of(object? value)
    {
        if (value is null) return null;
        var json = JsonSerializer.Serialize(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
