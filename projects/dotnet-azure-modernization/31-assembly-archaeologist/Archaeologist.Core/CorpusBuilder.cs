using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Archaeologist.Core;

/// <summary>
/// Emits the estate as real .NET Framework-era assemblies using Cecil.
///
/// Nothing here is compiled from C#. The assemblies reference System.Web 4.0.0.0,
/// System.ServiceModel, System.EnterpriseServices and friends -- none of which are
/// installed on the machine that writes them, and none of which need to be. That is the
/// point: the analyser reads metadata and IL, and metadata does not require the thing it
/// names to exist. It is the same reason a real archaeologist can read a manifest for a
/// GAC assembly that was uninstalled in 2016.
/// </summary>
public static class CorpusBuilder
{
    private static readonly Version Fx4 = new(4, 0, 0, 0);

    /// <summary>Public key tokens of the real Framework assemblies, for realism in the manifest.</summary>
    private static readonly IReadOnlyDictionary<string, byte[]> Tokens =
        new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["mscorlib"] = [0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89],
            ["System.Web"] = [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a],
            ["System.ServiceModel"] = [0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89],
            ["System.Configuration"] = [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a],
            ["System.Drawing"] = [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a],
            ["System.Data"] = [0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89],
            ["System.EnterpriseServices"] = [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a],
            ["System.Messaging"] = [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a],
        };

    /// <summary>Writes every assembly in the spec to <paramref name="outputDir"/>.</summary>
    public static IReadOnlyList<string> Emit(CorpusSpec spec, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        // Pass 1: every assembly, type and method must exist as a definition before any
        // body is written, because the estate contains cycles and a cycle cannot be
        // emitted in dependency order.
        var modules = new SortedDictionary<string, ModuleDefinition>(StringComparer.Ordinal);
        var assemblies = new SortedDictionary<string, AssemblyDefinition>(StringComparer.Ordinal);
        var types = new SortedDictionary<string, TypeDefinition>(StringComparer.Ordinal);
        var methods = new SortedDictionary<string, MethodDefinition>(StringComparer.Ordinal);
        var ctors = new SortedDictionary<string, MethodDefinition>(StringComparer.Ordinal);

        foreach (var a in spec.Assemblies)
        {
            var asm = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition(a.Name, Fx4), a.Name, ModuleKind.Dll);
            assemblies[a.Name] = asm;
            modules[a.Name] = asm.MainModule;

            foreach (var t in a.Types)
            {
                var (ns, name) = Split(t.FullName);
                var td = new TypeDefinition(ns, name,
                    TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
                    asm.MainModule.TypeSystem.Object);
                asm.MainModule.Types.Add(td);
                types[t.FullName] = td;

                var ctor = new MethodDefinition(".ctor",
                    MethodAttributes.Public | MethodAttributes.HideBySig |
                    MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                    asm.MainModule.TypeSystem.Void);
                var cil = ctor.Body.GetILProcessor();
                cil.Emit(OpCodes.Ldarg_0);
                cil.Emit(OpCodes.Call, ObjectCtor(asm.MainModule));
                cil.Emit(OpCodes.Ret);
                td.Methods.Add(ctor);
                ctors[t.FullName] = ctor;

                foreach (var m in t.Methods)
                {
                    var md = new MethodDefinition(m.Name,
                        MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                        asm.MainModule.TypeSystem.Void);
                    td.Methods.Add(md);
                    methods[$"{t.FullName}::{m.Name}"] = md;
                }
            }
        }

        // Pass 2: bodies. Cross-assembly references resolve against the in-memory
        // definitions, so a cycle is no harder to emit than a chain.
        foreach (var a in spec.Assemblies)
        {
            var module = modules[a.Name];
            var externals = new ExternalRefs(module);

            foreach (var t in a.Types)
            foreach (var m in t.Methods)
            {
                var md = methods[$"{t.FullName}::{m.Name}"];
                var il = md.Body.GetILProcessor();
                foreach (var op in m.Body) Emit(il, module, externals, op, types, methods, ctors);
                il.Emit(OpCodes.Ret);
            }
        }

        var written = new List<string>();
        foreach (var (name, asm) in assemblies)
        {
            var path = Path.Combine(outputDir, name + ".dll");
            var sourceAvailable = spec.Assemblies.First(a => a.Name == name).SourceAvailable;

            // Symbols are the only durable trace of "we still have the source". Three of
            // these assemblies ship without a PDB, which is how the estate presents in
            // reality: the DLL survived the SAN failure, the source did not.
            asm.Write(path, new WriterParameters
            {
                WriteSymbols = sourceAvailable,
                SymbolWriterProvider = sourceAvailable ? new PortablePdbWriterProvider() : null,
            });
            if (!sourceAvailable) File.Delete(Path.ChangeExtension(path, ".pdb"));
            written.Add(path);
        }
        return written;
    }

    private static void Emit(
        ILProcessor il,
        ModuleDefinition module,
        ExternalRefs ext,
        Op op,
        IDictionary<string, TypeDefinition> types,
        IDictionary<string, MethodDefinition> methods,
        IDictionary<string, MethodDefinition> ctors)
    {
        switch (op)
        {
            case CallCorpus c:
                il.Emit(OpCodes.Call, module.ImportReference(methods[$"{c.TypeFullName}::{c.Method}"]));
                break;

            case NewCorpus n:
                il.Emit(OpCodes.Newobj, module.ImportReference(ctors[n.TypeFullName]));
                il.Emit(OpCodes.Pop);
                break;

            case CallFramework f when f.Member == ".ctor":
                il.Emit(OpCodes.Newobj, ext.Ctor(f.Assembly, f.Namespace, f.Type));
                il.Emit(OpCodes.Pop);
                break;

            case CallFramework f:
                il.Emit(OpCodes.Call, ext.StaticVoid(f.Assembly, f.Namespace, f.Type, f.Member));
                break;

            case GetTypeLiteral g:
                il.Emit(OpCodes.Ldstr, g.TypeName);
                il.Emit(OpCodes.Call, ext.TypeGetType());
                il.Emit(OpCodes.Pop);
                break;

            // The pattern that defeats static analysis, written the way real code writes it:
            // a prefix concatenated with something that came from configuration.
            case GetTypeComputed g:
                il.Emit(OpCodes.Ldstr, "Contoso.Claims.Plugins.");
                il.Emit(OpCodes.Ldstr, g.ConfigKey);
                il.Emit(OpCodes.Call, ext.StringConcat());
                il.Emit(OpCodes.Call, ext.TypeGetType());
                il.Emit(OpCodes.Pop);
                break;

            case ActivatorTypeOf a:
                il.Emit(OpCodes.Ldtoken, module.ImportReference(types[a.TypeFullName]));
                il.Emit(OpCodes.Call, ext.TypeFromHandle());
                il.Emit(OpCodes.Call, ext.ActivatorCreateInstance());
                il.Emit(OpCodes.Pop);
                break;

            default:
                throw new NotSupportedException(op.GetType().Name);
        }
    }

    private static (string ns, string name) Split(string fullName)
    {
        var i = fullName.LastIndexOf('.');
        return i < 0 ? ("", fullName) : (fullName[..i], fullName[(i + 1)..]);
    }

    private static MethodReference ObjectCtor(ModuleDefinition m) =>
        new(".ctor", m.TypeSystem.Void, m.TypeSystem.Object) { HasThis = true };

    /// <summary>Caches references into assemblies that are not present on this machine.</summary>
    private sealed class ExternalRefs(ModuleDefinition module)
    {
        private readonly SortedDictionary<string, AssemblyNameReference> _asms = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, TypeReference> _types = new(StringComparer.Ordinal);

        private AssemblyNameReference Asm(string name)
        {
            if (_asms.TryGetValue(name, out var existing)) return existing;
            if (name == "mscorlib" && module.TypeSystem.CoreLibrary is AssemblyNameReference core)
            {
                _asms[name] = core;
                return core;
            }
            var r = new AssemblyNameReference(name, Fx4);
            if (Tokens.TryGetValue(name, out var tok)) r.PublicKeyToken = tok;
            module.AssemblyReferences.Add(r);
            _asms[name] = r;
            return r;
        }

        public TypeReference Type(string asm, string ns, string name)
        {
            var key = $"{asm}|{ns}.{name}";
            if (_types.TryGetValue(key, out var existing)) return existing;
            var tr = new TypeReference(ns, name, module, Asm(asm));
            _types[key] = tr;
            return tr;
        }

        public MethodReference StaticVoid(string asm, string ns, string type, string member) =>
            new(member, module.TypeSystem.Void, Type(asm, ns, type)) { HasThis = false };

        public MethodReference Ctor(string asm, string ns, string type) =>
            new(".ctor", module.TypeSystem.Void, Type(asm, ns, type)) { HasThis = true };

        public MethodReference TypeGetType()
        {
            var t = Type("mscorlib", "System", "Type");
            var m = new MethodReference("GetType", t, t) { HasThis = false };
            m.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            return m;
        }

        public MethodReference TypeFromHandle()
        {
            var t = Type("mscorlib", "System", "Type");
            var m = new MethodReference("GetTypeFromHandle", t, t) { HasThis = false };
            m.Parameters.Add(new ParameterDefinition(Type("mscorlib", "System", "RuntimeTypeHandle")));
            return m;
        }

        public MethodReference ActivatorCreateInstance()
        {
            var a = Type("mscorlib", "System", "Activator");
            var m = new MethodReference("CreateInstance", module.TypeSystem.Object, a) { HasThis = false };
            m.Parameters.Add(new ParameterDefinition(Type("mscorlib", "System", "Type")));
            return m;
        }

        public MethodReference StringConcat()
        {
            var s = Type("mscorlib", "System", "String");
            var m = new MethodReference("Concat", module.TypeSystem.String, s) { HasThis = false };
            m.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            m.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            return m;
        }
    }
}
