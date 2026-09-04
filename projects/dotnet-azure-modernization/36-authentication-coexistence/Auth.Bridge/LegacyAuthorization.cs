namespace Auth.Bridge;

/// <summary>Which stack authenticated this request.</summary>
public enum AuthenticationSource
{
    FormsTicket,
    WsFederation,
    Oidc,
}

/// <summary>
/// The single identity type the application is allowed to see.
/// </summary>
/// <remarks>
/// The whole point of the coexistence design is that this type exists and the application
/// code never learns which of the three stacks produced it. If the application branches on
/// <see cref="Source"/>, the migration can never finish, because switching a stack off
/// becomes a change to business logic rather than a change to configuration. The field is
/// here for the audit log and for the report -- not for authorization.
/// </remarks>
public sealed record CanonicalPrincipal(
    string Subject,
    AuthenticationSource Source,
    IReadOnlySet<string> Scopes,
    IReadOnlySet<string> DenyScopes,
    string Tenant,
    DateTimeOffset AuthenticatedAt)
{
    /// <summary>
    /// Deny wins. The ordering is not a preference; it is the only ordering under which the
    /// transformation can represent a legacy rule of the form "X, unless also Y".
    /// </summary>
    public bool Has(string scope) => !DenyScopes.Contains(scope) && Scopes.Contains(scope);
}

/// <summary>The request facts the legacy code consulted alongside roles.</summary>
public readonly record struct RequestContext(bool IsLocalNetwork, bool TenantMatches, bool IsTemporaryStaff);

public static class Resources
{
    public const string ReadLedger = "ledger.read";
    public const string WriteLedger = "ledger.write";
    public const string ApprovePayment = "payment.approve";
    public const string ReleasePayment = "payment.release";
    public const string ExportAudit = "audit.export";
    public const string ManageUsers = "users.manage";
    public const string ViewOwnInvoices = "invoices.own.read";
    public const string ChangeBankDetails = "vendor.bank.write";

    public static readonly string[] All =
    [
        ReadLedger, WriteLedger, ApprovePayment, ReleasePayment,
        ExportAudit, ManageUsers, ViewOwnInvoices, ChangeBankDetails,
    ];
}

public static class LegacyRoles
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string Clerk = "Clerk";
    public const string Auditor = "Auditor";
    public const string Vendor = "Vendor";
    public const string Temp = "Temp";

    /// <summary>Six roles, so the report can enumerate all 64 role sets exhaustively.</summary>
    public static readonly string[] All = [Admin, Manager, Clerk, Auditor, Vendor, Temp];
}

/// <summary>
/// The authorization rules as the legacy application actually implements them: scattered
/// imperative checks, reconstructed here in one place.
/// </summary>
/// <remarks>
/// <para>
/// Every clause below corresponds to a real pattern found in ASP.NET applications of this
/// vintage. The important ones are the negative clauses -- <c>IsInRole("Manager") &amp;&amp;
/// !IsInRole("Temp")</c> and friends. They exist because somebody needed to carve an
/// exception out of an existing role rather than create a new one, which is always the
/// cheaper change at the time.
/// </para>
/// <para>
/// Those clauses make this function <b>non-monotone</b> in the role set: there exist role
/// sets S subset of T where <c>Allows(S) = true</c> and <c>Allows(T) = false</c>. Adding a
/// role can take permissions away. That single fact is what the naive claims migration
/// cannot survive, and <c>Auth.Report</c> proves it by exhaustion rather than by argument.
/// </para>
/// </remarks>
public static class LegacyAuthorization
{
    public static bool Allows(IReadOnlySet<string> roles, string resource, RequestContext context)
    {
        bool In(string role) => roles.Contains(role);

        return resource switch
        {
            Resources.ReadLedger =>
                In(LegacyRoles.Admin) || In(LegacyRoles.Manager) ||
                In(LegacyRoles.Clerk) || In(LegacyRoles.Auditor),

            Resources.WriteLedger =>
                (In(LegacyRoles.Admin) || In(LegacyRoles.Clerk)) && !In(LegacyRoles.Auditor),

            // The segregation-of-duties rule. An auditor who also holds a writing role is
            // not an auditor for this purpose.
            Resources.ApprovePayment =>
                In(LegacyRoles.Manager) && !In(LegacyRoles.Temp) && !context.IsTemporaryStaff,

            // The "only from the office" rule, added after an incident and never revisited.
            Resources.ReleasePayment =>
                In(LegacyRoles.Admin) && context.IsLocalNetwork,

            Resources.ExportAudit =>
                In(LegacyRoles.Auditor) && !In(LegacyRoles.Vendor),

            Resources.ManageUsers =>
                In(LegacyRoles.Admin) && !In(LegacyRoles.Vendor) && !In(LegacyRoles.Temp),

            Resources.ViewOwnInvoices =>
                (In(LegacyRoles.Vendor) || In(LegacyRoles.Clerk) || In(LegacyRoles.Admin)) &&
                context.TenantMatches,

            Resources.ChangeBankDetails =>
                In(LegacyRoles.Vendor) && context.TenantMatches && context.IsLocalNetwork,

            _ => false,
        };
    }

    /// <summary>
    /// Finds a concrete counterexample to monotonicity: a role set, a strictly larger role
    /// set, and a resource where growing the set loses the permission.
    /// </summary>
    public static IEnumerable<(IReadOnlySet<string> Smaller, IReadOnlySet<string> Larger, string Resource)>
        MonotonicityCounterexamples(RequestContext context)
    {
        foreach (var smaller in AllRoleSets())
        {
            foreach (var extra in LegacyRoles.All)
            {
                if (smaller.Contains(extra)) continue;
                var larger = new HashSet<string>(smaller, StringComparer.Ordinal) { extra };
                foreach (var resource in Resources.All)
                {
                    if (Allows(smaller, resource, context) && !Allows(larger, resource, context))
                    {
                        yield return (smaller, larger, resource);
                    }
                }
            }
        }
    }

    /// <summary>All 2^6 subsets of the legacy role universe.</summary>
    public static IEnumerable<IReadOnlySet<string>> AllRoleSets()
    {
        for (var mask = 0; mask < 1 << LegacyRoles.All.Length; mask++)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (var bit = 0; bit < LegacyRoles.All.Length; bit++)
            {
                if ((mask & (1 << bit)) != 0) set.Add(LegacyRoles.All[bit]);
            }
            yield return set;
        }
    }

    public static IEnumerable<RequestContext> AllContexts()
    {
        foreach (var local in new[] { false, true })
        foreach (var tenant in new[] { false, true })
        foreach (var temp in new[] { false, true })
        {
            yield return new RequestContext(local, tenant, temp);
        }
    }
}
