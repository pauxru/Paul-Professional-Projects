using System.Text.Json.Nodes;

namespace AgentPlatform.Application.Abstractions;

/// <summary>
/// A registered deterministic transform over the run state bag. Transforms are ordinary code
/// (chunking, validation, eligibility) — no model, fully testable, identical every run.
/// </summary>
public interface IStateTransform
{
    string Id { get; }

    /// <summary>Compute an output value from the current (read-only) state bag.</summary>
    JsonNode? Apply(JsonObject state);
}

public interface ITransformRegistry
{
    bool TryGet(string id, out IStateTransform transform);

    IReadOnlyCollection<string> Ids { get; }
}
