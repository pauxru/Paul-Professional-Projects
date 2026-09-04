namespace Archaeologist.Core;

/// <summary>An instruction in a synthesised method body.</summary>
public abstract record Op;

/// <summary>call a static method on another type in the estate.</summary>
public sealed record CallCorpus(string TypeFullName, string Method) : Op;

/// <summary>newobj on another type in the estate.</summary>
public sealed record NewCorpus(string TypeFullName) : Op;

/// <summary>call a static method on a type outside the estate (the framework).</summary>
public sealed record CallFramework(string Assembly, string Namespace, string Type, string Member) : Op;

/// <summary>ldstr "Some.Type"; call Type::GetType(string) -- the name is in the IL.</summary>
public sealed record GetTypeLiteral(string TypeName) : Op;

/// <summary>The type name arrives from configuration. Nothing in the IL says what it is.</summary>
public sealed record GetTypeComputed(string ConfigKey) : Op;

/// <summary>ldtoken T; Activator::CreateInstance(Type) -- the token is in the IL.</summary>
public sealed record ActivatorTypeOf(string TypeFullName) : Op;

public sealed record MethodSpec(string Name, params Op[] Body);

public sealed record TypeSpec(string FullName, params MethodSpec[] Methods);

public sealed record AssemblySpec(string Name, bool SourceAvailable, params TypeSpec[] Types);

/// <summary>
/// The estate as declared, plus what its author planted in it. Tests attack these
/// claims from the outside: the corpus does not get to mark its own homework.
/// </summary>
public sealed class CorpusSpec
{
    public required IReadOnlyList<AssemblySpec> Assemblies { get; init; }

    /// <summary>The single static entry point. Everything reachable from here is live.</summary>
    public required string EntryPoint { get; init; }

    /// <summary>Assemblies the author believes no static path reaches.</summary>
    public required IReadOnlyList<string> PlantedStaticallyUnreachableAssemblies { get; init; }

    /// <summary>Of those, the ones a reflection-aware analysis can prove are live.</summary>
    public required IReadOnlyList<string> PlantedReflectionLiveAssemblies { get; init; }

    /// <summary>Of those, the ones nothing can decide -- so they can never be deleted.</summary>
    public required IReadOnlyList<string> PlantedUndecidableAssemblies { get; init; }

    /// <summary>
    /// The only genuine cycles the author planted: sets of types that mutually reach each
    /// other. Every other cycle in the estate is a consequence of how types were packaged
    /// into assemblies, and the analyser is expected to show that.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<string>> PlantedTypeLevelKnots { get; init; }

    /// <summary>
    /// Assembly-level cycles that contain no type-level cycle at all -- pure packaging
    /// artefacts, fixable by moving a type rather than by breaking a dependency.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<string>> PlantedPackagingOnlyCycles { get; init; }

    public IEnumerable<TypeSpec> AllTypes => Assemblies.SelectMany(a => a.Types);

    public AssemblySpec AssemblyOf(string typeFullName) =>
        Assemblies.First(a => a.Types.Any(t => t.FullName == typeFullName));

    public static CorpusSpec Contoso() => Build();

    // ---- the estate -------------------------------------------------------
    //
    // A claims platform begun in 2004. Three acquisitions, two failed rewrites,
    // one departed architect. Names are invented; the shapes are not.

    private const string Mscorlib = "mscorlib";
    private const string SysWeb = "System.Web";
    private const string SysSvc = "System.ServiceModel";
    private const string SysCfg = "System.Configuration";
    private const string SysDraw = "System.Drawing";
    private const string SysData = "System.Data";
    private const string SysEnt = "System.EnterpriseServices";
    private const string SysMsg = "System.Messaging";

    private static Op Http(string member) => new CallFramework(SysWeb, "System.Web", "HttpContext", member);

    private static CorpusSpec Build()
    {
        var asms = new List<AssemblySpec>
        {
            // -- foundation ---------------------------------------------------
            new("Contoso.Claims.Core", true,
                new TypeSpec("Contoso.Claims.Core.Claim",
                    new MethodSpec("Validate"),
                    new MethodSpec("Total")),
                new TypeSpec("Contoso.Claims.Core.Policy",
                    new MethodSpec("IsActive"),
                    new MethodSpec("CoverageFor")),
                new TypeSpec("Contoso.Claims.Core.Money",
                    new MethodSpec("Add"),
                    new MethodSpec("Round")),
                // Dead inside a live assembly: nothing calls it, nothing names it.
                new TypeSpec("Contoso.Claims.Core.LegacyCurrencyTable",
                    new MethodSpec("Lookup"))),

            new("Contoso.Common.Config", true,
                new TypeSpec("Contoso.Common.Config.ConfigStore",
                    new MethodSpec("Read",
                        new CallFramework(SysCfg, "System.Configuration", "ConfigurationManager", "get_AppSettings")),
                    // half of the irreducible cycle: ConfigStore -> PrincipalCache
                    new MethodSpec("ReadForUser",
                        new CallCorpus("Contoso.Claims.Security.PrincipalCache", "Current")))),

            new("Contoso.Common.Logging", false,
                new TypeSpec("Contoso.Common.Logging.Log",
                    new MethodSpec("Write",
                        new CallCorpus("Contoso.Common.Config.ConfigStore", "Read")),
                    new MethodSpec("Flush"))),

            new("Contoso.Common.Utils", false,
                new TypeSpec("Contoso.Common.Utils.Cloner",
                    new MethodSpec("DeepCopy",
                        new CallFramework(Mscorlib, "System.Runtime.Serialization.Formatters.Binary",
                            "BinaryFormatter", "Serialize"),
                        new CallFramework(Mscorlib, "System.Runtime.Serialization.Formatters.Binary",
                            "BinaryFormatter", "Deserialize"))),
                new TypeSpec("Contoso.Common.Utils.Strings",
                    new MethodSpec("Normalise"))),

            // -- data + audit: the cycle that dissolves ------------------------
            new("Contoso.Claims.Data", true,
                new TypeSpec("Contoso.Claims.Data.Repository",
                    new MethodSpec("Load",
                        new CallCorpus("Contoso.Claims.Data.ConnectionFactory", "Open"),
                        new CallCorpus("Contoso.Claims.Core.Claim", "Validate"),
                        // the retry path opens a second connection. Two call sites, one
                        // edge: weight is what tells a cut what it would actually cost.
                        new CallCorpus("Contoso.Claims.Data.ConnectionFactory", "Open"),
                        new CallCorpus("Contoso.Claims.Audit.AuditWriter", "Record")),
                    new MethodSpec("Save",
                        new CallCorpus("Contoso.Claims.Data.ConnectionFactory", "Open"),
                        new CallCorpus("Contoso.Claims.Audit.AuditWriter", "Record"))),
                new TypeSpec("Contoso.Claims.Data.ConnectionFactory",
                    new MethodSpec("Open",
                        new CallFramework(SysData, "System.Data.SqlClient", "SqlConnection", ".ctor"),
                        new CallCorpus("Contoso.Common.Config.ConfigStore", "Read")))),

            new("Contoso.Claims.Audit", true,
                new TypeSpec("Contoso.Claims.Audit.AuditWriter",
                    new MethodSpec("Record",
                        new CallCorpus("Contoso.Claims.Audit.AuditContext", "Describe"))),
                new TypeSpec("Contoso.Claims.Audit.AuditContext",
                    // the return edge: Audit -> Data, but from a different type
                    new MethodSpec("Describe",
                        new CallCorpus("Contoso.Claims.Data.ConnectionFactory", "Open"),
                        new CallCorpus("Contoso.Common.Logging.Log", "Write")))),

            // -- security + config: the cycle that does not dissolve -----------
            new("Contoso.Claims.Security", true,
                new TypeSpec("Contoso.Claims.Security.PrincipalCache",
                    new MethodSpec("Current",
                        new CallCorpus("Contoso.Common.Config.ConfigStore", "Read"),
                        new CallCorpus("Contoso.Common.Config.ConfigStore", "ReadForUser"))),
                new TypeSpec("Contoso.Claims.Security.FormsLogin",
                    new MethodSpec("SignIn",
                        new CallFramework(SysWeb, "System.Web.Security", "FormsAuthentication", "SetAuthCookie"),
                        new CallCorpus("Contoso.Claims.Security.PrincipalCache", "Current"),
                        new CallCorpus("Contoso.Claims.Data.Repository", "Load")))),

            // -- business ------------------------------------------------------
            new("Contoso.Claims.Rules", true,
                new TypeSpec("Contoso.Claims.Rules.RuleEngine",
                    new MethodSpec("Evaluate",
                        new CallCorpus("Contoso.Claims.Core.Policy", "CoverageFor"),
                        new CallCorpus("Contoso.Claims.Rules.RuleSet", "Load")),
                    new MethodSpec("Explain",
                        new CallCorpus("Contoso.Claims.Workflow.WorkflowContext", "Describe"))),
                new TypeSpec("Contoso.Claims.Rules.RuleSet",
                    new MethodSpec("Load",
                        new CallCorpus("Contoso.Claims.Data.Repository", "Load")))),

            new("Contoso.Claims.Workflow", true,
                new TypeSpec("Contoso.Claims.Workflow.ClaimWorkflow",
                    new MethodSpec("Advance",
                        new CallCorpus("Contoso.Claims.Rules.RuleEngine", "Evaluate"),
                        new CallCorpus("Contoso.Claims.Data.Repository", "Save"),
                        new CallCorpus("Contoso.Claims.Notifications.Notifier", "Send")),
                    new MethodSpec("Start",
                        new CallCorpus("Contoso.Claims.Data.Repository", "Load"),
                        new CallCorpus("Contoso.Claims.Pricing.Pricer", "Quote"))),
                new TypeSpec("Contoso.Claims.Workflow.WorkflowContext",
                    new MethodSpec("Describe",
                        new CallCorpus("Contoso.Common.Logging.Log", "Write")))),

            new("Contoso.Claims.Pricing", true,
                new TypeSpec("Contoso.Claims.Pricing.Pricer",
                    new MethodSpec("Quote",
                        new CallCorpus("Contoso.Claims.Core.Money", "Add"),
                        new CallCorpus("Contoso.Claims.Integration.Mainframe.Cics", "Invoke"))),
                new TypeSpec("Contoso.Claims.Pricing.RateTable",
                    new MethodSpec("Lookup",
                        new CallCorpus("Contoso.Claims.Data.Repository", "Load")))),

            // -- documents / notifications / reporting: a 3-cycle --------------
            new("Contoso.Claims.Documents", true,
                new TypeSpec("Contoso.Claims.Documents.Renderer",
                    new MethodSpec("ToPdf",
                        new CallFramework(SysDraw, "System.Drawing", "Bitmap", ".ctor"),
                        new CallCorpus("Contoso.Claims.Documents.TemplateStore", "Get"))),
                new TypeSpec("Contoso.Claims.Documents.TemplateStore",
                    new MethodSpec("Get",
                        new CallCorpus("Contoso.Common.Config.ConfigStore", "Read"))),
                new TypeSpec("Contoso.Claims.Documents.Archive",
                    new MethodSpec("Store",
                        new CallCorpus("Contoso.Claims.Notifications.Templates", "Resolve")))),

            new("Contoso.Claims.Notifications", true,
                new TypeSpec("Contoso.Claims.Notifications.Notifier",
                    new MethodSpec("Send",
                        new CallCorpus("Contoso.Claims.Documents.Renderer", "ToPdf"),
                        new CallFramework(SysMsg, "System.Messaging", "MessageQueue", "Send"))),
                new TypeSpec("Contoso.Claims.Notifications.Templates",
                    new MethodSpec("Resolve",
                        new CallCorpus("Contoso.Claims.Reporting.ReportBuilder", "Build")))),

            new("Contoso.Claims.Reporting", true,
                new TypeSpec("Contoso.Claims.Reporting.ReportBuilder",
                    new MethodSpec("Build",
                        new CallCorpus("Contoso.Claims.Data.Repository", "Load"),
                        new CallCorpus("Contoso.Claims.Documents.Archive", "Store"),
                        new CallCorpus("Contoso.Claims.Reporting.ChartWriter", "Draw"))),
                new TypeSpec("Contoso.Claims.Reporting.ChartWriter",
                    new MethodSpec("Draw",
                        new CallFramework(SysDraw, "System.Drawing", "Bitmap", "Save"),
                        new ActivatorTypeOf("Contoso.Claims.Core.LegacyCurrencyTable"),
                        new CallCorpus("Contoso.Claims.Documents.TemplateStore", "Get")))),

            // -- edges of the system -------------------------------------------
            new("Contoso.Claims.Web", true,
                new TypeSpec("Contoso.Claims.Web.ClaimPage",
                    new MethodSpec("Page_Load",
                        Http("get_Current"),
                        new CallFramework(SysWeb, "System.Web.UI", "Page", "get_Session"),
                        new CallCorpus("Contoso.Claims.Workflow.ClaimWorkflow", "Advance"),
                        new CallCorpus("Contoso.Claims.Security.FormsLogin", "SignIn")),
                    new MethodSpec("Render",
                        new CallFramework(SysWeb, "System.Web.Caching", "Cache", "Insert"),
                        new CallCorpus("Contoso.Claims.Reporting.ReportBuilder", "Build"))),
                new TypeSpec("Contoso.Claims.Web.PluginLoader",
                    // decidable reflection: the name is a literal in the IL
                    new MethodSpec("LoadFraud",
                        new GetTypeLiteral("Contoso.Claims.Plugins.Fraud.FraudScorer"),
                        new CallCorpus("Contoso.Claims.Plugins.Abstractions.IPlugin", "Describe")),
                    // undecidable reflection: the name arrives from config
                    new MethodSpec("LoadConfigured",
                        new GetTypeComputed("plugins.enabled")))),

            new("Contoso.Claims.WebServices", true,
                new TypeSpec("Contoso.Claims.WebServices.ClaimService",
                    new MethodSpec("Open",
                        new CallFramework(SysSvc, "System.ServiceModel", "ServiceHost", ".ctor"),
                        new CallCorpus("Contoso.Claims.Workflow.ClaimWorkflow", "Start")),
                    new MethodSpec("Submit",
                        new CallCorpus("Contoso.Claims.Workflow.ClaimWorkflow", "Advance")))),

            new("Contoso.Claims.Batch", true,
                new TypeSpec("Contoso.Claims.Batch.NightlyRun",
                    new MethodSpec("Execute",
                        new CallFramework(Mscorlib, "System", "AppDomain", "CreateDomain"),
                        new CallFramework(Mscorlib, "System.Runtime.Remoting", "RemotingConfiguration", "Configure"),
                        new CallCorpus("Contoso.Claims.Reporting.ReportBuilder", "Build"),
                        new CallCorpus("Contoso.Claims.Workflow.ClaimWorkflow", "Advance"))),
                new TypeSpec("Contoso.Claims.Batch.Sweeper",
                    new MethodSpec("Sweep",
                        new CallCorpus("Contoso.Claims.Scheduler.JobRunner", "Run")))),

            new("Contoso.Claims.Scheduler", true,
                new TypeSpec("Contoso.Claims.Scheduler.JobRunner",
                    new MethodSpec("Run",
                        new CallFramework(Mscorlib, "System.Threading", "Thread", "Abort"),
                        new CallCorpus("Contoso.Claims.Notifications.Templates", "Resolve")))),

            new("Contoso.Claims.Integration.Guidewire", true,
                new TypeSpec("Contoso.Claims.Integration.Guidewire.GwClient",
                    new MethodSpec("Push",
                        new CallFramework(SysEnt, "System.EnterpriseServices", "ServicedComponent", "get_ContextUtil"),
                        new CallCorpus("Contoso.Claims.Data.Repository", "Save")))),

            // Source lost in the 2011 SAN failure. Still in production.
            new("Contoso.Claims.Integration.Mainframe", false,
                new TypeSpec("Contoso.Claims.Integration.Mainframe.Cics",
                    new MethodSpec("Invoke",
                        new CallFramework(SysMsg, "System.Messaging", "MessageQueue", "Receive"),
                        new CallCorpus("Contoso.Common.Utils.Cloner", "DeepCopy"))),
                new TypeSpec("Contoso.Claims.Integration.Mainframe.Copybook",
                    new MethodSpec("Parse",
                        new CallCorpus("Contoso.Common.Utils.Strings", "Normalise")))),

            // -- plugins: reachable only by reflection --------------------------
            new("Contoso.Claims.Plugins.Abstractions", true,
                new TypeSpec("Contoso.Claims.Plugins.Abstractions.IPlugin",
                    new MethodSpec("Describe"))),

            new("Contoso.Claims.Plugins.Fraud", true,
                new TypeSpec("Contoso.Claims.Plugins.Fraud.FraudScorer",
                    new MethodSpec("Score",
                        new CallCorpus("Contoso.Claims.Data.Repository", "Load"),
                        new CallCorpus("Contoso.Claims.Plugins.Abstractions.IPlugin", "Describe")))),

            new("Contoso.Claims.Plugins.Subrogation", true,
                new TypeSpec("Contoso.Claims.Plugins.Subrogation.SubroScorer",
                    new MethodSpec("Score",
                        new CallCorpus("Contoso.Claims.Core.Claim", "Total"),
                        new CallCorpus("Contoso.Claims.Plugins.Abstractions.IPlugin", "Describe")))),

            // -- the 2014 migration attempt, abandoned --------------------------
            new("Contoso.Claims.Migration.Tools", true,
                new TypeSpec("Contoso.Claims.Migration.Tools.SchemaDiff",
                    new MethodSpec("Compare",
                        new CallCorpus("Contoso.Claims.Data.Repository", "Load"))),
                new TypeSpec("Contoso.Claims.Migration.Tools.Importer",
                    new MethodSpec("Import",
                        new CallCorpus("Contoso.Claims.Migration.Tools.SchemaDiff", "Compare")))),

            // -- the host --------------------------------------------------------
            new("Contoso.Claims.Host", true,
                new TypeSpec("Contoso.Claims.Host.Program",
                    new MethodSpec("Main",
                        new CallCorpus("Contoso.Claims.Web.ClaimPage", "Page_Load"),
                        new CallCorpus("Contoso.Claims.Web.PluginLoader", "LoadFraud"),
                        new CallCorpus("Contoso.Claims.Web.PluginLoader", "LoadConfigured"),
                        new CallCorpus("Contoso.Claims.WebServices.ClaimService", "Open"),
                        new CallCorpus("Contoso.Claims.Batch.NightlyRun", "Execute"),
                        new CallCorpus("Contoso.Claims.Batch.Sweeper", "Sweep"),
                        new CallCorpus("Contoso.Claims.Integration.Guidewire.GwClient", "Push"),
                        new CallCorpus("Contoso.Claims.Pricing.RateTable", "Lookup"),
                        new CallCorpus("Contoso.Claims.Integration.Mainframe.Copybook", "Parse")))),
        };

        return new CorpusSpec
        {
            Assemblies = asms,
            EntryPoint = "Contoso.Claims.Host.Program::Main",
            PlantedStaticallyUnreachableAssemblies =
            [
                "Contoso.Claims.Migration.Tools",
                "Contoso.Claims.Plugins.Fraud",
                "Contoso.Claims.Plugins.Subrogation",
            ],
            PlantedReflectionLiveAssemblies = ["Contoso.Claims.Plugins.Fraud"],
            PlantedUndecidableAssemblies = ["Contoso.Claims.Plugins.Subrogation"],
            PlantedTypeLevelKnots =
            [
                [
                    "Contoso.Claims.Security.PrincipalCache",
                    "Contoso.Common.Config.ConfigStore",
                ],
                [
                    "Contoso.Claims.Documents.Archive",
                    "Contoso.Claims.Notifications.Templates",
                    "Contoso.Claims.Reporting.ReportBuilder",
                ],
            ],
            PlantedPackagingOnlyCycles =
            [
                ["Contoso.Claims.Rules", "Contoso.Claims.Workflow"],
            ],
        };
    }
}
