using System.Text.Json.Nodes;
using AgentPlatform.Domain.Workflows;

namespace AgentPlatform.Application.Engine;

/// <summary>
/// Evaluates a <see cref="ConditionStep"/> deterministically. Branching in a workflow is ordinary
/// data comparison — never a model call — so routing is reproducible and auditable.
/// </summary>
public static class ConditionEvaluator
{
    public static bool Evaluate(JsonNode? actual, ConditionOperator op, JsonNode? expected) => op switch
    {
        ConditionOperator.Exists => actual is not null,
        ConditionOperator.Equals => JsonNode.DeepEquals(actual, expected),
        ConditionOperator.NotEquals => !JsonNode.DeepEquals(actual, expected),
        ConditionOperator.GreaterThan => Compare(actual, expected) > 0,
        ConditionOperator.GreaterThanOrEqual => Compare(actual, expected) >= 0,
        ConditionOperator.LessThan => Compare(actual, expected) < 0,
        ConditionOperator.LessThanOrEqual => Compare(actual, expected) <= 0,
        ConditionOperator.Contains => Contains(actual, expected),
        _ => false,
    };

    private static int Compare(JsonNode? actual, JsonNode? expected)
    {
        if (TryDecimal(actual, out var a) && TryDecimal(expected, out var b))
            return a.CompareTo(b);
        return string.CompareOrdinal(AsString(actual), AsString(expected));
    }

    private static bool Contains(JsonNode? actual, JsonNode? expected)
    {
        switch (actual)
        {
            case JsonArray arr:
                return arr.Any(item => JsonNode.DeepEquals(item, expected));
            case JsonValue when expected is not null:
                return AsString(actual).Contains(AsString(expected), StringComparison.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    private static bool TryDecimal(JsonNode? node, out decimal value)
    {
        value = 0m;
        if (node is not JsonValue jv) return false;
        if (jv.TryGetValue<decimal>(out value)) return true;
        if (jv.TryGetValue<double>(out var d)) { value = (decimal)d; return true; }
        if (jv.TryGetValue<long>(out var l)) { value = l; return true; }
        if (jv.TryGetValue<string>(out var s) && decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out value)) return true;
        return false;
    }

    private static string AsString(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };
}
