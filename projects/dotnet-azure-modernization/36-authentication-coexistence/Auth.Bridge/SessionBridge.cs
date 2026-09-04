using Auth.Legacy;
using Auth.Modern;
using Auth.Passwords;

namespace Auth.Bridge;

public sealed class UserRecord
{
    public required string Subject { get; init; }

    public required string Tenant { get; init; }

    public required IReadOnlySet<string> Roles { get; init; }

    /// <summary>Whatever format this user's hash happens to be in today.</summary>
    public required string PasswordHash { get; set; }

    /// <summary>Set the first time a login upgrades the stored hash.</summary>
    public DateTimeOffset? RehashedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
}

public sealed class UserDirectory
{
    private readonly Dictionary<string, UserRecord> _users = new(StringComparer.OrdinalIgnoreCase);

    public void Add(UserRecord user) => _users[user.Subject] = user;

    public UserRecord? Find(string subject) =>
        _users.TryGetValue(subject, out var user) ? user : null;

    public IReadOnlyCollection<UserRecord> All => _users.Values;

    public int CountByFormat(HashFormat format) =>
        _users.Values.Count(u => SafeFormat(u.PasswordHash) == format);

    private static HashFormat? SafeFormat(string stored)
    {
        try { return HashCodec.Parse(stored).Format; }
        catch (FormatException) { return null; }
    }
}

/// <summary>
/// Which authentication paths are switched on right now. The migration is a sequence of
/// changes to this record and nothing else.
/// </summary>
/// <remarks>
/// Making the migration state a value rather than a deployment means every intermediate
/// configuration is a thing tests can construct. The rollback plan stops being a document
/// and becomes an argument to a constructor -- which is the only form of rollback plan that
/// has ever been tested before it was needed.
/// </remarks>
public sealed record StackConfiguration(
    bool AcceptLegacyFormsTickets,
    bool AcceptWsFederation,
    bool AcceptOidc,
    bool RehashOnLogin)
{
    /// <summary>Day zero: only the old stacks exist.</summary>
    public static readonly StackConfiguration Before = new(true, true, false, false);

    /// <summary>The long middle, where all three run at once.</summary>
    public static readonly StackConfiguration Coexistence = new(true, true, true, true);

    /// <summary>After the cutoff date. This is the configuration nobody reaches by waiting.</summary>
    public static readonly StackConfiguration After = new(false, false, true, true);

    public int EnabledStacks =>
        (AcceptLegacyFormsTickets ? 1 : 0) + (AcceptWsFederation ? 1 : 0) + (AcceptOidc ? 1 : 0);
}

public enum BridgeFailure
{
    None,
    StackDisabled,
    RejectedByStack,
    UnknownUser,
}

public sealed record BridgeResult(
    CanonicalPrincipal? Principal,
    BridgeFailure Failure,
    AuthenticationSource? Source,
    string? UpgradedHash = null)
{
    public bool Ok => Failure == BridgeFailure.None;
}

/// <summary>
/// Accepts a credential from any enabled stack and produces one canonical principal.
/// </summary>
/// <remarks>
/// <para>
/// This class is the whole design. Everything upstream of it is three incompatible
/// authentication systems; everything downstream sees one identity type and one
/// authorization model. That is what makes it possible to switch a stack off without
/// touching application code -- and it is also what makes the security property below true
/// and uncomfortable.
/// </para>
/// <para>
/// Because every enabled path produces the <i>same</i> principal, with the same scopes, an
/// account's real security is the security of the <b>weakest enabled path</b>, not the one
/// its owner uses. A user who has moved to OIDC with a fresh Argon2id hash is still
/// reachable through the Forms ticket path for as long as that path is open, because the
/// bridge cannot tell the difference between a ticket minted for a migrated user and one
/// minted for anybody else. Improving a user's credential does not improve their account
/// until the other doors are shut.
/// </para>
/// </remarks>
public sealed class SessionBridge
{
    private readonly UserDirectory _directory;
    private readonly ITicketProtector _formsProtector;
    private readonly WsFederation _wsFederation;
    private readonly JwtCodec _jwt;
    private readonly IClaimsTransformer _transformer;
    private readonly MigratingPasswordHasher _hasher;

    public SessionBridge(
        UserDirectory directory,
        ITicketProtector formsProtector,
        WsFederation wsFederation,
        JwtCodec jwt,
        IClaimsTransformer transformer,
        MigratingPasswordHasher hasher,
        StackConfiguration configuration)
    {
        _directory = directory;
        _formsProtector = formsProtector;
        _wsFederation = wsFederation;
        _jwt = jwt;
        _transformer = transformer;
        _hasher = hasher;
        Configuration = configuration;
    }

    public StackConfiguration Configuration { get; set; }

    public BridgeResult FromFormsTicket(string ticket, DateTimeOffset now)
    {
        if (!Configuration.AcceptLegacyFormsTickets)
        {
            return new BridgeResult(null, BridgeFailure.StackDisabled, AuthenticationSource.FormsTicket);
        }

        var result = _formsProtector.Unprotect(ticket, now);
        if (!result.Ok || result.Ticket is null)
        {
            return new BridgeResult(null, BridgeFailure.RejectedByStack, AuthenticationSource.FormsTicket);
        }

        // The roles come out of the cookie, not out of the directory. That is how the legacy
        // application worked, and reproducing it faithfully is the point: a forged ticket
        // carries forged roles, and nothing downstream will contradict them.
        var roles = new HashSet<string>(result.Ticket.Roles, StringComparer.Ordinal);
        var user = _directory.Find(result.Ticket.Name);

        return Build(result.Ticket.Name, roles, user?.Tenant ?? "unknown",
                     AuthenticationSource.FormsTicket, now);
    }

    public BridgeResult FromWsFederation(string token, string audience, DateTimeOffset now)
    {
        if (!Configuration.AcceptWsFederation)
        {
            return new BridgeResult(null, BridgeFailure.StackDisabled, AuthenticationSource.WsFederation);
        }

        var validation = _wsFederation.Validate(token, audience, now);
        if (!validation.Ok || validation.Assertion is null)
        {
            return new BridgeResult(null, BridgeFailure.RejectedByStack, AuthenticationSource.WsFederation);
        }

        var roles = new HashSet<string>(validation.Assertion.Roles, StringComparer.Ordinal);
        var user = _directory.Find(validation.Assertion.NameIdentifier);

        return Build(validation.Assertion.NameIdentifier, roles, user?.Tenant ?? "unknown",
                     AuthenticationSource.WsFederation, now);
    }

    public BridgeResult FromIdToken(
        string idToken, string issuer, string audience, DateTimeOffset now, string? nonce = null)
    {
        if (!Configuration.AcceptOidc)
        {
            return new BridgeResult(null, BridgeFailure.StackDisabled, AuthenticationSource.Oidc);
        }

        var validation = _jwt.Validate(idToken, issuer, audience, now, nonce);
        if (!validation.Ok || validation.Claims is null || validation.Subject is null)
        {
            return new BridgeResult(null, BridgeFailure.RejectedByStack, AuthenticationSource.Oidc);
        }

        var user = _directory.Find(validation.Subject);
        if (user is null)
        {
            return new BridgeResult(null, BridgeFailure.UnknownUser, AuthenticationSource.Oidc);
        }

        return Build(user.Subject, user.Roles, user.Tenant, AuthenticationSource.Oidc, now);
    }

    /// <summary>
    /// The password login path, shared by the Forms and OIDC front doors. Returns the
    /// upgraded hash when one was produced, leaving the caller to decide whether to persist
    /// it -- because in a real system that write can fail, and a failed write must not fail
    /// the login.
    /// </summary>
    public (BridgeResult Result, VerifyResult Verification) SignIn(
        string subject, string password, AuthenticationSource source, DateTimeOffset now)
    {
        var user = _directory.Find(subject);

        // Verify even when the user does not exist. The hasher spends the same work on a
        // decoy, so the response time does not answer "does this account exist".
        var verification = _hasher.Verify(user?.PasswordHash, password);

        if (user is null)
        {
            return (new BridgeResult(null, BridgeFailure.UnknownUser, source), verification);
        }

        if (!verification.Succeeded)
        {
            return (new BridgeResult(null, BridgeFailure.RejectedByStack, source), verification);
        }

        user.LastLoginAt = now;

        string? upgraded = null;
        if (Configuration.RehashOnLogin &&
            verification.Outcome == VerifyOutcome.SucceededNeedsRehash)
        {
            upgraded = verification.UpgradedHash;
        }

        var result = Build(user.Subject, user.Roles, user.Tenant, source, now) with
        {
            UpgradedHash = upgraded,
        };

        return (result, verification);
    }

    /// <summary>Persists an upgraded hash. Separate from <see cref="SignIn"/> on purpose.</summary>
    public void CommitRehash(string subject, string upgradedHash, DateTimeOffset now)
    {
        var user = _directory.Find(subject);
        if (user is null) return;
        user.PasswordHash = upgradedHash;
        user.RehashedAt ??= now;
    }

    private BridgeResult Build(
        string subject, IReadOnlySet<string> roles, string tenant,
        AuthenticationSource source, DateTimeOffset now)
    {
        var (scopes, denies) = _transformer.Transform(roles);
        return new BridgeResult(
            new CanonicalPrincipal(subject, source, scopes, denies, tenant, now),
            BridgeFailure.None,
            source);
    }
}
