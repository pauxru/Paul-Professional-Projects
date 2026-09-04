using Lakehouse.Application.Serving;

namespace Lakehouse.UnitTests;

/// <summary>
/// The serving SQL guard (defence in depth): only a single read-only SELECT/WITH statement is allowed;
/// DML/DDL, PRAGMA/ATTACH, statement batching and SQL comments are refused — the classic injection
/// vectors against a query API.
/// </summary>
public sealed class SqlGuardTests
{
    [Theory]
    [InlineData("SELECT * FROM agg_daily_revenue")]
    [InlineData("select date_key, revenue_usd from agg_daily_revenue where revenue_usd > 0")]
    [InlineData("WITH t AS (SELECT 1 AS x) SELECT x FROM t")]
    [InlineData("SELECT * FROM fact_order_line;")] // single trailing semicolon is tolerated
    public void Allows_read_only_select(string sql)
    {
        var normalised = SqlGuard.Validate(sql);
        Assert.False(normalised.EndsWith(';'));
    }

    [Theory]
    [InlineData("INSERT INTO dim_customer VALUES (1)")]
    [InlineData("UPDATE fact_order_line SET net_amount_usd = 0")]
    [InlineData("DELETE FROM dim_customer")]
    [InlineData("DROP TABLE dim_customer")]
    [InlineData("CREATE TABLE evil (x INT)")]
    [InlineData("ALTER TABLE dim_customer ADD COLUMN x INT")]
    [InlineData("PRAGMA table_info(dim_customer)")]
    [InlineData("ATTACH DATABASE 'x.db' AS y")]
    [InlineData("SELECT 1; DROP TABLE dim_customer")]         // statement batching
    [InlineData("SELECT 1 -- comment")]                        // line comment
    [InlineData("SELECT 1 /* block */")]                       // block comment
    [InlineData("VACUUM")]
    [InlineData("")]                                           // empty
    [InlineData("   ")]                                        // whitespace only
    [InlineData("EXPLAIN SELECT 1")]                           // not a SELECT/WITH start
    public void Rejects_unsafe_statements(string sql)
        => Assert.Throws<SqlGuardException>(() => SqlGuard.Validate(sql));
}
