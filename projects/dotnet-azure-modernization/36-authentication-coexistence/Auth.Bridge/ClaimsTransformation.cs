namespace Auth.Bridge;

public interface IClaimsTransformer
{
    string Name { get; }

    (IReadOnlySet<string> Scopes, IReadOnlySet<string> DenyScopes) Transform(IReadOnlySet<string> roles);
}

/// <summary>
/// The transformation everybody writes first: a table from legacy role to the scopes that
/// role implies, unioned across the user's roles.
/// </summary>
/// <remarks>
/// <para>
/// It is correct for every rule of the form "role X may do Y", which is most of them, and it
/// reviews well because the table is legible and each row is individually defensible.
/// </para>
/// <para>
/// It is also, by construction, <b>monotone</b>: adding a role can only add scopes. The
/// legacy policy it replaces is not monotone. A monotone function cannot agree everywhere
/// with a non-monotone one, so this is not a transformation with some bugs in it -- it is a
/// transformation that provably cannot be finished. <c>Auth.Report</c> enumerates all 4096
/// decisions and shows exactly where and in which direction it fails.
/// </para>
/// </remarks>
public sealed class NaiveClaimsTransformer : IClaimsTransformer
{
    public static readonly IReadOnlyDictionary<string, string[]> Table =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [LegacyRoles.Admin] =
            [
                Resources.ReadLedger, Resources.WriteLedger, Resources.ReleasePayment,
                Resources.ManageUsers, Resources.ViewOwnInvoices,
            ],
            [LegacyRoles.Manager] = [Resources.ReadLedger, Resources.ApprovePayment],
            [LegacyRoles.Clerk] = [Resources.ReadLedger, Resources.WriteLedger, Resources.ViewOwnInvoices],
            [LegacyRoles.Auditor] = [Resources.ReadLedger, Resources.ExportAudit],
            [LegacyRoles.Vendor] = [Resources.ViewOwnInvoices, Resources.ChangeBankDetails],
            [LegacyRoles.Temp] = [Resources.ReadLedger],
        };

    public string Name => "naive";

    public (IReadOnlySet<string>, IReadOnlySet<string>) Transform(IReadOnlySet<string> roles)
    {
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            if (Table.TryGetValue(role, out var granted)) scopes.UnionWith(granted);
        }
        return (scopes, new HashSet<string>(StringComparer.Ordinal));
    }
}

/// <summary>
/// The same table, plus the clauses the first table had nowhere to put.
/// </summary>
/// <remarks>
/// <para>
/// The fix is not a longer grant table. No grant table can work: the problem is the shape of
/// the function, not the contents of the rows. What is needed is a second, negative channel
/// -- roles that <i>remove</i> a scope -- and a resolution rule where deny wins. That makes
/// the transformation non-monotone, which is exactly the property required to model a
/// non-monotone policy.
/// </para>
/// <para>
/// Each deny row below is a direct transcription of a <c>&amp;&amp; !IsInRole(...)</c> clause
/// in <see cref="LegacyAuthorization"/>. Finding them required reading the legacy code, not
/// reading the legacy role table -- the role table does not contain this information and
/// never did. That is the actual lesson: the authorization model was never in the identity
/// system, so migrating the identity system does not migrate it.
/// </para>
/// </remarks>
public sealed class DenyAwareClaimsTransformer : IClaimsTransformer
{
    public static readonly IReadOnlyDictionary<string, string[]> DenyTable =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // "(Admin || Clerk) && !Auditor"
            [LegacyRoles.Auditor] = [Resources.WriteLedger],

            // "Manager && !Temp", "Admin && !Temp"
            [LegacyRoles.Temp] = [Resources.ApprovePayment, Resources.ManageUsers],

            // "Auditor && !Vendor", "Admin && !Vendor"
            [LegacyRoles.Vendor] = [Resources.ExportAudit, Resources.ManageUsers],
        };

    private readonly NaiveClaimsTransformer _grants = new();

    public string Name => "deny-aware";

    public (IReadOnlySet<string>, IReadOnlySet<string>) Transform(IReadOnlySet<string> roles)
    {
        var (scopes, _) = _grants.Transform(roles);

        var denied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            if (DenyTable.TryGetValue(role, out var removals)) denied.UnionWith(removals);
        }

        return (scopes, denied);
    }
}

/// <summary>
/// The deny-aware table with one grant row corrected.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DenyAwareClaimsTransformer"/> fixes every divergence that the shape of the
/// transformation caused, and the residue it leaves behind is a different kind of mistake
/// altogether: <see cref="NaiveClaimsTransformer.Table"/> grants <c>ledger.read</c> to
/// <c>Temp</c>, and the legacy policy never did. Somebody writing the table assumed a
/// temporary member of staff could at least read, which is reasonable, wrong, and invisible
/// unless you check.
/// </para>
/// <para>
/// Separating the two matters. 576 of the 592 original divergences could not have been
/// fixed by any grant table, however carefully written -- they needed a new mechanism. The
/// remaining 16 are an ordinary data error that a careful reviewer could have caught. Both
/// produce the same symptom and they have nothing else in common, which is why "we reviewed
/// the mapping table" is not an answer to the first one.
/// </para>
/// </remarks>
public sealed class CorrectedClaimsTransformer : IClaimsTransformer
{
    /// <summary>The naive table with the one wrong row removed: <c>Temp</c> grants nothing.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Table =
        NaiveClaimsTransformer.Table.ToDictionary(
            row => row.Key,
            row => row.Key == LegacyRoles.Temp ? [] : row.Value,
            StringComparer.Ordinal);

    public string Name => "deny-aware, table corrected";

    public (IReadOnlySet<string>, IReadOnlySet<string>) Transform(IReadOnlySet<string> roles)
    {
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        var denies = new HashSet<string>(StringComparer.Ordinal);

        foreach (var role in roles)
        {
            if (Table.TryGetValue(role, out var granted)) scopes.UnionWith(granted);
            if (DenyAwareClaimsTransformer.DenyTable.TryGetValue(role, out var removed))
            {
                denies.UnionWith(removed);
            }
        }

        return (scopes, denies);
    }
}

/// <summary>
/// The modern policy: a scope check, plus the request conditions that were visible in the
/// legacy code and therefore survived the port.
/// </summary>
/// <remarks>
/// The context clauses are carried across faithfully because they are impossible to miss --
/// they read as <c>Request.IsLocal</c> in the middle of an <c>if</c>. The role clauses are
/// lost because they read as <c>IsInRole</c>, which looks like identity, and identity is
/// what the new system was supposed to be taking over. The migration loses precisely the
/// rules that <i>look</i> like they belong to the thing being replaced.
/// </remarks>
public static class ModernAuthorization
{
    public static bool Allows(CanonicalPrincipal principal, string resource, RequestContext context)
    {
        if (!principal.Has(resource)) return false;

        return resource switch
        {
            Resources.ReleasePayment => context.IsLocalNetwork,
            Resources.ApprovePayment => !context.IsTemporaryStaff,
            Resources.ViewOwnInvoices => context.TenantMatches,
            Resources.ChangeBankDetails => context.TenantMatches && context.IsLocalNetwork,
            _ => true,
        };
    }
}

public enum DivergenceKind
{
    /// <summary>Legacy denied, modern grants. A privilege the user did not have before.</summary>
    Escalation,

    /// <summary>Legacy granted, modern denies. A user who can no longer do their job.</summary>
    Lockout,
}

public sealed record Divergence(
    DivergenceKind Kind,
    IReadOnlySet<string> Roles,
    string Resource,
    RequestContext Context)
{
    public string RoleList => string.Join('+', Roles.OrderBy(r => r, StringComparer.Ordinal));
}

/// <summary>
/// Compares a claims transformation against the legacy policy over the entire decision
/// space. Not a sample: 64 role sets x 8 contexts x 8 resources = 4096 decisions, all of
/// them checked.
/// </summary>
/// <remarks>
/// Exhaustion is available here because the role universe is small, and it is worth taking
/// when it is available. A property-based test would find these divergences too, eventually,
/// with a probability nobody can quote in a change-approval meeting. "All 4096 decisions
/// agree" is a different kind of sentence.
/// </remarks>
public static class DivergenceAnalysis
{
    public const int TotalDecisions = 64 * 8 * 8;

    public static IReadOnlyList<Divergence> Compare(IClaimsTransformer transformer)
    {
        var divergences = new List<Divergence>();

        foreach (var roles in LegacyAuthorization.AllRoleSets())
        {
            var (scopes, denies) = transformer.Transform(roles);
            var principal = new CanonicalPrincipal(
                "u", AuthenticationSource.Oidc, scopes, denies, "t", DateTimeOffset.UnixEpoch);

            foreach (var context in LegacyAuthorization.AllContexts())
            foreach (var resource in Resources.All)
            {
                var legacy = LegacyAuthorization.Allows(roles, resource, context);
                var modern = ModernAuthorization.Allows(principal, resource, context);
                if (legacy == modern) continue;

                divergences.Add(new Divergence(
                    modern ? DivergenceKind.Escalation : DivergenceKind.Lockout,
                    roles, resource, context));
            }
        }

        return divergences;
    }
}
