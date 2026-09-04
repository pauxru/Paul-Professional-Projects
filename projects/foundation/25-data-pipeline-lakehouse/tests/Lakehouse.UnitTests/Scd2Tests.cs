using Lakehouse.Domain.Data;
using Lakehouse.Domain.Scd;

namespace Lakehouse.UnitTests;

/// <summary>
/// SCD Type 2 construction from a change feed. The build re-sorts the whole event log, so out-of-order
/// and late-arriving updates are correct by construction, surrogate keys are deterministic, and deletes
/// close the open version.
/// </summary>
public sealed class Scd2Tests
{
    private static readonly string[] Tracked = { "name" };
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DimChange Change(long seq, DateTimeOffset at, string? name, bool delete = false)
        => new("C1", at, seq, name is null ? null : Row.Of(("name", name)), delete);

    [Fact]
    public void Builds_valid_from_to_and_is_current()
    {
        var versions = Scd2Processor.Build(new[]
        {
            Change(1, T0, "Ann"),
            Change(2, T0.AddDays(10), "Annabel")
        }, Tracked);

        Assert.Equal(2, versions.Count);
        var v1 = versions[0];
        var v2 = versions[1];
        Assert.Equal("Ann", v1.Attributes.GetString("name"));
        Assert.Equal(T0, v1.ValidFrom);
        Assert.Equal(T0.AddDays(10), v1.ValidTo);
        Assert.False(v1.IsCurrent);
        Assert.Equal("Annabel", v2.Attributes.GetString("name"));
        Assert.Null(v2.ValidTo);
        Assert.True(v2.IsCurrent);
    }

    [Fact]
    public void Out_of_order_changes_produce_correct_chronology()
    {
        // Provided newest-first; the processor must still order versions by effective time.
        var versions = Scd2Processor.Build(new[]
        {
            Change(2, T0.AddDays(10), "Annabel"),
            Change(1, T0, "Ann")
        }, Tracked);

        Assert.Equal("Ann", versions[0].Attributes.GetString("name"));
        Assert.Equal("Annabel", versions[1].Attributes.GetString("name"));
        Assert.True(versions[0].ValidTo == versions[1].ValidFrom);
    }

    [Fact]
    public void Same_instant_highest_sequence_wins()
    {
        var versions = Scd2Processor.Build(new[]
        {
            Change(1, T0, "First"),
            Change(2, T0, "Winner")
        }, Tracked);

        Assert.Single(versions);
        Assert.Equal("Winner", versions[0].Attributes.GetString("name"));
    }

    [Fact]
    public void No_material_change_is_deduplicated()
    {
        var versions = Scd2Processor.Build(new[]
        {
            Change(1, T0, "Ann"),
            Change(2, T0.AddDays(5), "Ann") // same tracked attributes -> no new version
        }, Tracked);

        Assert.Single(versions);
        Assert.True(versions[0].IsCurrent);
    }

    [Fact]
    public void Delete_closes_open_version_leaving_no_current()
    {
        var versions = Scd2Processor.Build(new[]
        {
            Change(1, T0, "Ann"),
            Change(2, T0.AddDays(3), null, delete: true)
        }, Tracked);

        Assert.Single(versions);
        Assert.False(versions[0].IsCurrent);
        Assert.Equal(T0.AddDays(3), versions[0].ValidTo);
    }

    [Fact]
    public void Surrogate_keys_are_stable_across_rebuilds()
    {
        var input = new[] { Change(1, T0, "Ann"), Change(2, T0.AddDays(10), "Annabel") };
        var a = Scd2Processor.Build(input, Tracked);
        var b = Scd2Processor.Build(input, Tracked);

        Assert.Equal(a.Select(v => v.SurrogateKey), b.Select(v => v.SurrogateKey));
        Assert.All(a, v => Assert.True(v.SurrogateKey > 0));
    }
}
