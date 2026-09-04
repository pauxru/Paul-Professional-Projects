namespace FeatureFlags.Domain;

public static class FlagConfigurationValidator
{
    public static IReadOnlyList<string> Validate(EnvironmentConfiguration configuration)
    {
        var errors = new List<string>();
        var duplicateKeys = configuration.Flags.GroupBy(f => f.Key, StringComparer.Ordinal).Where(group => group.Count() > 1);
        errors.AddRange(duplicateKeys.Select(group => $"Duplicate flag key '{group.Key}'."));

        foreach (var flag in configuration.Flags)
        {
            if (string.IsNullOrWhiteSpace(flag.Key)) errors.Add("A flag key is required.");
            if (!flag.TryGetVariation(flag.OffVariation, out _)) errors.Add($"Flag '{flag.Key}' has an invalid off variation.");
            if (!flag.TryGetVariation(flag.FallthroughVariation, out _)) errors.Add($"Flag '{flag.Key}' has an invalid fallthrough variation.");
            if (flag.Rollout is not null && flag.Rollout.Variations.Sum(item => item.WeightBps) > Bucketing.BucketCount)
            {
                errors.Add($"Flag '{flag.Key}' rollout exceeds 100%.");
            }
        }

        var cycle = PrerequisiteGraphValidator.FindCycle(configuration.Flags);
        if (cycle.Count > 0)
        {
            errors.Add($"Prerequisite cycle: {string.Join(" -> ", cycle)}.");
        }

        return errors;
    }
}
