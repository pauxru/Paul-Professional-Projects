using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Northstar.Iga.Domain;

public sealed record AccessDerivation(
    Guid EntitlementId,
    string Permission,
    IReadOnlyList<string> Path,
    DateTimeOffset? ExpiresAt = null);

public sealed record AuthorizationContext(
    UserIdentity Subject,
    string Permission,
    IReadOnlyDictionary<string, object?> Resource,
    IReadOnlyDictionary<string, object?> Environment,
    DateTimeOffset Now);

public sealed record PolicyEvaluationTrace(
    Guid PolicyId,
    string PolicyName,
    PolicyEffect Effect,
    int Priority,
    int Specificity,
    bool PermissionMatched,
    bool ConditionsMatched,
    IReadOnlyList<string> Reasons);

public sealed record AuthorizationDecision(
    bool Allowed,
    string Decision,
    string Explanation,
    Guid? DecisivePolicyId,
    IReadOnlyList<AccessDerivation> Derivations,
    IReadOnlyList<PolicyEvaluationTrace> PoliciesEvaluated,
    double ElapsedMilliseconds);

public static class PolicyEngine
{
    public static AuthorizationDecision Evaluate(
        AuthorizationContext context,
        IEnumerable<AccessDerivation> derivations,
        IEnumerable<PolicyDefinition> policies)
    {
        var stopwatch = Stopwatch.StartNew();
        var activeDerivations = derivations
            .Where(x => PermissionMatches(x.Permission, context.Permission))
            .Where(x => x.ExpiresAt is null || x.ExpiresAt > context.Now)
            .ToArray();
        var traces = policies
            .Where(x => x.Enabled)
            .Select(policy => EvaluatePolicy(policy, context))
            .OrderByDescending(x => x.Priority)
            .ThenByDescending(x => x.Specificity)
            .ThenBy(x => x.PolicyName, StringComparer.Ordinal)
            .ToArray();

        var decisiveDeny = traces
            .Where(x => x.Effect == PolicyEffect.Deny && x.PermissionMatched && x.ConditionsMatched)
            .OrderByDescending(x => x.Priority)
            .ThenByDescending(x => x.Specificity)
            .FirstOrDefault();

        if (decisiveDeny is not null)
        {
            stopwatch.Stop();
            return new AuthorizationDecision(
                false,
                "Deny",
                $"Denied by explicit policy '{decisiveDeny.PolicyName}'. Explicit deny overrides all grants and allows.",
                decisiveDeny.PolicyId,
                activeDerivations,
                traces,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        var decisiveAllow = traces
            .Where(x => x.Effect == PolicyEffect.Allow && x.PermissionMatched && x.ConditionsMatched)
            .OrderByDescending(x => x.Priority)
            .ThenByDescending(x => x.Specificity)
            .FirstOrDefault();

        if (decisiveAllow is not null)
        {
            stopwatch.Stop();
            return new AuthorizationDecision(
                true,
                "Allow",
                $"Allowed by policy '{decisiveAllow.PolicyName}' after evaluating deny rules.",
                decisiveAllow.PolicyId,
                activeDerivations,
                traces,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        if (activeDerivations.Length > 0)
        {
            stopwatch.Stop();
            return new AuthorizationDecision(
                true,
                "Allow",
                "Allowed by effective RBAC/group/JIT entitlement; no matching explicit deny policy was found.",
                null,
                activeDerivations,
                traces,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        stopwatch.Stop();
        return new AuthorizationDecision(
            false,
            "Deny",
            "Denied by default because no effective entitlement or matching allow policy was found.",
            null,
            activeDerivations,
            traces,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    public static bool PermissionMatches(string pattern, string permission)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(permission, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static PolicyEvaluationTrace EvaluatePolicy(PolicyDefinition policy, AuthorizationContext context)
    {
        var permissionMatched = PermissionMatches(policy.PermissionPattern, context.Permission);
        var reasons = new List<string>
        {
            permissionMatched
                ? $"Permission '{context.Permission}' matched '{policy.PermissionPattern}'."
                : $"Permission '{context.Permission}' did not match '{policy.PermissionPattern}'."
        };

        var conditionResult = permissionMatched
            ? PolicyConditionMatcher.Match(policy.ConditionsJson, context)
            : new ConditionMatchResult(false, 0, ["Conditions skipped because the permission pattern did not match."]);
        reasons.AddRange(conditionResult.Reasons);

        return new PolicyEvaluationTrace(
            policy.Id,
            policy.Name,
            policy.Effect,
            policy.Priority,
            CalculateSpecificity(policy.PermissionPattern, conditionResult.ConditionCount),
            permissionMatched,
            permissionMatched && conditionResult.Matched,
            reasons);
    }

    private static int CalculateSpecificity(string pattern, int conditionCount) =>
        pattern.Count(x => x != '*') + (conditionCount * 10);
}

public sealed record ConditionMatchResult(bool Matched, int ConditionCount, IReadOnlyList<string> Reasons);

public static class PolicyConditionMatcher
{
    public static ConditionMatchResult Match(string conditionsJson, AuthorizationContext context)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(conditionsJson) ? "{}" : conditionsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new DomainRuleException("Policy conditions must be a JSON object.");
        }

        var reasons = new List<string>();
        var count = 0;
        var allMatched = true;
        foreach (var section in document.RootElement.EnumerateObject())
        {
            if (section.Value.ValueKind != JsonValueKind.Object)
            {
                throw new DomainRuleException($"Policy condition section '{section.Name}' must be an object.");
            }

            foreach (var condition in section.Value.EnumerateObject())
            {
                count++;
                var actual = GetActual(section.Name, condition.Name, context);
                var matched = Compare(section.Name, condition.Name, actual, condition.Value, context);
                allMatched &= matched;
                reasons.Add(
                    $"{section.Name}.{condition.Name} {(matched ? "matched" : "did not match")} " +
                    $"(actual='{FormatActual(actual)}', expected='{condition.Value}').");
            }
        }

        if (count == 0)
        {
            reasons.Add("Policy has no conditions and therefore matches unconditionally.");
        }

        return new ConditionMatchResult(allMatched, count, reasons);
    }

    private static object? GetActual(string section, string name, AuthorizationContext context)
    {
        if (section.Equals("subject", StringComparison.OrdinalIgnoreCase))
        {
            return name.ToLowerInvariant() switch
            {
                "id" => context.Subject.Id,
                "department" => context.Subject.Department,
                "jobtitle" => context.Subject.JobTitle,
                "managerid" => context.Subject.ManagerId,
                "location" => context.Subject.Location,
                "costcentre" => context.Subject.CostCentre,
                "employmenttype" => context.Subject.EmploymentType.ToString(),
                "clearance" => context.Subject.Clearance,
                "status" => context.Subject.Status.ToString(),
                _ => null
            };
        }

        var source = section.Equals("resource", StringComparison.OrdinalIgnoreCase)
            ? context.Resource
            : section.Equals("environment", StringComparison.OrdinalIgnoreCase)
                ? context.Environment
                : throw new DomainRuleException($"Unknown policy condition section '{section}'.");

        return source.FirstOrDefault(x => x.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static bool Compare(
        string section,
        string name,
        object? actual,
        JsonElement expected,
        AuthorizationContext context)
    {
        if (expected.ValueKind == JsonValueKind.Array)
        {
            return expected.EnumerateArray().Any(item => Compare(section, name, actual, item, context));
        }

        var expectedText = expected.ValueKind == JsonValueKind.String
            ? expected.GetString() ?? string.Empty
            : expected.ToString();

        if (expectedText.Equals("${subject.id}", StringComparison.OrdinalIgnoreCase))
        {
            expectedText = context.Subject.Id.ToString();
        }
        else if (expectedText.Equals("${subject.costCentre}", StringComparison.OrdinalIgnoreCase))
        {
            expectedText = context.Subject.CostCentre;
        }

        if (section.Equals("environment", StringComparison.OrdinalIgnoreCase) &&
            name.Equals("timeOfDay", StringComparison.OrdinalIgnoreCase))
        {
            return IsWithinTimeRange(context.Now, expectedText);
        }

        if (actual is null)
        {
            return expected.ValueKind == JsonValueKind.Null;
        }

        if (TryDecimal(actual, out var actualNumber) && TryComparison(expectedText, actualNumber, out var numericResult))
        {
            return numericResult;
        }

        var actualText = Convert.ToString(actual, CultureInfo.InvariantCulture) ?? string.Empty;
        if (expectedText.Contains('*', StringComparison.Ordinal))
        {
            return PolicyEngine.PermissionMatches(expectedText, actualText);
        }

        return actualText.Equals(expectedText, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryDecimal(object actual, out decimal value) =>
        decimal.TryParse(Convert.ToString(actual, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static bool TryComparison(string expected, decimal actual, out bool result)
    {
        var operations = new[] { ">=", "<=", ">", "<", "==" };
        foreach (var operation in operations)
        {
            if (!expected.StartsWith(operation, StringComparison.Ordinal) ||
                !decimal.TryParse(expected[operation.Length..], NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            result = operation switch
            {
                ">=" => actual >= value,
                "<=" => actual <= value,
                ">" => actual > value,
                "<" => actual < value,
                _ => actual == value
            };
            return true;
        }

        if (decimal.TryParse(expected, NumberStyles.Number, CultureInfo.InvariantCulture, out var exact))
        {
            result = actual == exact;
            return true;
        }

        result = false;
        return false;
    }

    private static bool IsWithinTimeRange(DateTimeOffset now, string range)
    {
        var parts = range.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !TimeOnly.TryParseExact(parts[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(parts[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            throw new DomainRuleException($"Invalid timeOfDay range '{range}'. Expected HH:mm-HH:mm.");
        }

        var current = TimeOnly.FromDateTime(now.UtcDateTime);
        return start <= end
            ? current >= start && current <= end
            : current >= start || current <= end;
    }

    private static string FormatActual(object? actual) =>
        Convert.ToString(actual, CultureInfo.InvariantCulture) ?? "null";
}
