using System.Globalization;
using CloudCostObservability.Application.Contracts;
using CloudCostObservability.Domain.Models;

namespace CloudCostObservability.Infrastructure.Ingestion;

public abstract class CsvCostDataSource(string path) : ICostDataSource
{
    protected string Path { get; } = path;
    public abstract ImportProvider Provider { get; }

    public async IAsyncEnumerable<CostImportLine> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(Path);
        var headerLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(headerLine)) yield break;
        var headers = ParseCsvLine(headerLine).Select(x => x.Trim()).ToList();
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = ParseCsvLine(line);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < headers.Count; index++)
                row[headers[index]] = index < cells.Count ? cells[index].Trim() : string.Empty;
            yield return Map(row);
        }
    }

    protected abstract CostImportLine Map(IReadOnlyDictionary<string, string> row);

    protected static string Value(IReadOnlyDictionary<string, string> row, params string[] keys) =>
        keys.Select(key => row.TryGetValue(key, out var value) ? value : string.Empty).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    protected static decimal DecimalValue(IReadOnlyDictionary<string, string> row, params string[] keys)
    {
        var raw = Value(row, keys);
        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0m;
    }

    protected static DateOnly DateValue(IReadOnlyDictionary<string, string> row, params string[] keys)
    {
        var raw = Value(row, keys);
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
            return DateOnly.FromDateTime(timestamp.UtcDateTime);
        if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        throw new FormatException($"Could not parse required usage date '{raw}'.");
    }

    protected static ResourceCategory Category(string service, string resourceType) =>
        $"{service} {resourceType}".ToLowerInvariant() switch
        {
            var value when value.Contains("virtual machine") || value.Contains("compute") || value.Contains("ec2") || value.Contains("vm") => ResourceCategory.Compute,
            var value when value.Contains("storage") || value.Contains("s3") || value.Contains("disk") || value.Contains("snapshot") => ResourceCategory.Storage,
            var value when value.Contains("sql") || value.Contains("database") || value.Contains("rds") => ResourceCategory.Database,
            var value when value.Contains("network") || value.Contains("bandwidth") || value.Contains("ip") || value.Contains("load balancer") => ResourceCategory.Networking,
            var value when value.Contains("function") || value.Contains("lambda") || value.Contains("serverless") => ResourceCategory.Serverless,
            var value when value.Contains("ai") || value.Contains("cognitive") || value.Contains("ml") => ResourceCategory.Ai,
            _ => ResourceCategory.Monitoring
        };

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else current.Append(character);
        }
        values.Add(current.ToString());
        return values;
    }
}

public sealed class AzureCostManagementCsvDataSource(string path) : CsvCostDataSource(path)
{
    public override ImportProvider Provider => ImportProvider.AzureCostManagementExport;

    protected override CostImportLine Map(IReadOnlyDictionary<string, string> row)
    {
        var date = DateValue(row, "Date", "UsageDate", "UsageDateTime");
        var resourceId = Value(row, "ResourceId", "Resource ID", "ResourceGuid");
        var meter = Value(row, "MeterName", "Meter", "MeterCategory");
        var service = Value(row, "ServiceName", "ConsumedService", "Service");
        var actual = DecimalValue(row, "CostInBillingCurrency", "Cost", "CostInUSD");
        var quantity = DecimalValue(row, "Quantity", "UsageQuantity");
        return new CostImportLine(
            Value(row, "BillingPeriodStartDate", "BillingPeriod", "InvoiceSection") is { Length: > 0 } billing
                ? ParseBillingPeriod(billing, date)
                : $"{date:yyyy-MM}",
            resourceId,
            date,
            meter,
            service,
            Category(service, Value(row, "ResourceType", "ResourceTypeName")),
            quantity,
            Value(row, "UnitOfMeasure", "Unit", "PricingQuantity") is { Length: > 0 } unit ? unit : "unit",
            DecimalValue(row, "EffectivePrice", "UnitPrice") is var rate && rate != 0m ? rate : (quantity == 0m ? 0m : actual / quantity),
            actual,
            DecimalValue(row, "AmortizedCost", "CostInBillingCurrency", "Cost"),
            DecimalValue(row, "Credit", "Credits"),
            DecimalValue(row, "Discount", "Discounts"),
            Value(row, "ReservationId", "ReservationOrderId").Length > 0 ? 100m : 0m,
            DecimalValue(row, "ReservationUtilizationPercent", "ReservationUtilization"),
            Value(row, "BillingCurrency", "Currency") is { Length: > 0 } currency ? currency : "USD");
    }

    private static string ParseBillingPeriod(string billing, DateOnly fallback) =>
        DateOnly.TryParse(billing, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? $"{parsed:yyyy-MM}" : billing.Length >= 7 ? billing[..7] : $"{fallback:yyyy-MM}";
}

public sealed class AwsCurCsvDataSource(string path) : CsvCostDataSource(path)
{
    public override ImportProvider Provider => ImportProvider.AwsCur;

    protected override CostImportLine Map(IReadOnlyDictionary<string, string> row)
    {
        var date = DateValue(row, "lineItem/UsageStartDate", "UsageStartDate", "identity/TimeInterval");
        var resourceId = Value(row, "lineItem/ResourceId", "ResourceId");
        var meter = Value(row, "lineItem/UsageType", "product/usagetype", "UsageType");
        var service = Value(row, "product/ProductName", "lineItem/ProductCode", "ProductName");
        var actual = DecimalValue(row, "lineItem/UnblendedCost", "lineItem/BlendedCost", "UnblendedCost");
        var quantity = DecimalValue(row, "lineItem/UsageAmount", "UsageAmount");
        var amortized = DecimalValue(row, "reservation/EffectiveCost", "savingsPlan/SavingsPlanEffectiveCost", "lineItem/UnblendedCost");
        return new CostImportLine(
            $"{date:yyyy-MM}",
            resourceId,
            date,
            meter,
            service,
            Category(service, Value(row, "product/productFamily", "lineItem/ProductCode")),
            quantity,
            Value(row, "pricing/unit", "Unit") is { Length: > 0 } unit ? unit : "unit",
            DecimalValue(row, "pricing/publicOnDemandRate", "lineItem/UnblendedRate") is var rate && rate != 0m ? rate : (quantity == 0m ? 0m : actual / quantity),
            actual,
            amortized == 0m ? actual : amortized,
            0m,
            DecimalValue(row, "discount/DiscountedCost", "lineItem/Discount") < 0m ? -DecimalValue(row, "discount/DiscountedCost", "lineItem/Discount") : 0m,
            Value(row, "reservation/ReservationARN", "savingsPlan/SavingsPlanARN").Length > 0 ? 100m : 0m,
            DecimalValue(row, "reservation/UnusedQuantity"),
            Value(row, "lineItem/CurrencyCode", "Currency") is { Length: > 0 } currency ? currency : "USD",
            Value(row, "lineItem/UsageStartDate").Contains('T'),
            ParseHour(Value(row, "lineItem/UsageStartDate")));
    }

    private static int? ParseHour(string raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value.Hour : null;
}

