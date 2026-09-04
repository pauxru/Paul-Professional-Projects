using Lakehouse.Application.Abstractions;
using Lakehouse.Application.Model;
using Lakehouse.Application.Pipelines;
using Lakehouse.Domain.Quality;

namespace Lakehouse.Application.Quality;

/// <summary>
/// The declarative expectation suites for the silver and gold gates. Expectations are data, not code
/// paths — adding a check is a one-line change here. Referential-integrity parent key sets are read
/// from the lake at build time so the checks reflect the data actually present.
/// </summary>
public static class QualitySuites
{
    /// <summary>
    /// The silver gate. Guards typed correctness, uniqueness of business/surrogate keys and referential
    /// integrity, plus a reject-volume anomaly check: if quarantine grows far beyond the rolling baseline
    /// the gate fails (Fail severity) and the circuit breaker blocks promotion to gold.
    /// </summary>
    public static QualityGate Silver(ILakehouse lake, double quarantineBaseline, double tolerance = 0.5)
    {
        var customerKeys = DistinctKeys(lake, Tables.SilverCustomers, "customer_id");
        var orderKeys = DistinctKeys(lake, Tables.SilverOrders, "order_id");

        return new QualityGate("silver", new[]
        {
            new DatasetExpectations(Tables.SilverCustomers, new Expectation[]
            {
                new UniqueExpectation(new[] { "surrogate_key" }),
                new NotNullExpectation("customer_id"),
                new NotNullExpectation("valid_from")
            }),
            new DatasetExpectations(Tables.SilverProducts, new Expectation[]
            {
                new UniqueExpectation(new[] { "surrogate_key" }),
                new NotNullExpectation("product_id")
            }),
            new DatasetExpectations(Tables.SilverOrders, new Expectation[]
            {
                new NotNullExpectation("order_id"),
                new NotNullExpectation("customer_id"),
                // Warn, not Fail: orders may reference a customer that was deleted or rejected from the
                // conformed dimension (a late-arriving/deleted member). Gold resolves these by inferring a
                // dimension member, so they must not block promotion — the blocking signal at silver is the
                // quarantine-volume anomaly below.
                new ReferentialIntegrityExpectation("customer_id", customerKeys, Severity.Warn)
            }),
            new DatasetExpectations(Tables.SilverOrderLines, new Expectation[]
            {
                new UniqueExpectation(new[] { "order_line_id" }),
                new AcceptedRangeExpectation("quantity", 1, null),
                new AcceptedRangeExpectation("net_amount", 0, null),
                new ReferentialIntegrityExpectation("order_id", orderKeys, Severity.Warn)
            }),
            new DatasetExpectations(Tables.SilverFx, new Expectation[]
            {
                new NotNullExpectation("rate_to_usd"),
                new AcceptedRangeExpectation("rate_to_usd", 0.0000001m, null)
            }),
            new DatasetExpectations(Tables.Quarantine, new Expectation[]
            {
                new RowCountAnomalyExpectation(quarantineBaseline, tolerance, Severity.Fail)
            })
        });
    }

    /// <summary>The gold gate: star-schema key uniqueness, non-null grains, positive revenue and fact→dim RI.</summary>
    public static QualityGate Gold(ILakehouse lake)
    {
        var customerSks = DistinctKeys(lake, Tables.DimCustomer, "customer_sk");
        var productSks = DistinctKeys(lake, Tables.DimProduct, "product_sk");
        var dateKeys = DistinctKeys(lake, Tables.DimDate, "date_key");

        return new QualityGate("gold", new[]
        {
            new DatasetExpectations(Tables.DimCustomer, new Expectation[]
            {
                new UniqueExpectation(new[] { "customer_sk" }),
                new NotNullExpectation("customer_id")
            }),
            new DatasetExpectations(Tables.DimProduct, new Expectation[]
            {
                new UniqueExpectation(new[] { "product_sk" })
            }),
            new DatasetExpectations(Tables.FactOrderLine, new Expectation[]
            {
                new UniqueExpectation(new[] { "order_line_id" }),
                new NotNullExpectation("order_line_id"),
                new AcceptedRangeExpectation("net_amount_usd", 0, null),
                new ReferentialIntegrityExpectation("customer_sk", customerSks),
                new ReferentialIntegrityExpectation("product_sk", productSks),
                new ReferentialIntegrityExpectation("order_date_key", dateKeys)
            }),
            new DatasetExpectations(Tables.FactClickstreamSession, new Expectation[]
            {
                new UniqueExpectation(new[] { "session_id" })
            })
        });
    }

    private static IReadOnlyCollection<string> DistinctKeys(ILakehouse lake, string table, string column)
    {
        var t = lake.Table(table);
        if (!t.Exists) return Array.Empty<string>();
        return t.Scan()
            .Select(r => r[column]?.ToString())
            .Where(v => !Parsing.IsBlank(v))
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
