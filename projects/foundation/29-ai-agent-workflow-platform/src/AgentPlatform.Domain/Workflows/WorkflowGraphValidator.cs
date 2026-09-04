using System.Text.Json.Nodes;

namespace AgentPlatform.Domain.Workflows;

/// <summary>Outcome of validating a workflow definition's graph at registration time.</summary>
public sealed record WorkflowValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static WorkflowValidationResult Ok { get; } = new(true, Array.Empty<string>());
    public string Summary => string.Join("; ", Errors);
}

/// <summary>
/// Validates a workflow graph before it is accepted. Rejects: duplicate/missing step ids, dangling
/// transitions, unreachable steps, top-level cycles (bounded iteration must use a LoopStep),
/// missing terminal, unknown tool/prompt/transform references, and bindings to variables that no
/// step produces. This is what stops a malformed or malicious workflow from ever running.
/// </summary>
public sealed class WorkflowGraphValidator
{
    private readonly IReadOnlySet<string> _knownTools;
    private readonly IReadOnlySet<string> _knownPrompts;
    private readonly IReadOnlySet<string> _knownTransforms;

    public WorkflowGraphValidator(
        IEnumerable<string> knownTools,
        IEnumerable<string> knownPrompts,
        IEnumerable<string> knownTransforms)
    {
        _knownTools = knownTools.ToHashSet(StringComparer.Ordinal);
        _knownPrompts = knownPrompts.ToHashSet(StringComparer.Ordinal);
        _knownTransforms = knownTransforms.ToHashSet(StringComparer.Ordinal);
    }

    public WorkflowValidationResult Validate(WorkflowDefinition workflow)
    {
        var errors = new List<string>();
        var steps = workflow.Steps;

        // 1. Unique ids.
        var byId = new Dictionary<string, WorkflowStep>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (!byId.TryAdd(step.Id, step))
                errors.Add($"Duplicate step id '{step.Id}'.");
        }

        // 2. Start step exists.
        if (!byId.ContainsKey(workflow.StartStepId))
            errors.Add($"Start step '{workflow.StartStepId}' does not exist.");

        // 3. Transitions reference existing steps.
        foreach (var step in steps)
            foreach (var successor in step.Successors())
                if (!byId.ContainsKey(successor))
                    errors.Add($"Step '{step.Id}' transitions to unknown step '{successor}'.");

        // Declared variables = workflow inputs ∪ every step's produced output (incl. inline bodies).
        var declared = new HashSet<string>(workflow.InputVariables, StringComparer.Ordinal);
        foreach (var step in EnumerateAll(steps))
            CollectProducedVariables(step, declared);

        // 4. Reference and binding checks (top-level and inline).
        foreach (var step in EnumerateAll(steps))
            ValidateStepReferences(step, declared, errors);

        // 5. Reachability + terminal presence (top-level graph only).
        if (byId.ContainsKey(workflow.StartStepId))
        {
            var reachable = Reachable(workflow.StartStepId, byId);
            foreach (var step in steps)
                if (!reachable.Contains(step.Id))
                    errors.Add($"Step '{step.Id}' is unreachable from the start step.");

            if (!steps.Any(s => s is TerminalStep && reachable.Contains(s.Id)))
                errors.Add("No terminal step is reachable from the start step.");

            // 6. No top-level cycle.
            if (HasCycle(workflow.StartStepId, byId))
                errors.Add("Workflow graph contains a cycle without a bound; use a LoopStep for bounded iteration.");
        }

        return errors.Count == 0 ? WorkflowValidationResult.Ok : new WorkflowValidationResult(false, errors);
    }

    private static IEnumerable<WorkflowStep> EnumerateAll(IEnumerable<WorkflowStep> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            switch (step)
            {
                case ParallelStep p:
                    foreach (var child in EnumerateAll(p.Branches)) yield return child;
                    break;
                case LoopStep l:
                    foreach (var child in EnumerateAll(l.Body)) yield return child;
                    break;
            }
        }
    }

    private static void CollectProducedVariables(WorkflowStep step, HashSet<string> declared)
    {
        switch (step)
        {
            case ModelStep m: declared.Add(m.OutputVariable); break;
            case ToolStep t: declared.Add(t.OutputVariable); break;
            case TransformStep tr: declared.Add(tr.OutputVariable); break;
            case LoopStep l when l.ItemVariable is not null: declared.Add(l.ItemVariable); break;
        }
    }

    private void ValidateStepReferences(WorkflowStep step, HashSet<string> declared, List<string> errors)
    {
        switch (step)
        {
            case ModelStep m:
                if (!_knownPrompts.Contains(m.PromptTemplateId))
                    errors.Add($"Step '{m.Id}' references unknown prompt '{m.PromptTemplateId}'.");
                foreach (var tool in m.AllowedTools)
                    if (!_knownTools.Contains(tool))
                        errors.Add($"Step '{m.Id}' allows unknown tool '{tool}'.");
                if (m.InputVariable is not null && !declared.Contains(m.InputVariable.Split('.', 2)[0]))
                    errors.Add($"Step '{m.Id}' binds missing variable '{m.InputVariable}'.");
                if (m.MaxIterations <= 0)
                    errors.Add($"Step '{m.Id}' must allow at least one iteration.");
                break;

            case ToolStep t:
                if (!_knownTools.Contains(t.ToolName))
                    errors.Add($"Step '{t.Id}' references unknown tool '{t.ToolName}'.");
                foreach (var reference in VariableReferences(t.ArgumentsTemplate))
                    if (!declared.Contains(reference))
                        errors.Add($"Step '{t.Id}' binds missing variable '{reference}'.");
                break;

            case ConditionStep c:
                if (!declared.Contains(c.Variable.Split('.', 2)[0]))
                    errors.Add($"Step '{c.Id}' tests missing variable '{c.Variable}'.");
                break;

            case TransformStep tr:
                if (!_knownTransforms.Contains(tr.TransformId))
                    errors.Add($"Step '{tr.Id}' references unknown transform '{tr.TransformId}'.");
                break;

            case HumanApprovalStep h:
                if (!declared.Contains(h.ProposedActionVariable))
                    errors.Add($"Step '{h.Id}' references missing variable '{h.ProposedActionVariable}'.");
                break;

            case LoopStep l:
                if (l.MaxIterations <= 0)
                    errors.Add($"Loop step '{l.Id}' must have a positive iteration cap.");
                if (l.OverVariable is not null && !declared.Contains(l.OverVariable))
                    errors.Add($"Loop step '{l.Id}' iterates missing variable '{l.OverVariable}'.");
                break;
        }
    }

    private static IEnumerable<string> VariableReferences(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kvp in obj)
                    foreach (var r in VariableReferences(kvp.Value)) yield return r;
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    foreach (var r in VariableReferences(item)) yield return r;
                break;
            case JsonValue v when v.TryGetValue<string>(out var s) && s.StartsWith('$'):
                yield return s[1..].Split('.', 2)[0];
                break;
        }
    }

    private static HashSet<string> Reachable(string start, Dictionary<string, WorkflowStep> byId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id) || !byId.TryGetValue(id, out var step)) continue;
            foreach (var successor in step.Successors()) stack.Push(successor);
        }
        return seen;
    }

    private static bool HasCycle(string start, Dictionary<string, WorkflowStep> byId)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0=unseen,1=in-stack,2=done
        return Visit(start);

        bool Visit(string id)
        {
            if (!byId.TryGetValue(id, out var step)) return false;
            state[id] = 1;
            foreach (var successor in step.Successors())
            {
                var s = state.GetValueOrDefault(successor);
                if (s == 1) return true;
                if (s == 0 && Visit(successor)) return true;
            }
            state[id] = 2;
            return false;
        }
    }
}
