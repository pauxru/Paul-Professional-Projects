using CloudCostObservability.Domain.Models;

namespace CloudCostObservability.UnitTests;

internal static class TestData
{
    public static CloudResource Resource(
        string id = "r1",
        string team = "commerce",
        ResourceCategory category = ResourceCategory.Compute,
        string? parent = null,
        string resourceGroup = "commerce-production-rg",
        IReadOnlyDictionary<string, string>? tags = null) =>
        new(id, $"resource-{id}", "Contoso/type", category, "westeurope", "sub-1", resourceGroup, "D4s", new DateOnly(2026, 1, 1),
            tags ?? new Dictionary<string, string>
            {
                ["owner"] = "owner-commerce",
                ["team"] = team,
                ["environment"] = "production",
                ["cost-centre"] = "cc-200",
                ["application"] = "order-hub"
            },
            "owner-commerce", parent);

    public static CostRecord Cost(
        string resourceId = "r1",
        decimal actual = 100m,
        decimal amortized = 100m,
        decimal usage = 10m,
        DateOnly? date = null,
        string meter = "Compute Hours") =>
        new("2026-01", resourceId, date ?? new DateOnly(2026, 1, 15), meter, "Compute", ResourceCategory.Compute, usage, "hour", actual / usage, actual, amortized);
}

