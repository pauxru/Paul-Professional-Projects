using CloudCostObservability.Domain.Models;

namespace CloudCostObservability.Domain.Services;

public sealed class BudgetService
{
    private static readonly int[] Thresholds = [50, 80, 100, 120];

    public BudgetStatus Evaluate(BudgetDefinition budget, decimal spend)
    {
        if (spend < 0) throw new ArgumentOutOfRangeException(nameof(spend));
        var percent = decimal.Round(spend / budget.Amount * 100m, 2);
        var newlyCrossed = Thresholds
            .Where(threshold => percent >= threshold && !budget.AlertedThresholds.Contains(threshold))
            .ToList();
        if (newlyCrossed.Count > 0)
            budget.RecordAlertState(budget.AlertedThresholds.Concat(newlyCrossed));
        return new BudgetStatus(budget.Id, budget.Amount, spend, percent, newlyCrossed);
    }

    public static bool AppliesTo(BudgetDefinition budget, CloudResource resource)
    {
        return budget.Scope switch
        {
            BudgetScope.Team => resource.Tags.TryGetValue("team", out var team) && string.Equals(team, budget.Selector, StringComparison.OrdinalIgnoreCase),
            BudgetScope.Subscription => string.Equals(resource.SubscriptionId, budget.Selector, StringComparison.OrdinalIgnoreCase),
            BudgetScope.Environment => resource.Tags.TryGetValue("environment", out var environment) && string.Equals(environment, budget.Selector, StringComparison.OrdinalIgnoreCase),
            BudgetScope.Tag => resource.Tags.Any(tag => string.Equals($"{tag.Key}:{tag.Value}", budget.Selector, StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }
}

