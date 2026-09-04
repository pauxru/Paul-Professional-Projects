using Sagas.Core;

namespace Sagas.Tests;

/// <summary>
/// The state vector underneath everything. It is immutable, it is compared by
/// value, and it caches its hash -- which means a bug here would silently
/// collapse or explode the state space rather than throwing, so it gets tested
/// more carefully than its size suggests.
/// </summary>
public class WorldStateTests
{
    private static readonly Schema S = new("stock", "reserved", "slot");

    [Fact]
    public void SchemaAssignsStableIndices()
    {
        Assert.Equal(0, S.IndexOf("stock"));
        Assert.Equal(1, S.IndexOf("reserved"));
        Assert.Equal(2, S.IndexOf("slot"));
        Assert.Equal(3, S.Count);
    }

    [Fact]
    public void UnknownVariableIsRejectedRatherThanDefaulted()
    {
        var ex = Assert.Throws<KeyNotFoundException>(() => S.IndexOf("nonexistent"));
        Assert.Contains("nonexistent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownVariableInStateBuilderIsRejected()
    {
        Assert.Throws<KeyNotFoundException>(() => S.State(("nope", 1)));
    }

    [Fact]
    public void UnsetVariablesAreZero()
    {
        var s = S.State(("stock", 5));
        Assert.Equal(5, s["stock"]);
        Assert.Equal(0, s["reserved"]);
        Assert.Equal(0, s["slot"]);
    }

    [Fact]
    public void EqualityIsByValueNotReference()
    {
        var a = S.State(("stock", 5), ("reserved", 1));
        var b = S.State(("reserved", 1), ("stock", 5));
        Assert.NotSame(a, b);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void DifferentValuesAreNotEqual()
    {
        Assert.NotEqual(S.State(("stock", 5)), S.State(("stock", 4)));
    }

    [Fact]
    public void StatesFromDifferentSchemasAreNotEqual()
    {
        var other = new Schema("stock", "reserved", "slot", "extra");
        Assert.NotEqual(S.State(("stock", 1)), other.State(("stock", 1)));
    }

    [Fact]
    public void WithDoesNotMutateTheOriginal()
    {
        var a = S.State(("stock", 5));
        var b = a.With("stock", 4);
        Assert.Equal(5, a["stock"]);
        Assert.Equal(4, b["stock"]);
    }

    [Fact]
    public void WithReturnsTheSameInstanceWhenNothingChanges()
    {
        var a = S.State(("stock", 5));
        Assert.Same(a, a.With("stock", 5));
    }

    [Fact]
    public void AddIsShorthandForRelativeChange()
    {
        var a = S.State(("stock", 5));
        Assert.Equal(3, a.Add("stock", -2)["stock"]);
        Assert.Same(a, a.Add("stock", 0));
    }

    [Fact]
    public void HashIsStableAcrossRepeatedCalls()
    {
        var a = S.State(("stock", 5), ("slot", 2));
        var first = a.GetHashCode();
        Assert.Equal(first, a.GetHashCode());
        Assert.Equal(first, a.GetHashCode());
    }

    [Fact]
    public void DistinctStatesLargelyProduceDistinctHashes()
    {
        // Not a correctness requirement -- collisions are legal -- but a
        // catastrophic collision rate would turn the BFS visited-set into a
        // linear scan and make the whole tool unusable, so it is worth pinning.
        var seen = new HashSet<int>();
        for (var a = 0; a < 12; a++)
        {
            for (var b = 0; b < 12; b++)
            {
                seen.Add(S.State(("stock", a), ("reserved", b)).GetHashCode());
            }
        }

        Assert.True(seen.Count > 130, $"only {seen.Count} distinct hashes for 144 distinct states");
    }

    [Fact]
    public void DiffFromNamesOnlyWhatChanged()
    {
        var before = S.State(("stock", 5));
        var after = before.With("stock", 4).With("reserved", 1);
        var diff = after.DiffFrom(before);
        Assert.Contains("stock: 5 -> 4", diff, StringComparison.Ordinal);
        Assert.Contains("reserved: 0 -> 1", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("slot", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void DiffFromAnIdenticalStateIsEmpty()
    {
        var a = S.State(("stock", 5));
        Assert.Equal("(no change)", a.DiffFrom(a));
    }

    [Fact]
    public void StatesWorkAsDictionaryKeys()
    {
        var d = new Dictionary<WorldState, int>
        {
            [S.State(("stock", 1))] = 10
        };

        Assert.Equal(10, d[S.State(("stock", 1))]);
        Assert.False(d.ContainsKey(S.State(("stock", 2))));
    }
}
