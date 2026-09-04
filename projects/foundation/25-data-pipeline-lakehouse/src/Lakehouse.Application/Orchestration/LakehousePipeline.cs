using System.Globalization;
using Lakehouse.Application.Abstractions;
using Lakehouse.Application.Model;
using Lakehouse.Application.Pipelines;
using Lakehouse.Application.Quality;
using Lakehouse.Domain.Cdc;
using Lakehouse.Domain.Quality;

namespace Lakehouse.Application.Orchestration;

/// <summary>
/// Wires the medallion transforms into an executable dependency DAG: bronze ingest (per entity) →
/// silver build → silver quality gate (circuit breaker) → gold dimensions → gold facts → aggregate
/// marts → gold quality gate. The silver gate sits between silver and every gold task, so a blocking
/// data-quality failure blocks promotion to gold in its entirety.
/// </summary>
public sealed class LakehousePipeline
{
    private const string BaselineKey = "dq:quarantine_baseline";

    private readonly ILakehouse _lake;
    private readonly ICheckpointStore _checkpoints;
    private readonly DataQualityRunner _dq;
    private readonly ISourceFeedProvider _feedProvider;
    private readonly BronzeIngestor _bronze;
    private readonly SilverBuilder _silver;
    private readonly GoldBuilder _gold;
    private IReadOnlyList<ChangeEvent>? _feed;

    public LakehousePipeline(ILakehouse lake, ICheckpointStore checkpoints, IClock clock,
        DataQualityRunner dq, ISourceFeedProvider feedProvider)
    {
        _lake = lake;
        _checkpoints = checkpoints;
        _dq = dq;
        _feedProvider = feedProvider;
        _bronze = new BronzeIngestor(lake, checkpoints, clock);
        _silver = new SilverBuilder(lake, clock);
        _gold = new GoldBuilder(lake);
    }

    /// <summary>The active source feed (loaded lazily). Override for scenarios like the demo's quality breach.</summary>
    public IReadOnlyList<ChangeEvent> Feed => _feed ??= _feedProvider.Load();
    public void UseFeed(IReadOnlyList<ChangeEvent> feed) => _feed = feed;

    public double QuarantineBaseline =>
        double.TryParse(_checkpoints.Get(BaselineKey), NumberStyles.Any, CultureInfo.InvariantCulture, out var b) ? b : 0;

    // ---- quality gates (also callable directly by the API) --------------------------------------

    public DataQualityReport RunSilverGate(string runId)
    {
        var report = _dq.Evaluate(QualitySuites.Silver(_lake, QuarantineBaseline), runId);
        CircuitBreaker.Assert(report); // throws on blocking failure → downstream gold is blocked
        var q = _lake.Table(Tables.Quarantine);
        _checkpoints.Set(BaselineKey, (q.Exists ? q.Scan().Count : 0).ToString(CultureInfo.InvariantCulture));
        return report;
    }

    public DataQualityReport RunGoldGate(string runId)
    {
        var report = _dq.Evaluate(QualitySuites.Gold(_lake), runId);
        CircuitBreaker.Assert(report);
        return report;
    }

    // ---- DAG ------------------------------------------------------------------------------------

    public Dag Build()
    {
        var dag = new Dag();

        // Bronze: one ingest task per source entity.
        foreach (var entity in LakehouseModel.SourceEntities)
        {
            var e = entity;
            dag.Add(B(e), ctx => IngestEntity(e, ctx), maxRetries: 2);
        }

        // Silver.
        dag.Add("silver_customers", ctx => _silver.BuildCustomers(ctx.RunId), 1, B(Tables.Customers));
        dag.Add("silver_products", ctx => _silver.BuildProducts(ctx.RunId), 1, B(Tables.Products));
        dag.Add("silver_fx", ctx => _silver.BuildFx(ctx.RunId), 1, B(Tables.Fx));
        dag.Add("silver_orders", ctx => _silver.BuildOrders(ctx.RunId), 1, B(Tables.Orders), B(Tables.Customers));
        dag.Add("silver_order_lines", ctx => _silver.BuildOrderLines(ctx.RunId), 1, B(Tables.OrderLines), "silver_orders", B(Tables.Products));
        dag.Add("silver_clickstream", ctx => _silver.BuildClickstream(ctx.RunId), 1, B(Tables.Clickstream));

        // Silver quality gate — the circuit breaker between silver and gold.
        dag.Add("dq_silver", ctx =>
        {
            var r = RunSilverGate(ctx.RunId);
            return new StepResult("dq:silver", r.Results.Count, r.Passed, r.Failed);
        }, 0, "silver_customers", "silver_products", "silver_orders", "silver_order_lines", "silver_clickstream", "silver_fx");

        // Gold dimensions (all gated behind dq_silver).
        dag.Add("dim_customer", ctx => _gold.BuildDimCustomer(), 1, "silver_customers", "dq_silver");
        dag.Add("dim_product", ctx => _gold.BuildDimProduct(), 1, "silver_products", "dq_silver");
        dag.Add("dim_date", ctx => _gold.BuildDimDate(), 1, "silver_orders", "silver_clickstream", "silver_fx", "dq_silver");
        dag.Add("dim_currency", ctx => _gold.BuildDimCurrency(), 1, "silver_fx", "dq_silver");
        dag.Add("dim_channel", ctx => _gold.BuildDimChannel(), 1, "silver_orders", "silver_clickstream", "dq_silver");

        // Gold facts.
        dag.Add("fact_order_line", ctx => _gold.BuildFactOrderLine(), 1,
            "dim_customer", "dim_product", "silver_order_lines", "silver_orders", "silver_fx");
        dag.Add("fact_clickstream_session", ctx => _gold.BuildFactClickstreamSession(), 1,
            "dim_customer", "silver_clickstream");

        // Aggregate marts.
        dag.Add("agg_daily_revenue", ctx => _gold.BuildAggDailyRevenue(), 1, "fact_order_line");
        dag.Add("agg_cohort_retention", ctx => _gold.BuildAggCohortRetention(), 1, "silver_orders", "dq_silver");
        dag.Add("agg_funnel", ctx => _gold.BuildAggFunnel(), 1, "fact_clickstream_session");
        dag.Add("agg_inventory_position", ctx => _gold.BuildAggInventoryPosition(), 1, B(Tables.Inventory), "dq_silver");

        // Gold quality gate.
        dag.Add("dq_gold", ctx =>
        {
            var r = RunGoldGate(ctx.RunId);
            return new StepResult("dq:gold", r.Results.Count, r.Passed, r.Failed);
        }, 0, "dim_customer", "dim_product", "dim_date", "fact_order_line", "fact_clickstream_session");

        return dag;
    }

    private static string B(string entity) => $"bronze_{entity}";

    private StepResult IngestEntity(string entity, RunContext ctx)
    {
        var window = ParseWindow(ctx.Window);
        return window is { } w
            ? _bronze.IngestWindow(Feed, entity, ctx.RunId, w.From, w.To)
            : _bronze.Ingest(Feed, entity, ctx.RunId);
    }

    /// <summary>Interpret a run window: "full" ⇒ incremental; "yyyy-MM", "yyyy-MM-dd" or "a..b" ⇒ backfill range.</summary>
    internal static (DateTimeOffset From, DateTimeOffset To)? ParseWindow(string window)
    {
        if (string.IsNullOrWhiteSpace(window) || window.Equals("full", StringComparison.OrdinalIgnoreCase))
            return null;

        if (window.Contains("..", StringComparison.Ordinal))
        {
            var parts = window.Split("..", StringSplitOptions.TrimEntries);
            var from = DateTimeOffset.Parse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var to = DateTimeOffset.Parse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).AddDays(1);
            return (from.ToUniversalTime(), to.ToUniversalTime());
        }

        if (window.Length == 7) // yyyy-MM
        {
            var month = DateTimeOffset.Parse(window + "-01", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
            return (month, month.AddMonths(1));
        }

        var day = DateTimeOffset.Parse(window, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
        return (day, day.AddDays(1));
    }
}
