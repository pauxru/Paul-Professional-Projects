using System.Runtime.CompilerServices;
using CloudCostObservability.Application.Contracts;
using CloudCostObservability.Domain.Models;

namespace CloudCostObservability.Infrastructure.Ingestion;

public sealed record InjectedAnomaly(string Name, DateOnly Date, string ResourceId, string Description);

public sealed class SyntheticFinOpsData
{
    public static readonly DateOnly DefaultStart = new(2024, 10, 1);
    public static readonly DateOnly DefaultEnd = new(2026, 8, 31);
    private static readonly string[] Teams = ["atlas", "commerce", "data", "platform", "finops", "identity", "insights", "payments", "support"];
    private static readonly string[] Subscriptions = ["ns-prod-core-001", "ns-data-002", "ns-digital-003", "ns-shared-004"];
    private static readonly string[] Regions = ["westeurope", "eastus", "uksouth", "southafricanorth"];

    public IReadOnlyList<CloudResource> GenerateResources(int count = 400)
    {
        var resources = new List<CloudResource>(count);
        for (var index = 0; index < count; index++)
        {
            var category = (ResourceCategory)(index % Enum.GetValues<ResourceCategory>().Length);
            var team = Teams[index % Teams.Length];
            var environment = index % 10 < 6 ? "production" : index % 10 < 8 ? "staging" : "development";
            var resourceGroup = category is ResourceCategory.Networking or ResourceCategory.Monitoring
                ? "shared-platform-rg"
                : $"{team}-{environment}-rg";
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["owner"] = $"owner-{team}",
                ["team"] = index % 29 == 0 ? "platfrom" : team,
                ["environment"] = environment == "production" && index % 31 == 0 ? "prd" : environment,
                ["cost-centre"] = category is ResourceCategory.Networking or ResourceCategory.Monitoring ? "shared" : $"cc-{100 + (index % 4) * 100}",
                ["application"] = team switch
                {
                    "data" => "data-lake",
                    "insights" => "ml-studio",
                    "platform" => "shared-platform",
                    _ => index % 2 == 0 ? "northstar-api" : "order-hub"
                }
            };
            if (index % 17 == 0) tags.Remove("team");
            if (index % 23 == 0) tags.Remove("application");
            if (index % 41 == 0) tags.Remove("owner");
            if (category is ResourceCategory.Networking or ResourceCategory.Monitoring)
                tags.Remove("team"); // deliberately untaggable shared services are handled by split rules
            var parent = category == ResourceCategory.Storage && index > 0 && index % 3 == 0 ? $"res-{index - 1:D3}" :
                category == ResourceCategory.Database && index > 2 && index % 4 == 0 ? $"res-{index - 2:D3}" : null;
            resources.Add(new CloudResource(
                $"res-{index:D3}",
                ResourceName(category, environment, index),
                ResourceType(category),
                category,
                Regions[index % Regions.Length],
                Subscriptions[index % Subscriptions.Length],
                resourceGroup,
                Sku(category, index),
                DefaultStart.AddDays(-(index % 180)),
                tags,
                tags.GetValueOrDefault("owner"),
                parent,
                index % 97 == 0 ? DefaultEnd.AddDays(-60) : null));
        }
        return resources;
    }

    public IReadOnlyList<InjectedAnomaly> KnownInjectedAnomalies => [
        new("step-change", DefaultStart.AddDays(420), "res-014", "Deliberate 2.1x step change in a compute workload."),
        new("runaway-resource", DefaultStart.AddDays(600), "res-031", "Runaway resource begins compounding daily spend."),
        new("gradual-drift", DefaultStart.AddDays(540), "res-059", "Gradual storage drift crosses material threshold."),
        new("month-end-batch", DefaultStart.AddDays(482), "res-002", "Known scheduled batch spike; expected seasonal event.")
    ];

    public ICostDataSource CreateCostSource(IReadOnlyList<CloudResource>? resources = null, DateOnly? start = null, DateOnly? end = null) =>
        new SyntheticCostDataSource(this, resources ?? GenerateResources(), start ?? DefaultStart, end ?? DefaultEnd);

    internal async IAsyncEnumerable<CostImportLine> GenerateCostsAsync(
        IReadOnlyList<CloudResource> resources,
        DateOnly start,
        DateOnly end,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var totalDays = end.DayNumber - start.DayNumber + 1;
        for (var resourceIndex = 0; resourceIndex < resources.Count; resourceIndex++)
        {
            var resource = resources[resourceIndex];
            var resourceOrdinal = int.TryParse(resource.Id.AsSpan(4), out var parsedOrdinal) ? parsedOrdinal : resourceIndex;
            var baseDailyCost = BaseDailyCost(resource.Category, resourceOrdinal);
            for (var dayIndex = 0; dayIndex < totalDays; dayIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var date = start.AddDays(dayIndex);
                if (!resource.IsActiveOn(date)) continue;
                var seasonal = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? .76m : 1.11m;
                var monthEnd = date.Day >= 27 && resourceOrdinal % 11 == 2 ? 1.34m : 1m;
                var growth = 1m + dayIndex * .00023m;
                var deterministicNoise = 1m + ((resourceOrdinal * 17 + dayIndex * 7) % 13 - 6) / 300m;
                var anomaly = AnomalyMultiplier(resourceOrdinal, dayIndex);
                var actual = Round(baseDailyCost * seasonal * monthEnd * growth * deterministicNoise * anomaly);
                var usage = UsageQuantity(resource.Category, resourceOrdinal, dayIndex);
                var reservedCoverage = resource.Category == ResourceCategory.Compute && resourceOrdinal % 3 == 0 ? 75m : 0m;
                var amortized = Round(actual * (reservedCoverage > 0 ? .92m : 1m));
                var credits = date.Day == 1 && resourceIndex % 71 == 0 ? Round(actual * .05m) : 0m;
                yield return new CostImportLine(
                    $"{date:yyyy-MM}",
                    resource.Id,
                    date,
                    Meter(resource.Category),
                    Service(resource.Category),
                    resource.Category,
                    usage,
                    Unit(resource.Category),
                    usage == 0m ? 0m : Round(actual / usage),
                    actual,
                    amortized,
                    credits,
                    0m,
                    reservedCoverage,
                    reservedCoverage == 0 ? 0m : 88m,
                    "USD");

                // A small high-frequency subset makes the model exercise hour grain without replacing daily billing lines.
                if (resourceOrdinal < 8 && dayIndex >= totalDays - 45)
                {
                    for (var hour = 0; hour < 24; hour++)
                    {
                        var hourlyCost = Round(actual * (hour is >= 8 and <= 18 ? .0028m : .0014m));
                        yield return new CostImportLine(
                            $"{date:yyyy-MM}",
                            resource.Id,
                            date,
                            $"{Meter(resource.Category)}-hour-{hour:D2}",
                            Service(resource.Category),
                            resource.Category,
                            1m,
                            "hour",
                            hourlyCost,
                            hourlyCost,
                            hourlyCost,
                            0m,
                            0m,
                            reservedCoverage,
                            reservedCoverage == 0 ? 0m : 88m,
                            "USD",
                            true,
                            hour);
                    }
                }
            }
            if (resourceIndex % 25 == 0) await Task.Yield();
        }
    }

    public IReadOnlyList<BusinessMetric> GenerateBusinessMetrics(DateOnly start, DateOnly end)
    {
        var metrics = new List<BusinessMetric>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            for (var index = 0; index < Teams.Length; index++)
            {
                var weekdayFactor = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? .55m : 1m;
                var orders = (int)(350 * weekdayFactor + index * 19 + day.Day % 17);
                metrics.Add(new BusinessMetric(day, Teams[index], orders, 30 + index * 7, 700m * weekdayFactor + index * 31));
            }
        }
        return metrics;
    }

    private static decimal AnomalyMultiplier(int resourceIndex, int dayIndex)
    {
        if (resourceIndex == 14 && dayIndex >= 420) return 2.1m;
        if (resourceIndex == 31 && dayIndex >= 600) return 1m + (dayIndex - 600) * .045m;
        if (resourceIndex == 59 && dayIndex >= 360) return 1m + (dayIndex - 360) * .005m;
        // One recognised month-end job intentionally remains seasonal and should not be treated as a surprise.
        return 1m;
    }

    private static decimal BaseDailyCost(ResourceCategory category, int index) => category switch
    {
        ResourceCategory.Compute => 18m + index % 9,
        ResourceCategory.Storage => 5m + index % 7,
        ResourceCategory.Database => 24m + index % 8,
        ResourceCategory.Networking => 7m + index % 5,
        ResourceCategory.Serverless => 4m + index % 6,
        ResourceCategory.Ai => 28m + index % 11,
        _ => 3m + index % 4
    };

    private static decimal UsageQuantity(ResourceCategory category, int resourceIndex, int dayIndex) => category switch
    {
        ResourceCategory.Compute => 24m,
        ResourceCategory.Storage => 100m + resourceIndex % 100,
        ResourceCategory.Database => 24m,
        ResourceCategory.Networking => 120m + dayIndex % 80,
        ResourceCategory.Serverless => 1_000m + resourceIndex * 5,
        ResourceCategory.Ai => 10_000m + dayIndex * 12,
        _ => 24m
    };

    private static string ResourceName(ResourceCategory category, string environment, int index) => $"{category.ToString().ToLowerInvariant()}-{environment}-{index:D3}";
    private static string ResourceType(ResourceCategory category) => category switch
    {
        ResourceCategory.Compute => "Microsoft.Compute/virtualMachines",
        ResourceCategory.Storage => "Microsoft.Compute/disks",
        ResourceCategory.Database => "Microsoft.Sql/servers/databases",
        ResourceCategory.Networking => "Microsoft.Network/loadBalancers",
        ResourceCategory.Serverless => "Microsoft.Web/sites/functions",
        ResourceCategory.Ai => "Microsoft.CognitiveServices/accounts",
        _ => "Microsoft.Insights/components"
    };
    private static string Sku(ResourceCategory category, int index) => category switch
    {
        ResourceCategory.Compute => index % 2 == 0 ? "D4s_v5" : "D8s_v5",
        ResourceCategory.Storage => "Premium_LRS",
        ResourceCategory.Database => "GP_Gen5_4",
        ResourceCategory.Networking => "Standard",
        ResourceCategory.Serverless => "ElasticPremium",
        ResourceCategory.Ai => "S0",
        _ => "PayAsYouGo"
    };
    private static string Meter(ResourceCategory category) => category switch
    {
        ResourceCategory.Compute => "Compute Hours",
        ResourceCategory.Storage => "Storage GB-Month",
        ResourceCategory.Database => "Database vCore Hours",
        ResourceCategory.Networking => "Data Transfer",
        ResourceCategory.Serverless => "Function Executions",
        ResourceCategory.Ai => "AI Tokens",
        _ => "Monitoring Data"
    };
    private static string Service(ResourceCategory category) => category switch
    {
        ResourceCategory.Compute => "Compute",
        ResourceCategory.Storage => "Storage",
        ResourceCategory.Database => "Database",
        ResourceCategory.Networking => "Networking",
        ResourceCategory.Serverless => "Serverless",
        ResourceCategory.Ai => "AI Services",
        _ => "Monitoring"
    };
    private static string Unit(ResourceCategory category) => category switch
    {
        ResourceCategory.Compute or ResourceCategory.Database => "hour",
        ResourceCategory.Storage => "GB-month",
        ResourceCategory.Networking => "GB",
        ResourceCategory.Serverless => "execution",
        ResourceCategory.Ai => "1K tokens",
        _ => "GB"
    };
    private static decimal Round(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}

public sealed class SyntheticCostDataSource(SyntheticFinOpsData data, IReadOnlyList<CloudResource> resources, DateOnly start, DateOnly end) : ICostDataSource
{
    public ImportProvider Provider => ImportProvider.Synthetic;
    public IAsyncEnumerable<CostImportLine> ReadAsync(CancellationToken cancellationToken = default) =>
        data.GenerateCostsAsync(resources, start, end, cancellationToken);
}
