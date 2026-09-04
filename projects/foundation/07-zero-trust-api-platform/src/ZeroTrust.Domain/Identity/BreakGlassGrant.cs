using ZeroTrust.Domain.Common;

namespace ZeroTrust.Domain.Identity;

/// <summary>
/// Break-glass emergency admin access. Requires an approver at request time,
/// is time-boxed, and every use is audited with a correlation id.
/// </summary>
public sealed class BreakGlassGrant : Entity
{
    public string Subject { get; private set; } = default!;
    public string Requestor { get; private set; } = default!;
    public string Approver { get; private set; } = default!;
    public string Justification { get; private set; } = default!;
    public DateTime ExpiresAtUtc { get; private set; }
    public bool Used { get; private set; }
    public bool Revoked { get; private set; }

    private BreakGlassGrant() { }

    public BreakGlassGrant(string subject, string requestor, string approver, string justification, DateTime expiresAtUtc)
    {
        if (string.Equals(requestor, approver, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Break-glass approver must differ from requestor (two-person rule).");
        if (string.IsNullOrWhiteSpace(justification) || justification.Length < 10)
            throw new DomainException("Break-glass requires a justification of at least 10 characters.");
        Subject = subject;
        Requestor = requestor;
        Approver = approver;
        Justification = justification;
        ExpiresAtUtc = expiresAtUtc;
    }

    public void MarkUsed() => Used = true;
    public void Revoke() => Revoked = true;
    public bool IsActive(DateTime nowUtc) => !Revoked && !Used && ExpiresAtUtc > nowUtc;
}
