namespace Archaeologist.Core;

/// <summary>
/// What stops an assembly running on modern .NET. The table is keyed by the only thing
/// IL actually preserves about a call into an assembly you do not have: the declaring
/// type's full name, and the member name.
///
/// The table is deliberately written without reference to the corpus. If it only found
/// what the corpus planted it would be measuring nothing.
/// </summary>
public static class BlockerRules
{
    public static readonly IReadOnlyList<BlockerRule> All =
    [
        new("System.Web", "HttpContext", null, Severity.NoEquivalent,
            "Ambient per-request state with no equivalent in the modern hosting model.",
            "Flow state explicitly, or IHttpContextAccessor at the edge only."),
        new("System.Web.UI", "Page", null, Severity.NoEquivalent,
            "Web Forms was never ported and will not be.",
            "Rewrite the surface: Razor Pages, MVC, or an API plus a separate client."),
        new("System.Web.Security", "FormsAuthentication", null, Severity.NoEquivalent,
            "Forms authentication tickets are a Framework-only cookie format.",
            "Terminate on cookie auth or OIDC; bridge tickets during transition."),
        new("System.Web.Caching", "Cache", null, Severity.Rewrite,
            "Process-local cache tied to the ASP.NET runtime.",
            "IMemoryCache, or a distributed cache if the process count is changing."),
        new("System.ServiceModel", "ServiceHost", null, Severity.NoEquivalent,
            "WCF server-side hosting has no modern .NET implementation.",
            "Expose the same contracts over gRPC or HTTP; CoreWCF if the wire format is fixed."),
        new("System.Runtime.Remoting", "RemotingConfiguration", null, Severity.NoEquivalent,
            ".NET Remoting was removed outright.",
            "Replace with an explicit RPC boundary. This is a design change, not a port."),
        new("System", "AppDomain", "CreateDomain", Severity.PlatformNotSupported,
            "Present in the surface area, throws at runtime. The compiler will not warn you.",
            "AssemblyLoadContext for isolation, or a separate process for real isolation."),
        new("System.Runtime.Serialization.Formatters.Binary", "BinaryFormatter", null, Severity.NoEquivalent,
            "Removed for unfixable deserialisation vulnerabilities.",
            "An explicit contract-based serialiser. Persisted payloads must be migrated."),
        new("System.Threading", "Thread", "Abort", Severity.PlatformNotSupported,
            "Present in the surface area, throws at runtime.",
            "Cooperative cancellation. There is no drop-in replacement."),
        new("System.Configuration", "ConfigurationManager", null, Severity.Rewrite,
            "web.config/app.config is not the modern configuration model.",
            "IConfiguration with the same keys, so call sites change shape but not meaning."),
        new("System.Drawing", "Bitmap", null, Severity.Rewrite,
            "System.Drawing.Common is Windows-only from .NET 7 onward.",
            "ImageSharp or SkiaSharp, or keep this component on Windows deliberately."),
        new("System.Drawing", "Bitmap", "GetHbitmap", Severity.NoEquivalent,
            "Hands out a Win32 HBITMAP. No cross-platform imaging library has this concept, "
            + "so the caller is not using an image library, it is using Windows.",
            "Follow the handle to whatever consumes it; that call site is the real blocker."),
        new("System.Data.SqlClient", "SqlConnection", null, Severity.Rewrite,
            "The type moved to Microsoft.Data.SqlClient with changed defaults (Encrypt=true).",
            "Swap the package; re-test connection strings, not just compilation."),
        new("System.EnterpriseServices", "ServicedComponent", null, Severity.NoEquivalent,
            "COM+ hosting does not exist on modern .NET.",
            "Extract the transaction boundary explicitly. Usually the hardest item on the list."),
        new("System.Messaging", "MessageQueue", null, Severity.NoEquivalent,
            "MSMQ has no modern .NET client.",
            "A broker with an equivalent delivery guarantee -- and prove the guarantee."),
    ];

    private static readonly IReadOnlyDictionary<string, List<BlockerRule>> ByType = Build();

    private static IReadOnlyDictionary<string, List<BlockerRule>> Build()
    {
        var d = new SortedDictionary<string, List<BlockerRule>>(StringComparer.Ordinal);
        foreach (var r in All)
        {
            var key = $"{r.Namespace}.{r.TypeName}";
            if (!d.TryGetValue(key, out var list)) d[key] = list = [];
            list.Add(r);
        }
        return d;
    }

    /// <summary>
    /// Member-specific rules win over type-wide rules, so AppDomain::CreateDomain can be
    /// a blocker while AppDomain::get_CurrentDomain is not.
    /// </summary>
    public static BlockerRule? Match(string declaringTypeFullName, string memberName)
    {
        if (!ByType.TryGetValue(declaringTypeFullName, out var candidates)) return null;
        var specific = candidates.FirstOrDefault(r => r.MemberName == memberName);
        if (specific is not null) return specific;
        return candidates.FirstOrDefault(r => r.MemberName is null);
    }
}
