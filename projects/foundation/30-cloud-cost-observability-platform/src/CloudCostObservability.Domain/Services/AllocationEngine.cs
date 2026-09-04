using CloudCostObservability.Domain.Models;

namespace CloudCostObservability.Domain.Services;

public sealed class AllocationEngine
{
    private readonly TagGovernanceService _tagGovernance;

    public AllocationEngine(TagGovernanceService? tagGovernance = null)
    {
        _tagGovernance = tagGovernance ?? new TagGovernanceService();
    }

    public IReadOnlyList<AllocationAudit> Allocate(
        IEnumerable<CostRecord> costs,
        IEnumerable<CloudResource> resources,
        IEnumerable<AllocationRule> rules,
        CostBasis basis = CostBasis.Amortized,
        IReadOnlyDictionary<string, decimal>? teamUsageWeights = null)
    {
        var resourceById = resources.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var orderedRules = rules.Where(x => x.Enabled).OrderBy(x => x.Order).ToList();
        var input = costs.ToList();
        var derivedWeights = teamUsageWeights ?? DeriveUsageWeights(input, resourceById);
        var audits = new List<AllocationAudit>(input.Count);

        foreach (var cost in input)
        {
            var sourceAmount = cost.Cost(basis);
            resourceById.TryGetValue(cost.ResourceId, out var resource);
            var lines = AllocateOne(cost, resource, resourceById, orderedRules, sourceAmount, derivedWeights);
            var allocated = lines.Sum(x => x.Amount);
            var unallocated = sourceAmount - allocated;
            if (unallocated != 0m)
            {
                lines.Add(new AllocationLine(
                    cost.Id,
                    cost.UsageDate,
                    "unallocated",
                    "unallocated",
                    unallocated,
                    AllocationMethod.Unallocated,
                    "No allocation rule matched; retained in the explicit residual bucket."));
            }

            var audit = new AllocationAudit(cost.Id, sourceAmount, lines.Where(x => x.Method != AllocationMethod.Unallocated).Sum(x => x.Amount), lines.Where(x => x.Method == AllocationMethod.Unallocated).Sum(x => x.Amount), lines);
            AssertInvariant(audit);
            audits.Add(audit);
        }
        return audits;
    }

    public IReadOnlyDictionary<string, decimal> RollUpCostsToParents(IEnumerable<CostRecord> costs, IEnumerable<CloudResource> resources, CostBasis basis = CostBasis.Amortized)
    {
        var byId = resources.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var cost in costs)
        {
            var owner = ResolveRootResource(cost.ResourceId, byId);
            result[owner] = result.GetValueOrDefault(owner) + cost.Cost(basis);
        }
        return result;
    }

    public static void AssertInvariant(AllocationAudit audit)
    {
        if (audit.SourceAmount != audit.AllocatedAmount + audit.UnallocatedAmount)
            throw new InvalidOperationException($"Allocation invariant failed for {audit.CostRecordId}: {audit.SourceAmount} != {audit.AllocatedAmount} + {audit.UnallocatedAmount}.");
        if (audit.Lines.Sum(x => x.Amount) != audit.SourceAmount)
            throw new InvalidOperationException($"Allocation lines do not reconcile for {audit.CostRecordId}.");
    }

    private List<AllocationLine> AllocateOne(
        CostRecord cost,
        CloudResource? resource,
        IReadOnlyDictionary<string, CloudResource> resources,
        IReadOnlyList<AllocationRule> rules,
        decimal sourceAmount,
        IReadOnlyDictionary<string, decimal> weights)
    {
        foreach (var rule in rules)
        {
            switch (rule.Method)
            {
                case AllocationMethod.DirectTag when resource is not null && TryDirectTag(resource, rule, out var directTeam, out var directCostCentre):
                    return [Line(cost, directTeam, directCostCentre, sourceAmount, rule.Method, $"Direct tag '{rule.MatchKey ?? "team"}' on resource {resource.Id}.", rule)];

                case AllocationMethod.ResourceGroupMapping when resource is not null && MatchesResource(rule, resource):
                    return [Line(cost, rule.TargetTeam ?? "unallocated", rule.TargetCostCentre ?? "unallocated", sourceAmount, rule.Method, $"Mapped {rule.MatchKey ?? "resourceGroup"} '{rule.MatchValue}' by ordered rule {rule.Order}.", rule)];

                case AllocationMethod.ParentInheritance when resource is not null && TryParentTags(resource, resources, out var parent, out var parentTeam, out var parentCostCentre):
                    return [Line(cost, parentTeam, parentCostCentre, sourceAmount, rule.Method, $"Inherited ownership from parent resource {parent.Id}.", rule)];

                case AllocationMethod.SharedProportional or AllocationMethod.SharedEven or AllocationMethod.SharedFixed when resource is not null && MatchesResource(rule, resource):
                    return SplitShared(cost, resource, sourceAmount, rule, weights);
            }
        }
        return [];
    }

    private static AllocationLine Line(CostRecord cost, string team, string costCentre, decimal amount, AllocationMethod method, string explanation, AllocationRule rule) =>
        new(cost.Id, cost.UsageDate, team, costCentre, amount, method, explanation, $"rule-{rule.Order}");

    private bool TryDirectTag(CloudResource resource, AllocationRule rule, out string team, out string costCentre)
    {
        var tags = _tagGovernance.Normalize(resource.Tags);
        var teamKey = rule.MatchKey ?? "team";
        if (!tags.TryGetValue(teamKey, out var taggedTeam) || string.IsNullOrWhiteSpace(taggedTeam))
        {
            team = costCentre = string.Empty;
            return false;
        }
        team = rule.TargetTeam ?? taggedTeam.Trim().ToLowerInvariant();
        costCentre = rule.TargetCostCentre ??
            (tags.TryGetValue("cost-centre", out var taggedCostCentre) && !string.IsNullOrWhiteSpace(taggedCostCentre)
                ? taggedCostCentre.Trim().ToLowerInvariant()
                : "unassigned-cost-centre");
        return true;
    }

    private bool TryParentTags(CloudResource resource, IReadOnlyDictionary<string, CloudResource> resources, out CloudResource parent, out string team, out string costCentre)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parentId = resource.ParentResourceId;
        while (!string.IsNullOrWhiteSpace(parentId) && seen.Add(parentId) && resources.TryGetValue(parentId, out parent!))
        {
            var tags = _tagGovernance.Normalize(parent.Tags);
            if (tags.TryGetValue("team", out var taggedTeam) && !string.IsNullOrWhiteSpace(taggedTeam))
            {
                team = taggedTeam.Trim().ToLowerInvariant();
                costCentre = tags.TryGetValue("cost-centre", out var cc) && !string.IsNullOrWhiteSpace(cc)
                    ? cc.Trim().ToLowerInvariant()
                    : "unassigned-cost-centre";
                return true;
            }
            parentId = parent.ParentResourceId;
        }
        parent = null!;
        team = costCentre = string.Empty;
        return false;
    }

    private static bool MatchesResource(AllocationRule rule, CloudResource resource)
    {
        var key = (rule.MatchKey ?? "resourceGroup").Trim().ToLowerInvariant();
        var value = rule.MatchValue ?? string.Empty;
        object actual = key switch
        {
            "resourcegroup" or "resource-group" => resource.ResourceGroup,
            "subscription" or "subscriptionid" => resource.SubscriptionId,
            "category" => resource.Category.ToString(),
            "resourceid" or "resource-id" => resource.Id,
            "shared" => resource.Category is ResourceCategory.Networking or ResourceCategory.Monitoring,
            _ => string.Empty
        };
        return actual is bool shared
            ? shared
            : string.Equals(actual as string, value, StringComparison.OrdinalIgnoreCase);
    }

    private static List<AllocationLine> SplitShared(CostRecord cost, CloudResource resource, decimal sourceAmount, AllocationRule rule, IReadOnlyDictionary<string, decimal> weights)
    {
        var targets = rule.FixedPercentages?.Keys.ToList() ?? weights.Where(x => x.Value > 0).Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (targets.Count == 0) return [];

        var shares = new List<(string team, decimal share)>();
        switch (rule.Method)
        {
            case AllocationMethod.SharedFixed:
                var totalPercent = rule.FixedPercentages!.Values.Sum();
                if (totalPercent > 100m) throw new InvalidOperationException($"Fixed percentages in rule {rule.Order} exceed 100%.");
                shares.AddRange(targets.Select(team => (team, sourceAmount * (rule.FixedPercentages!.GetValueOrDefault(team) / 100m))));
                break;
            case AllocationMethod.SharedEven:
                var even = sourceAmount / targets.Count;
                shares.AddRange(targets.Select(team => (team, even)));
                break;
            default:
                var totalWeight = targets.Sum(x => weights.GetValueOrDefault(x));
                if (totalWeight <= 0) return [];
                shares.AddRange(targets.Select(team => (team, sourceAmount * weights.GetValueOrDefault(team) / totalWeight)));
                break;
        }

        // Preserve every fractional decimal exactly for full splits; incomplete fixed schedules retain an explicit residual.
        var lines = new List<AllocationLine>(shares.Count);
        var allocated = 0m;
        for (var index = 0; index < shares.Count; index++)
        {
            var (team, intendedShare) = shares[index];
            var share = rule.Method != AllocationMethod.SharedFixed && index == shares.Count - 1
                ? sourceAmount - allocated
                : intendedShare;
            allocated += share;
            var strategy = rule.Method switch
            {
                AllocationMethod.SharedProportional => "proportional usage",
                AllocationMethod.SharedEven => "even split",
                _ => "fixed percentage"
            };
            lines.Add(Line(cost, team, "shared", share, rule.Method, $"Shared {resource.Category} cost split using {strategy} under rule {rule.Order}.", rule));
        }
        return lines;
    }

    private IReadOnlyDictionary<string, decimal> DeriveUsageWeights(IEnumerable<CostRecord> costs, IReadOnlyDictionary<string, CloudResource> resources)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var cost in costs)
        {
            if (!resources.TryGetValue(cost.ResourceId, out var resource)) continue;
            var tags = _tagGovernance.Normalize(resource.Tags);
            if (!tags.TryGetValue("team", out var team) || string.IsNullOrWhiteSpace(team)) continue;
            result[team.Trim().ToLowerInvariant()] = result.GetValueOrDefault(team.Trim().ToLowerInvariant()) + cost.UsageQuantity;
        }
        return result;
    }

    private static string ResolveRootResource(string resourceId, IReadOnlyDictionary<string, CloudResource> resources)
    {
        var current = resourceId;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (seen.Add(current) && resources.TryGetValue(current, out var resource) && !string.IsNullOrWhiteSpace(resource.ParentResourceId))
            current = resource.ParentResourceId;
        return current;
    }
}
