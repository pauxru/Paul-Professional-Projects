using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FeatureFlags.Domain;

public static class Bucketing
{
    public const int BucketCount = 100_000;

    public static int GetBucket(string flagKey, string salt, string contextKey)
    {
        var input = Encoding.UTF8.GetBytes($"{flagKey}|{salt}|{contextKey}");
        var hash = SHA256.HashData(input);
        var number = BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(0, sizeof(ulong)));
        return (int)(number % BucketCount);
    }
}

public static class PrerequisiteGraphValidator
{
    public static IReadOnlyList<string> FindCycle(IEnumerable<FlagDefinition> flags)
    {
        var map = flags.GroupBy(f => f.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();

        foreach (var key in map.Keys)
        {
            var cycle = Visit(key);
            if (cycle.Count > 0)
            {
                return cycle;
            }
        }

        return Array.Empty<string>();

        List<string> Visit(string key)
        {
            if (visiting.Contains(key))
            {
                var start = path.IndexOf(key);
                var result = path.Skip(start).ToList();
                result.Add(key);
                return result;
            }

            if (!visited.Add(key) || !map.TryGetValue(key, out var flag))
            {
                return [];
            }

            visiting.Add(key);
            path.Add(key);
            foreach (var prerequisite in flag.Prerequisites)
            {
                if (map.ContainsKey(prerequisite.FlagKey))
                {
                    var result = Visit(prerequisite.FlagKey);
                    if (result.Count > 0)
                    {
                        return result;
                    }
                }
            }

            path.RemoveAt(path.Count - 1);
            visiting.Remove(key);
            return [];
        }
    }

    public static bool HasCycle(IEnumerable<FlagDefinition> flags) => FindCycle(flags).Count > 0;
}

public sealed class FlagEvaluator
{
    public EvaluationResult Evaluate(EnvironmentConfiguration configuration, string flagKey, EvaluationContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);
        return EvaluateInternal(configuration, flagKey, context, now, new HashSet<string>(StringComparer.Ordinal));
    }

    private EvaluationResult EvaluateInternal(
        EnvironmentConfiguration configuration,
        string flagKey,
        EvaluationContext context,
        DateTimeOffset now,
        HashSet<string> evaluationPath)
    {
        var flag = configuration.FindFlag(flagKey);
        if (flag is null)
        {
            return EvaluationResult.Error(FeatureFlagJson.Null, EvaluationErrorKind.FlagNotFound);
        }

        if (!evaluationPath.Add(flagKey))
        {
            return EvaluationResult.Error(flag.GetFallbackValue(), EvaluationErrorKind.PrerequisiteCycle);
        }

        try
        {
            if (!flag.IsEffectiveOn(now))
            {
                return VariationOrError(flag, flag.OffVariation, EvaluationReason.Off());
            }

            foreach (var prerequisite in flag.Prerequisites)
            {
                var result = EvaluateInternal(configuration, prerequisite.FlagKey, context, now, evaluationPath);
                if (result.Reason.ErrorKind == EvaluationErrorKind.PrerequisiteCycle)
                {
                    return EvaluationResult.Error(flag.GetFallbackValue(), EvaluationErrorKind.PrerequisiteCycle);
                }

                if (result.VariationIndex != prerequisite.VariationIndex)
                {
                    return VariationOrError(flag, flag.OffVariation, EvaluationReason.PrerequisiteFailed());
                }
            }

            foreach (var target in flag.Targets)
            {
                if (target.ContextKeys.Contains(context.Key, StringComparer.Ordinal))
                {
                    return VariationOrError(flag, target.VariationIndex, EvaluationReason.TargetMatch());
                }
            }

            foreach (var target in flag.SegmentTargets)
            {
                if (IsSegmentMember(configuration, target.SegmentKey, context, new HashSet<string>(StringComparer.Ordinal)))
                {
                    return VariationOrError(flag, target.VariationIndex, EvaluationReason.SegmentMatch());
                }
            }

            for (var index = 0; index < flag.Rules.Count; index++)
            {
                var rule = flag.Rules[index];
                if (rule.Clauses.All(clause => ClauseEvaluator.IsMatch(context, clause)))
                {
                    return VariationOrError(flag, rule.VariationIndex, EvaluationReason.RuleMatch(index));
                }
            }

            if (flag.Rollout is not null)
            {
                var bucket = Bucketing.GetBucket(flag.Key, flag.Salt, context.Key);
                var cumulative = 0;
                foreach (var allocation in flag.Rollout.Variations)
                {
                    if (allocation.WeightBps < 0)
                    {
                        return EvaluationResult.Error(flag.GetFallbackValue(), EvaluationErrorKind.InvalidConfiguration);
                    }

                    cumulative += allocation.WeightBps;
                    if (cumulative > Bucketing.BucketCount)
                    {
                        return EvaluationResult.Error(flag.GetFallbackValue(), EvaluationErrorKind.InvalidConfiguration);
                    }

                    if (bucket < cumulative)
                    {
                        return VariationOrError(flag, allocation.VariationIndex, EvaluationReason.Fallthrough());
                    }
                }
            }

            return VariationOrError(flag, flag.FallthroughVariation, EvaluationReason.Fallthrough());
        }
        finally
        {
            evaluationPath.Remove(flagKey);
        }
    }

    private static EvaluationResult VariationOrError(FlagDefinition flag, int variationIndex, EvaluationReason reason)
    {
        return flag.TryGetVariation(variationIndex, out var variation)
            ? new EvaluationResult(variation.Value, variation.Index, reason)
            : EvaluationResult.Error(flag.GetFallbackValue(), EvaluationErrorKind.InvalidConfiguration);
    }

    private static bool IsSegmentMember(
        EnvironmentConfiguration configuration,
        string segmentKey,
        EvaluationContext context,
        HashSet<string> path)
    {
        if (!path.Add(segmentKey))
        {
            return false;
        }

        try
        {
            var segment = configuration.FindSegment(segmentKey);
            if (segment is null || segment.ExcludedContextKeys.Contains(context.Key, StringComparer.Ordinal))
            {
                return false;
            }

            if (segment.ExcludedSegments.Any(key => IsSegmentMember(configuration, key, context, path)))
            {
                return false;
            }

            if (segment.IncludedContextKeys.Contains(context.Key, StringComparer.Ordinal)
                || segment.IncludedSegments.Any(key => IsSegmentMember(configuration, key, context, path)))
            {
                return true;
            }

            return segment.Rules.Any(rule => rule.Clauses.All(clause => ClauseEvaluator.IsMatch(context, clause)));
        }
        finally
        {
            path.Remove(segmentKey);
        }
    }
}

public static class ClauseEvaluator
{
    private const int MaxRegexInputLength = 4_096;
    private const int MaxRegexPatternLength = 256;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    public static bool IsMatch(EvaluationContext context, RuleClause clause)
    {
        var hasAttribute = context.TryGetAttribute(clause.Attribute, out var attribute);
        JsonElement? actual = hasAttribute ? attribute : null;
        return clause.Operator switch
        {
            ClauseOperator.Equals => Equal(actual, clause.Value),
            ClauseOperator.NotEquals => !Equal(actual, clause.Value),
            ClauseOperator.In => clause.Values.Any(value => Equal(actual, value)),
            ClauseOperator.NotIn => clause.Values.All(value => !Equal(actual, value)),
            ClauseOperator.Contains => Contains(actual, clause.Value),
            ClauseOperator.StartsWith => Text(actual, clause.Value, (a, b) => a.StartsWith(b, StringComparison.Ordinal)),
            ClauseOperator.EndsWith => Text(actual, clause.Value, (a, b) => a.EndsWith(b, StringComparison.Ordinal)),
            ClauseOperator.MatchesRegex => Regex(actual, clause.Value),
            ClauseOperator.GreaterThan => Number(actual, clause.Value, (a, b) => a > b),
            ClauseOperator.LessThan => Number(actual, clause.Value, (a, b) => a < b),
            ClauseOperator.Before => Date(actual, clause.Value, (a, b) => a < b),
            ClauseOperator.After => Date(actual, clause.Value, (a, b) => a > b),
            ClauseOperator.SemVerEqual => SemanticVersionCompare(actual, clause.Value, comparison => comparison == 0),
            ClauseOperator.SemVerGreater => SemanticVersionCompare(actual, clause.Value, comparison => comparison > 0),
            _ => false
        };
    }

    private static bool Equal(JsonElement? actual, JsonElement? expected)
    {
        if (IsNull(actual) || IsNull(expected))
        {
            return IsNull(actual) && IsNull(expected);
        }

        var actualValue = actual!.Value;
        var expectedValue = expected!.Value;
        if (TryDecimal(actualValue, out var actualNumber) && TryDecimal(expectedValue, out var expectedNumber))
        {
            return actualNumber == expectedNumber;
        }

        if (TryBool(actualValue, out var actualBool) && TryBool(expectedValue, out var expectedBool))
        {
            return actualBool == expectedBool;
        }

        if (actualValue.ValueKind == JsonValueKind.String && expectedValue.ValueKind == JsonValueKind.String)
        {
            return string.Equals(actualValue.GetString(), expectedValue.GetString(), StringComparison.Ordinal);
        }

        return string.Equals(actualValue.GetRawText(), expectedValue.GetRawText(), StringComparison.Ordinal);
    }

    private static bool Contains(JsonElement? actual, JsonElement? expected)
    {
        if (IsNull(actual) || IsNull(expected))
        {
            return false;
        }

        if (actual!.Value.ValueKind == JsonValueKind.Array)
        {
            return actual.Value.EnumerateArray().Any(item => Equal(item, expected));
        }

        return Text(actual, expected, (a, b) => a.Contains(b, StringComparison.Ordinal));
    }

    private static bool Text(JsonElement? actual, JsonElement? expected, Func<string, string, bool> comparison)
    {
        return TryText(actual, out var actualText) && TryText(expected, out var expectedText) && comparison(actualText, expectedText);
    }

    private static bool Regex(JsonElement? actual, JsonElement? expected)
    {
        if (!TryText(actual, out var input) || !TryText(expected, out var pattern)
            || input.Length > MaxRegexInputLength || pattern.Length > MaxRegexPatternLength)
        {
            return false;
        }

        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout).IsMatch(input);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool Number(JsonElement? actual, JsonElement? expected, Func<decimal, decimal, bool> comparison)
    {
        return actual.HasValue && expected.HasValue
            && TryDecimal(actual.Value, out var left)
            && TryDecimal(expected.Value, out var right)
            && comparison(left, right);
    }

    private static bool Date(JsonElement? actual, JsonElement? expected, Func<DateTimeOffset, DateTimeOffset, bool> comparison)
    {
        return TryDate(actual, out var left) && TryDate(expected, out var right) && comparison(left, right);
    }

    private static bool SemanticVersionCompare(JsonElement? actual, JsonElement? expected, Func<int, bool> comparison)
    {
        return TryText(actual, out var actualText) && TryText(expected, out var expectedText)
            && SemanticVersion.TryParse(actualText, out var left)
            && SemanticVersion.TryParse(expectedText, out var right)
            && comparison(left.CompareTo(right));
    }

    private static bool IsNull(JsonElement? value) => !value.HasValue || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    private static bool TryText(JsonElement? value, out string text)
    {
        text = string.Empty;
        if (IsNull(value))
        {
            return false;
        }

        var element = value!.Value;
        if (element.ValueKind == JsonValueKind.String)
        {
            text = element.GetString() ?? string.Empty;
            return true;
        }

        if (element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            text = element.GetRawText();
            return true;
        }

        return false;
    }

    private static bool TryDecimal(JsonElement value, out decimal number)
    {
        number = default;
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetDecimal(out number);
        }

        return value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number);
    }

    private static bool TryBool(JsonElement value, out bool result)
    {
        result = false;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            result = value.GetBoolean();
            return true;
        }

        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out result);
    }

    private static bool TryDate(JsonElement? value, out DateTimeOffset date)
    {
        date = default;
        return TryText(value, out var text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date);
    }
}

internal readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<SemanticVersion>
{
    public static bool TryParse(string text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().TrimStart('v', 'V').Split('+', 2)[0].Split('-', 2);
        var numeric = parts[0].Split('.');
        if (numeric.Length is < 1 or > 3 || !int.TryParse(numeric[0], out var major))
        {
            return false;
        }

        var minor = numeric.Length > 1 && int.TryParse(numeric[1], out var parsedMinor) ? parsedMinor : 0;
        var patch = numeric.Length > 2 && int.TryParse(numeric[2], out var parsedPatch) ? parsedPatch : 0;
        if (minor < 0 || patch < 0 || major < 0)
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, parts.Length == 2 ? parts[1] : null);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var numeric = Major.CompareTo(other.Major);
        if (numeric != 0) return numeric;
        numeric = Minor.CompareTo(other.Minor);
        if (numeric != 0) return numeric;
        numeric = Patch.CompareTo(other.Patch);
        if (numeric != 0) return numeric;
        if (Prerelease is null && other.Prerelease is not null) return 1;
        if (Prerelease is not null && other.Prerelease is null) return -1;
        return string.Compare(Prerelease, other.Prerelease, StringComparison.Ordinal);
    }
}
