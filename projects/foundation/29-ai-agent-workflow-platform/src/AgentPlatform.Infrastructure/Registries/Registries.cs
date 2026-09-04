using AgentPlatform.Application.Abstractions;
using AgentPlatform.Application.Tools;
using AgentPlatform.Domain.Prompts;
using AgentPlatform.Domain.Tools;
using AgentPlatform.Domain.Workflows;

namespace AgentPlatform.Infrastructure.Registries;

/// <summary>
/// The closed tool allow-list. Tools are supplied at construction (statically registered in DI);
/// there is no reflection-based discovery or dynamic loading. A name that is not here cannot run.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        _tools = new Dictionary<string, ITool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (!_tools.TryAdd(tool.Descriptor.Name, tool))
                throw new InvalidOperationException($"Duplicate tool registration '{tool.Descriptor.Name}'.");
        }
    }

    public IReadOnlyCollection<ToolDescriptor> Descriptors =>
        _tools.Values.Select(t => t.Descriptor).ToList();

    public bool TryGet(string name, out ITool tool) => _tools.TryGetValue(name, out tool!);

    public ITool? Find(string name) => _tools.GetValueOrDefault(name);
}

/// <summary>The closed registry of deterministic state transforms.</summary>
public sealed class TransformRegistry : ITransformRegistry
{
    private readonly Dictionary<string, IStateTransform> _transforms;

    public TransformRegistry(IEnumerable<IStateTransform> transforms)
    {
        _transforms = new Dictionary<string, IStateTransform>(StringComparer.Ordinal);
        foreach (var transform in transforms)
        {
            if (!_transforms.TryAdd(transform.Id, transform))
                throw new InvalidOperationException($"Duplicate transform registration '{transform.Id}'.");
        }
    }

    public bool TryGet(string id, out IStateTransform transform) => _transforms.TryGetValue(id, out transform!);

    public IReadOnlyCollection<string> Ids => _transforms.Keys.ToList();
}

/// <summary>In-memory registry of validated, versioned workflow definitions.</summary>
public sealed class WorkflowRegistry : IWorkflowRegistry
{
    private readonly List<WorkflowDefinition> _all = new();
    private readonly Dictionary<(string, int), WorkflowDefinition> _byKey = new();

    public WorkflowRegistry(IEnumerable<WorkflowDefinition> definitions)
    {
        foreach (var definition in definitions)
            Register(definition);
    }

    public void Register(WorkflowDefinition definition)
    {
        if (!_byKey.TryAdd((definition.Name, definition.Version), definition))
            throw new InvalidOperationException($"Duplicate workflow '{definition.Key}'.");
        _all.Add(definition);
    }

    public WorkflowDefinition? Get(string name, int version) => _byKey.GetValueOrDefault((name, version));

    public WorkflowDefinition? GetLatest(string name) =>
        _all.Where(w => string.Equals(w.Name, name, StringComparison.Ordinal))
            .OrderByDescending(w => w.Version).FirstOrDefault();

    public IReadOnlyCollection<WorkflowDefinition> All => _all;

    public IReadOnlyCollection<int> Versions(string name) =>
        _all.Where(w => string.Equals(w.Name, name, StringComparison.Ordinal))
            .Select(w => w.Version).OrderBy(v => v).ToList();
}

/// <summary>In-memory registry of versioned prompt templates.</summary>
public sealed class PromptRegistry : IPromptRegistry
{
    private readonly List<PromptTemplate> _all = new();
    private readonly Dictionary<(string, int), PromptTemplate> _byKey = new();

    public PromptRegistry(IEnumerable<PromptTemplate> templates)
    {
        foreach (var template in templates)
        {
            if (!_byKey.TryAdd((template.Name, template.Version), template))
                throw new InvalidOperationException($"Duplicate prompt '{template.Key}'.");
            _all.Add(template);
        }
    }

    public PromptTemplate? Get(string name, int version) => _byKey.GetValueOrDefault((name, version));

    public PromptTemplate? GetLatest(string name) =>
        _all.Where(p => string.Equals(p.Name, name, StringComparison.Ordinal))
            .OrderByDescending(p => p.Version).FirstOrDefault();

    public IReadOnlyCollection<PromptTemplate> All => _all;
}
