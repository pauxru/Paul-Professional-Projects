using System.Globalization;
using System.Text.RegularExpressions;

namespace Northstar.Iga.Domain;

public static partial class DynamicGroupRuleEvaluator
{
    public static bool IsMatch(UserIdentity user, string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            return false;
        }

        return rule.Split("&&", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(clause => EvaluateClause(user, clause));
    }

    private static bool EvaluateClause(UserIdentity user, string clause)
    {
        var match = ClauseRegex().Match(clause);
        if (!match.Success)
        {
            throw new DomainRuleException($"Invalid dynamic group clause '{clause}'.");
        }

        var attribute = match.Groups["attribute"].Value;
        var operation = match.Groups["operator"].Value;
        var expected = match.Groups["value"].Value.Trim().Trim('\'', '"');
        var actual = GetAttribute(user, attribute);

        if (actual is null)
        {
            return operation == "!=" && !string.IsNullOrEmpty(expected);
        }

        if (actual is int number &&
            decimal.TryParse(expected, NumberStyles.Number, CultureInfo.InvariantCulture, out var expectedNumber))
        {
            return operation switch
            {
                "==" => number == expectedNumber,
                "!=" => number != expectedNumber,
                ">" => number > expectedNumber,
                ">=" => number >= expectedNumber,
                "<" => number < expectedNumber,
                "<=" => number <= expectedNumber,
                _ => false
            };
        }

        var value = Convert.ToString(actual, CultureInfo.InvariantCulture) ?? string.Empty;
        return operation switch
        {
            "==" => value.Equals(expected, StringComparison.OrdinalIgnoreCase),
            "!=" => !value.Equals(expected, StringComparison.OrdinalIgnoreCase),
            "contains" => value.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "startsWith" => value.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            _ => throw new DomainRuleException($"Operator '{operation}' is not valid for '{attribute}'.")
        };
    }

    private static object? GetAttribute(UserIdentity user, string attribute) =>
        attribute.ToLowerInvariant() switch
        {
            "department" => user.Department,
            "jobtitle" => user.JobTitle,
            "managerid" => user.ManagerId,
            "location" => user.Location,
            "costcentre" => user.CostCentre,
            "employmenttype" => user.EmploymentType.ToString(),
            "clearance" => user.Clearance,
            "status" => user.Status.ToString(),
            "email" => user.Email,
            _ => throw new DomainRuleException($"Unsupported identity attribute '{attribute}'.")
        };

    [GeneratedRegex(
        "^\\s*(?<attribute>[A-Za-z][A-Za-z0-9]*)\\s*(?<operator>==|!=|>=|<=|>|<|contains|startsWith)\\s*(?<value>.+?)\\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClauseRegex();
}
