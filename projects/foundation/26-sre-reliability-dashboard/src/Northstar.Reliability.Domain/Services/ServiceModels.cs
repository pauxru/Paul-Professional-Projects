using Northstar.Reliability.Domain.Common;

namespace Northstar.Reliability.Domain.Services;

public enum CriticalityTier
{
    Tier0,
    Tier1,
    Tier2,
    Tier3
}

public sealed record ServiceDefinition(
    Guid Id,
    string Slug,
    string Name,
    CriticalityTier Tier,
    string OwningTeam,
    string OnCallRotation,
    string RepositoryUrl,
    string? RunbookUrl,
    IReadOnlyList<string> Dependencies,
    DateTimeOffset CreatedAt)
{
    public static ServiceDefinition Create(
        string slug,
        string name,
        CriticalityTier tier,
        string owningTeam,
        string onCallRotation,
        string repositoryUrl,
        string? runbookUrl,
        IEnumerable<string>? dependencies,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(slug) || !SlugIsValid(slug))
        {
            throw new DomainRuleViolationException("Service slug must contain lowercase letters, digits, and hyphens.");
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(owningTeam) ||
            string.IsNullOrWhiteSpace(onCallRotation))
        {
            throw new DomainRuleViolationException("Name, owning team, and on-call rotation are required.");
        }

        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out _))
        {
            throw new DomainRuleViolationException("Repository URL must be absolute.");
        }

        if (!string.IsNullOrWhiteSpace(runbookUrl) && !Uri.TryCreate(runbookUrl, UriKind.Absolute, out _))
        {
            throw new DomainRuleViolationException("Runbook URL must be absolute.");
        }

        var dependencyList = dependencies?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        if (dependencyList.Contains(slug, StringComparer.OrdinalIgnoreCase))
        {
            throw new DomainRuleViolationException("A service cannot depend on itself.");
        }

        return new ServiceDefinition(
            Guid.NewGuid(),
            slug.Trim().ToLowerInvariant(),
            name.Trim(),
            tier,
            owningTeam.Trim(),
            onCallRotation.Trim(),
            repositoryUrl.Trim(),
            string.IsNullOrWhiteSpace(runbookUrl) ? null : runbookUrl.Trim(),
            dependencyList,
            now);
    }

    private static bool SlugIsValid(string value) =>
        value.All(character => char.IsLower(character) || char.IsDigit(character) || character == '-');
}

public static class ServiceDependencyGraph
{
    public static bool HasCycle(IEnumerable<ServiceDefinition> services)
    {
        var graph = services.ToDictionary(
            service => service.Slug,
            service => service.Dependencies,
            StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in graph.Keys)
        {
            if (Visit(node, graph, visiting, visited))
            {
                return true;
            }
        }

        return false;
    }

    public static void EnsureAcyclic(IEnumerable<ServiceDefinition> services)
    {
        if (HasCycle(services))
        {
            throw new DomainRuleViolationException("Service dependency graph contains a cycle.");
        }
    }

    private static bool Visit(
        string node,
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph,
        ISet<string> visiting,
        ISet<string> visited)
    {
        if (visited.Contains(node))
        {
            return false;
        }

        if (!visiting.Add(node))
        {
            return true;
        }

        if (graph.TryGetValue(node, out var dependencies))
        {
            foreach (var dependency in dependencies)
            {
                if (graph.ContainsKey(dependency) && Visit(dependency, graph, visiting, visited))
                {
                    return true;
                }
            }
        }

        visiting.Remove(node);
        visited.Add(node);
        return false;
    }
}

public enum DependencyRisk
{
    Healthy,
    Elevated,
    Critical
}

public sealed record ServiceMaturityScorecard(
    string ServiceSlug,
    bool HasSlo,
    bool HasRunbook,
    bool HasRecentPostmortem,
    bool ErrorBudgetHealthy,
    DependencyRisk DependencyRisk,
    int Score,
    IReadOnlyList<string> Recommendations);

public static class ServiceScorecardCalculator
{
    public static ServiceMaturityScorecard Calculate(
        ServiceDefinition service,
        bool hasSlo,
        bool hasRecentPostmortem,
        decimal remainingBudgetPercent,
        DependencyRisk dependencyRisk)
    {
        var hasRunbook = !string.IsNullOrWhiteSpace(service.RunbookUrl);
        var budgetHealthy = remainingBudgetPercent >= 50m;
        var score = (hasSlo ? 25 : 0)
            + (hasRunbook ? 20 : 0)
            + (hasRecentPostmortem ? 15 : 0)
            + (budgetHealthy ? 25 : 0)
            + (dependencyRisk == DependencyRisk.Healthy ? 15 : dependencyRisk == DependencyRisk.Elevated ? 7 : 0);

        var recommendations = new List<string>();
        if (!hasSlo) recommendations.Add("Define at least one customer-journey SLO.");
        if (!hasRunbook) recommendations.Add("Link an actionable operational runbook.");
        if (!hasRecentPostmortem) recommendations.Add("Review recent learning actions or record a postmortem.");
        if (!budgetHealthy) recommendations.Add("Stabilize reliability before accepting risky change.");
        if (dependencyRisk != DependencyRisk.Healthy) recommendations.Add("Review upstream dependency resilience and ownership.");

        return new ServiceMaturityScorecard(
            service.Slug,
            hasSlo,
            hasRunbook,
            hasRecentPostmortem,
            budgetHealthy,
            dependencyRisk,
            score,
            recommendations);
    }
}
