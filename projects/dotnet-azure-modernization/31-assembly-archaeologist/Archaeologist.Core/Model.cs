namespace Archaeologist.Core;

/// <summary>How hard an API is to move off, and why.</summary>
public enum Severity
{
    /// <summary>Works on modern .NET. No action.</summary>
    None = 0,

    /// <summary>Compiles and runs, but the shape changes (config, DI, hosting).</summary>
    Rewrite = 1,

    /// <summary>Present in the surface area of modern .NET but throws at runtime.</summary>
    PlatformNotSupported = 2,

    /// <summary>No modern equivalent. The design must change.</summary>
    NoEquivalent = 3,
}

public sealed record BlockerRule(
    string Namespace,
    string TypeName,
    string? MemberName,
    Severity Severity,
    string Reason,
    string Guidance)
{
    public string Key => MemberName is null
        ? $"{Namespace}.{TypeName}"
        : $"{Namespace}.{TypeName}::{MemberName}";
}

public sealed record BlockerHit(
    string AssemblyName,
    string TypeFullName,
    string MethodName,
    BlockerRule Rule,
    int IlOffset)
{
    public string Site => $"{TypeFullName}::{MethodName}+IL_{IlOffset:x4}";
}

/// <summary>A method as read from IL. Identity is the full signature-free name.</summary>
public sealed record MethodNode(
    string AssemblyName,
    string TypeFullName,
    string MethodName,
    bool IsEntryPointish)
{
    public string Id => $"{TypeFullName}::{MethodName}";
    public override string ToString() => Id;
}

public sealed record TypeNode(
    string AssemblyName,
    string FullName,
    bool IsPublic,
    IReadOnlyList<string> MethodNames)
{
    public override string ToString() => FullName;
}

public sealed record AssemblyNode(
    string Name,
    bool SourceAvailable,
    IReadOnlyList<string> TypeFullNames,
    IReadOnlyList<string> ExternalReferences)
{
    public override string ToString() => Name;
}

/// <summary>A directed edge with a weight, so cut cost is measurable rather than counted.</summary>
public sealed record Edge(string From, string To, int Weight)
{
    public override string ToString() => $"{From}->{To}({Weight})";
}

/// <summary>How a type can be reached other than by a direct call.</summary>
public enum ReflectionKind
{
    /// <summary>Type.GetType("Literal.Name") -- the name is in the IL, so it is decidable.</summary>
    LiteralTypeName,

    /// <summary>Activator.CreateInstance(typeof(X)) -- decidable.</summary>
    LiteralTypeToken,

    /// <summary>The name comes from config or concatenation. Not decidable from IL alone.</summary>
    ComputedTypeName,
}

public sealed record ReflectionSite(
    string AssemblyName,
    string TypeFullName,
    string MethodName,
    ReflectionKind Kind,
    string? ResolvedTarget,
    int IlOffset,
    string? RecoveredPrefix = null)
{
    public bool IsDecidable => Kind != ReflectionKind.ComputedTypeName;
    public string Site => $"{TypeFullName}::{MethodName}+IL_{IlOffset:x4}";
}
