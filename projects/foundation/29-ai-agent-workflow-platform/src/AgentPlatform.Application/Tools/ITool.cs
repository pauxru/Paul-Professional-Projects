using System.Text.Json.Nodes;
using AgentPlatform.Application.Abstractions;
using AgentPlatform.Domain.Abstractions;
using AgentPlatform.Domain.Tools;

namespace AgentPlatform.Application.Tools;

/// <summary>
/// Everything a tool needs to execute, supplied by the invoker. A tool never reaches outside this
/// context for identity, time or the calling run — this is part of bounding its blast radius.
/// </summary>
public sealed record ToolExecutionContext(
    AgentCaller Caller,
    string RunId,
    string StepId,
    string CorrelationId,
    IClock Clock);

/// <summary>
/// A statically-registered tool. Implementations receive <b>already schema-validated and coerced</b>
/// arguments and must return a structured <see cref="ToolResult"/> — they never throw for
/// caller/argument problems. Tools are the only bridge from model output to effects, so they are a
/// closed allow-list with typed contracts.
/// </summary>
public interface ITool
{
    ToolDescriptor Descriptor { get; }

    Task<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonObject arguments, CancellationToken cancellationToken);
}

/// <summary>The closed registry of tools. There is no dynamic loading or discovery by reflection.</summary>
public interface IToolRegistry
{
    IReadOnlyCollection<ToolDescriptor> Descriptors { get; }

    bool TryGet(string name, out ITool tool);

    ITool? Find(string name);
}
