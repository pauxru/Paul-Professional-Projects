using CloudCostObservability.Domain.Models;

namespace CloudCostObservability.Domain.Services;

public sealed record TagPolicy(IReadOnlyDictionary<string, IReadOnlySet<string>> RequiredTagsByResourceType)
{
    public static TagPolicy Default { get; } = new(
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["*"] = new HashSet<string>(["owner", "team", "environment", "cost-centre", "application"], StringComparer.OrdinalIgnoreCase),
            ["Microsoft.Compute/virtualMachines"] = new HashSet<string>(["owner", "team", "environment", "cost-centre", "application"], StringComparer.OrdinalIgnoreCase),
            ["Microsoft.Sql/servers/databases"] = new HashSet<string>(["owner", "team", "environment", "cost-centre", "application"], StringComparer.OrdinalIgnoreCase),
            ["Microsoft.Network/loadBalancers"] = new HashSet<string>(["owner", "team", "environment", "cost-centre", "application"], StringComparer.OrdinalIgnoreCase)
        });

    public IReadOnlySet<string> RequiredTagsFor(CloudResource resource) =>
        RequiredTagsByResourceType.TryGetValue(resource.ResourceType, out var tags) ? tags : RequiredTagsByResourceType["*"];
}

public sealed record TagRemediationItem(string ResourceId, string ResourceName, string MissingTag, string SuggestedValue, string Reason);
public sealed record TagCoverageReport(int TotalResources, int FullyCompliantResources, decimal CoveragePercent, IReadOnlyDictionary<string, int> MissingByTag, IReadOnlyList<TagRemediationItem> Worklist);

public sealed class TagGovernanceService
{
    private static readonly IReadOnlyDictionary<string, string> Synonyms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["prod"] = "production",
        ["prd"] = "production",
        ["production"] = "production",
        ["dev"] = "development",
        ["development"] = "development",
        ["stage"] = "staging",
        ["stg"] = "staging",
        ["ops"] = "platform",
        ["platform-engineering"] = "platform",
        ["fin-ops"] = "finops"
    };

    private readonly TagPolicy _policy;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _canonicalValues;

    public TagGovernanceService(TagPolicy? policy = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? canonicalValues = null)
    {
        _policy = policy ?? TagPolicy.Default;
        _canonicalValues = canonicalValues ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["team"] = ["atlas", "commerce", "data", "platform", "finops", "identity", "insights", "payments", "support"],
            ["environment"] = ["production", "staging", "development"],
            ["cost-centre"] = ["cc-100", "cc-200", "cc-300", "cc-400", "shared"],
            ["application"] = ["northstar-api", "order-hub", "data-lake", "ml-studio", "shared-platform"]
        };
    }

    public IReadOnlyDictionary<string, string> Normalize(IReadOnlyDictionary<string, string> input)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawKey, rawValue) in input)
        {
            var key = rawKey.Trim().ToLowerInvariant().Replace("_", "-");
            var value = rawValue.Trim().ToLowerInvariant().Replace("_", "-");
            if (Synonyms.TryGetValue(value, out var synonym)) value = synonym;

            if (_canonicalValues.TryGetValue(key, out var candidates) && candidates.Count > 0)
            {
                var closest = candidates
                    .Select(candidate => (candidate, distance: Levenshtein(value, candidate)))
                    .OrderBy(x => x.distance)
                    .ThenBy(x => x.candidate, StringComparer.Ordinal)
                    .First();
                if (closest.distance <= Math.Max(1, value.Length / 4))
                    value = closest.candidate;
            }

            normalized[key] = value;
        }
        return normalized;
    }

    public TagCoverageReport BuildCoverage(IEnumerable<CloudResource> resources)
    {
        var all = resources.ToList();
        var missingByTag = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var worklist = new List<TagRemediationItem>();
        var compliant = 0;

        foreach (var resource in all)
        {
            var tags = Normalize(resource.Tags);
            var required = _policy.RequiredTagsFor(resource);
            var missing = required.Where(tag => !tags.TryGetValue(tag, out var value) || string.IsNullOrWhiteSpace(value)).ToList();
            if (missing.Count == 0) compliant++;
            foreach (var tag in missing)
            {
                missingByTag[tag] = missingByTag.GetValueOrDefault(tag) + 1;
                var suggestion = tag switch
                {
                    "owner" => resource.Owner ?? "assign-owner",
                    "team" => SuggestTeam(resource),
                    "environment" => InferEnvironment(resource),
                    "cost-centre" => "assign-cost-centre",
                    "application" => "assign-application",
                    _ => "assign-value"
                };
                worklist.Add(new TagRemediationItem(resource.Id, resource.Name, tag, suggestion, $"Required by resource type policy '{resource.ResourceType}'."));
            }
        }

        return new TagCoverageReport(
            all.Count,
            compliant,
            all.Count == 0 ? 100m : decimal.Round(compliant * 100m / all.Count, 2),
            missingByTag,
            worklist.OrderBy(x => x.ResourceId, StringComparer.Ordinal).ThenBy(x => x.MissingTag, StringComparer.Ordinal).ToList());
    }

    private static string SuggestTeam(CloudResource resource) =>
        resource.ResourceGroup.Contains("data", StringComparison.OrdinalIgnoreCase) ? "data" :
        resource.ResourceGroup.Contains("shared", StringComparison.OrdinalIgnoreCase) ? "platform" :
        resource.ResourceGroup.Contains("commerce", StringComparison.OrdinalIgnoreCase) ? "commerce" : "assign-team";

    private static string InferEnvironment(CloudResource resource) =>
        resource.Name.Contains("prod", StringComparison.OrdinalIgnoreCase) ? "production" :
        resource.Name.Contains("stage", StringComparison.OrdinalIgnoreCase) ? "staging" : "development";

    private static int Levenshtein(string left, string right)
    {
        if (left == right) return 0;
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }
}
