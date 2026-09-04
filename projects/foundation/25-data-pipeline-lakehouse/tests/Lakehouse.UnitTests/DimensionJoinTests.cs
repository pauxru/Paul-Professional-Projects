using Lakehouse.Application.Pipelines;
using Lakehouse.Domain.Data;

namespace Lakehouse.UnitTests;

/// <summary>
/// The effective-version SCD2 join — the crux of a correct star build. A fact must join to the dimension
/// version whose [valid_from, valid_to) window contains the event time, not the current row. This is the
/// classic bug, isolated here as a pure-function test.
/// </summary>
public sealed class DimensionJoinTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Row V1 = Row.Of(("customer_sk", 1L), ("customer_id", "C1"),
        ("valid_from", T0), ("valid_to", (object?)T0.AddDays(10)), ("segment", "retail"));

    private static readonly Row V2 = Row.Of(("customer_sk", 2L), ("customer_id", "C1"),
        ("valid_from", T0.AddDays(10)), ("valid_to", (object?)null), ("segment", "vip"));

    private static readonly Row[] Versions = { V1, V2 };

    [Fact]
    public void Picks_version_effective_at_event_time_not_current()
    {
        var hit = DimensionResolver.Effective(Versions, "customer_id", "C1", T0.AddDays(3));
        Assert.NotNull(hit);
        Assert.Equal(1L, hit!.GetLong("customer_sk"));
        Assert.Equal("retail", hit.GetString("segment"));
    }

    [Fact]
    public void Picks_open_current_version_for_recent_event()
    {
        var hit = DimensionResolver.Effective(Versions, "customer_id", "C1", T0.AddDays(30));
        Assert.NotNull(hit);
        Assert.Equal(2L, hit!.GetLong("customer_sk"));
        Assert.Equal("vip", hit.GetString("segment"));
    }

    [Fact]
    public void Boundary_valid_to_is_exclusive()
    {
        // Exactly at the boundary belongs to the next version [valid_from, valid_to).
        var hit = DimensionResolver.Effective(Versions, "customer_id", "C1", T0.AddDays(10));
        Assert.Equal(2L, hit!.GetLong("customer_sk"));
    }

    [Fact]
    public void Event_before_any_version_returns_null()
    {
        var hit = DimensionResolver.Effective(Versions, "customer_id", "C1", T0.AddDays(-1));
        Assert.Null(hit);
    }

    [Fact]
    public void Unknown_business_key_returns_null()
    {
        var hit = DimensionResolver.Effective(Versions, "customer_id", "NOPE", T0.AddDays(3));
        Assert.Null(hit);
    }
}
