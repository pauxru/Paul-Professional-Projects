using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Archaeologist.Core;

/// <summary>The estate as recovered from compiled artefacts alone.</summary>
public sealed class Estate
{
    public required IReadOnlyList<AssemblyNode> Assemblies { get; init; }
    public required IReadOnlyList<TypeNode> Types { get; init; }
    public required IReadOnlyList<MethodNode> Methods { get; init; }

    /// <summary>method id -> method id, weight = number of call sites.</summary>
    public required IReadOnlyList<Edge> MethodEdges { get; init; }

    public required IReadOnlyList<BlockerHit> Blockers { get; init; }
    public required IReadOnlyList<ReflectionSite> ReflectionSites { get; init; }

    /// <summary>Assemblies referenced from the manifest that are not part of the estate.</summary>
    public required IReadOnlyList<string> ExternalReferences { get; init; }

    private IReadOnlyDictionary<string, string>? _typeOfMethod;
    private IReadOnlyDictionary<string, string>? _asmOfType;

    public string TypeOfMethod(string methodId) =>
        (_typeOfMethod ??= Methods.ToDictionary(m => m.Id, m => m.TypeFullName, StringComparer.Ordinal))[methodId];

    public string AssemblyOfType(string typeFullName) =>
        (_asmOfType ??= Types.ToDictionary(t => t.FullName, t => t.AssemblyName, StringComparer.Ordinal))[typeFullName];

    public string AssemblyOfMethod(string methodId) => AssemblyOfType(TypeOfMethod(methodId));

    /// <summary>
    /// Method edges lifted to types. Weight stays in the same unit at every level: the
    /// number of call sites behind the edge. That is the unit a cut is actually paid in --
    /// each call site is somewhere a human has to go and change something.
    /// </summary>
    public IReadOnlyList<Edge> TypeEdges => _typeEdges ??= Lift(e =>
        (TypeOfMethod(e.From), TypeOfMethod(e.To)));

    private IReadOnlyList<Edge>? _typeEdges;

    /// <summary>Type edges lifted to assemblies. Weight is still the number of call sites.</summary>
    public IReadOnlyList<Edge> AssemblyEdges => _asmEdges ??= LiftFrom(TypeEdges, e =>
        (AssemblyOfType(e.From), AssemblyOfType(e.To)));

    private IReadOnlyList<Edge>? _asmEdges;

    private IReadOnlyList<Edge> Lift(Func<Edge, (string, string)> map) => LiftFrom(MethodEdges, map);

    private static IReadOnlyList<Edge> LiftFrom(IEnumerable<Edge> edges, Func<Edge, (string, string)> map)
    {
        var counts = new SortedDictionary<(string, string), int>(PairComparer.Instance);
        foreach (var e in edges)
        {
            var k = map(e);
            if (k.Item1 == k.Item2) continue;
            counts[k] = counts.GetValueOrDefault(k) + e.Weight;
        }
        return counts.Select(kv => new Edge(kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList();
    }

    private sealed class PairComparer : IComparer<(string, string)>
    {
        public static readonly PairComparer Instance = new();
        public int Compare((string, string) a, (string, string) b)
        {
            var c = string.CompareOrdinal(a.Item1, b.Item1);
            return c != 0 ? c : string.CompareOrdinal(a.Item2, b.Item2);
        }
    }
}

/// <summary>
/// Reads a directory of assemblies. It resolves nothing: no reference assemblies are
/// loaded, no types are resolved outside the directory, and the framework assemblies the
/// estate depends on are absent by construction.
/// </summary>
public static class IlReader
{
    public static Estate Read(string directory, IAssemblyResolver? resolver = null)
    {
        var files = Directory.GetFiles(directory, "*.dll")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();

        var modules = new List<ModuleDefinition>();
        try
        {
            foreach (var f in files)
                modules.Add(ModuleDefinition.ReadModule(f, new ReaderParameters
                {
                    ReadingMode = ReadingMode.Immediate,
                    // Never resolve. Four of the Framework assemblies this estate names do
                    // resolve on a modern machine -- to empty facades containing none of the
                    // types actually used. Resolution here does not fail loudly, it succeeds
                    // quietly and wrongly. Names in metadata are the only reliable evidence.
                    AssemblyResolver = resolver ?? new NullResolver(),
                    ReadSymbols = false,
                }));

            return Build(modules);
        }
        finally
        {
            // Cecil keeps the file mapped. An analyser that cannot be run twice in one
            // process is an analyser nobody can put in a build.
            foreach (var m in modules) m.Dispose();
        }
    }

    private static Estate Build(List<ModuleDefinition> modules)
    {        var estateAssemblies = modules.Select(m => m.Assembly.Name.Name).ToHashSet(StringComparer.Ordinal);

        var assemblies = new List<AssemblyNode>();
        var types = new List<TypeNode>();
        var methods = new List<MethodNode>();
        var edgeCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var blockers = new List<BlockerHit>();
        var reflection = new List<ReflectionSite>();
        var external = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var module in modules)
        {
            var asmName = module.Assembly.Name.Name;
            // A DLL with no matching PDB is a DLL whose source you cannot assume you have.
            var hasSymbols = File.Exists(Path.ChangeExtension(module.FileName, ".pdb"));

            var typeNames = new List<string>();
            var ownRefs = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var r in module.AssemblyReferences)
                if (!estateAssemblies.Contains(r.Name))
                {
                    external.Add(r.Name);
                    ownRefs.Add(r.Name);
                }

            foreach (var td in module.Types.Where(t => t.FullName != "<Module>")
                         .OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                typeNames.Add(td.FullName);
                var methodNames = new List<string>();

                foreach (var md in td.Methods.OrderBy(m => m.Name, StringComparer.Ordinal))
                {
                    methodNames.Add(md.Name);
                    methods.Add(new MethodNode(asmName, td.FullName, md.Name, md.Name == "Main"));
                    if (!md.HasBody) continue;

                    var instructions = md.Body.Instructions;
                    for (var i = 0; i < instructions.Count; i++)
                    {
                        var ins = instructions[i];
                        if (ins.Operand is not MethodReference callee) continue;

                        var declaring = callee.DeclaringType.FullName;
                        var scope = ScopeAssembly(callee.DeclaringType);

                        if (scope is not null && estateAssemblies.Contains(scope))
                        {
                            var key = $"{td.FullName}::{md.Name}\u0000{declaring}::{callee.Name}";
                            edgeCounts[key] = edgeCounts.GetValueOrDefault(key) + 1;
                            continue;
                        }

                        var rule = BlockerRules.Match(declaring, callee.Name);
                        if (rule is not null)
                            blockers.Add(new BlockerHit(asmName, td.FullName, md.Name, rule, ins.Offset));

                        var site = ReadReflection(instructions, i, asmName, td.FullName, md.Name, callee);
                        if (site is not null) reflection.Add(site);
                    }
                }

                types.Add(new TypeNode(asmName, td.FullName, td.IsPublic, methodNames));
            }

            assemblies.Add(new AssemblyNode(asmName, hasSymbols, typeNames, ownRefs.ToList()));
        }

        var methodEdges = edgeCounts
            .Select(kv =>
            {
                var parts = kv.Key.Split('\u0000');
                return new Edge(parts[0], parts[1], kv.Value);
            })
            .Where(e => e.From != e.To)
            .ToList();

        return new Estate
        {
            Assemblies = assemblies.OrderBy(a => a.Name, StringComparer.Ordinal).ToList(),
            Types = types.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList(),
            Methods = methods.OrderBy(m => m.Id, StringComparer.Ordinal).ToList(),
            MethodEdges = methodEdges,
            Blockers = blockers
                .OrderBy(b => b.AssemblyName, StringComparer.Ordinal)
                .ThenBy(b => b.Site, StringComparer.Ordinal).ToList(),
            ReflectionSites = reflection
                .OrderBy(r => r.AssemblyName, StringComparer.Ordinal)
                .ThenBy(r => r.Site, StringComparer.Ordinal).ToList(),
            ExternalReferences = external.ToList(),
        };
    }

    /// <summary>
    /// The whole question of decidability, in one function. Type::GetType preceded by
    /// ldstr tells you the answer; Type::GetType preceded by anything else does not, and
    /// no amount of additional analysis of this method will change that.
    /// </summary>
    private static ReflectionSite? ReadReflection(
        Mono.Collections.Generic.Collection<Instruction> ins, int i,
        string asm, string type, string method, MethodReference callee)
    {
        var declaring = callee.DeclaringType.FullName;

        if (declaring == "System.Type" && callee.Name == "GetType")
        {
            var prev = i > 0 ? ins[i - 1] : null;
            if (prev is { OpCode.Code: Code.Ldstr, Operand: string literal })
                return new ReflectionSite(asm, type, method, ReflectionKind.LiteralTypeName, literal, ins[i].Offset);
            return new ReflectionSite(asm, type, method, ReflectionKind.ComputedTypeName, null,
                ins[i].Offset, RecoverPrefix(ins, i));
        }

        if (declaring == "System.Activator" && callee.Name == "CreateInstance")
        {
            // ldtoken T; call Type::GetTypeFromHandle; call Activator::CreateInstance
            for (var back = i - 1; back >= 0 && back >= i - 3; back--)
                if (ins[back] is { OpCode.Code: Code.Ldtoken, Operand: TypeReference tr })
                    return new ReflectionSite(asm, type, method,
                        ReflectionKind.LiteralTypeToken, tr.FullName, ins[i].Offset);
            return new ReflectionSite(asm, type, method, ReflectionKind.ComputedTypeName, null, ins[i].Offset);
        }

        return null;
    }

    /// <summary>
    /// The undecidable case is not uniformly undecidable. Real code builds type names by
    /// concatenating a fixed namespace prefix onto a configured leaf, and the prefix is a
    /// string constant sitting in the IL two instructions back. Recovering it turns "any
    /// type in the process could be alive" into "any type under this namespace could be
    /// alive", which is the difference between an answer nobody can act on and one they can.
    ///
    /// This is a two-instruction pattern match, not a string solver. It recovers nothing
    /// when the prefix is itself computed, and it says so rather than guessing.
    /// </summary>
    private static string? RecoverPrefix(Mono.Collections.Generic.Collection<Instruction> ins, int i)
    {
        if (i < 3) return null;
        if (ins[i - 1] is not { Operand: MethodReference concat }) return null;
        if (concat.DeclaringType.FullName != "System.String" || concat.Name != "Concat") return null;
        // Concat(a, b): a is loaded first, so it is the earlier of the two pushes.
        var first = ins[i - 1 - concat.Parameters.Count];
        return first is { OpCode.Code: Code.Ldstr, Operand: string prefix } ? prefix : null;
    }

    private static string? ScopeAssembly(TypeReference tr) => tr.Scope switch    {
        AssemblyNameReference a => a.Name,
        ModuleDefinition m => m.Assembly.Name.Name,
        ModuleReference => null,
        _ => tr.Module?.Assembly?.Name.Name,
    };

    private sealed class NullResolver : IAssemblyResolver
    {
        public void Dispose() { }
        public AssemblyDefinition Resolve(AssemblyNameReference name) =>
            throw new AssemblyResolutionException(name);
        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters) =>
            throw new AssemblyResolutionException(name);
    }
}
