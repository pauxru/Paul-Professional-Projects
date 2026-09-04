using JobScheduler.Domain;

namespace JobScheduler.UnitTests.Domain;

/// <summary>Topological ordering, cycle detection, fan-in and fan-out over the dependency DAG.</summary>
public sealed class DagValidatorTests
{
    private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> Edges(
        params (string Node, string[] Deps)[] entries)
    {
        var d = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        foreach (var (node, deps) in entries)
        {
            d[node] = deps;
        }
        return d;
    }

    [Fact]
    public void Linear_chain_orders_dependencies_first()
    {
        // C depends on B depends on A.
        var order = DagValidator.TopologicalOrder(Edges(("A", []), ("B", ["A"]), ("C", ["B"])));
        Assert.Equal(["A", "B", "C"], order);
    }

    [Fact]
    public void FanOut_places_root_before_all_dependents()
    {
        var order = DagValidator.TopologicalOrder(Edges(("A", []), ("B", ["A"]), ("C", ["A"])));
        Assert.Equal(0, order.ToList().IndexOf("A"));
        Assert.True(order.ToList().IndexOf("A") < order.ToList().IndexOf("B"));
        Assert.True(order.ToList().IndexOf("A") < order.ToList().IndexOf("C"));
    }

    [Fact]
    public void FanIn_places_all_predecessors_before_the_join()
    {
        var order = DagValidator.TopologicalOrder(Edges(("A", []), ("B", []), ("C", ["A", "B"]))).ToList();
        Assert.True(order.IndexOf("A") < order.IndexOf("C"));
        Assert.True(order.IndexOf("B") < order.IndexOf("C"));
    }

    [Fact]
    public void Cycle_is_detected_and_reported()
    {
        var edges = Edges(("A", ["B"]), ("B", ["A"]));
        var ex = Assert.Throws<DependencyCycleException>(() => DagValidator.TopologicalOrder(edges));
        Assert.Contains("A", ex.Nodes);
        Assert.Contains("B", ex.Nodes);
        Assert.True(DagValidator.HasCycle(edges));
    }

    [Fact]
    public void HasCycle_is_false_for_a_valid_dag()
    {
        Assert.False(DagValidator.HasCycle(Edges(("A", []), ("B", ["A"]))));
    }

    [Fact]
    public void Dependencies_returns_direct_predecessors()
    {
        var edges = Edges(("A", []), ("B", []), ("C", ["A", "B"]));
        Assert.Equal(["A", "B"], DagValidator.Dependencies(edges, "C"));
        Assert.Empty(DagValidator.Dependencies(edges, "A"));
    }
}
