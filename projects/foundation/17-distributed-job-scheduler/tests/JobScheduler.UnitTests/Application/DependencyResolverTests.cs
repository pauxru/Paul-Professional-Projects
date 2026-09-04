using JobScheduler.Application.Services;
using JobScheduler.Domain;

namespace JobScheduler.UnitTests.Application;

/// <summary>DAG readiness resolution for fan-in / fan-out workflow orchestration.</summary>
public sealed class DependencyResolverTests
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
    public void Dependent_becomes_ready_once_all_upstreams_succeed()
    {
        var edges = Edges(("extract", []), ("transform", ["extract"]), ("load", ["transform"]));
        var ready = DependencyResolver.ReadyToRun(
            edges,
            succeeded: new HashSet<string> { "extract" },
            alreadyScheduled: new HashSet<string>());
        Assert.Equal(["transform"], ready);
    }

    [Fact]
    public void FanIn_waits_for_every_predecessor()
    {
        var edges = Edges(("a", []), ("b", []), ("join", ["a", "b"]));

        var onlyA = DependencyResolver.ReadyToRun(edges, new HashSet<string> { "a" }, new HashSet<string>());
        Assert.Empty(onlyA); // join still waiting on b

        var both = DependencyResolver.ReadyToRun(edges, new HashSet<string> { "a", "b" }, new HashSet<string>());
        Assert.Equal(["join"], both);
    }

    [Fact]
    public void Already_scheduled_dependents_are_not_re_enqueued()
    {
        var edges = Edges(("root", []), ("child", ["root"]));
        var ready = DependencyResolver.ReadyToRun(
            edges,
            succeeded: new HashSet<string> { "root" },
            alreadyScheduled: new HashSet<string> { "child" });
        Assert.Empty(ready);
    }

    [Fact]
    public void Roots_are_nodes_without_dependencies()
    {
        var edges = Edges(("a", []), ("b", []), ("c", ["a", "b"]));
        Assert.Equal(["a", "b"], DependencyResolver.Roots(edges));
    }

    [Fact]
    public void A_cycle_is_surfaced_rather_than_silently_stalling()
    {
        var edges = Edges(("a", ["b"]), ("b", ["a"]));
        Assert.Throws<DependencyCycleException>(
            () => DependencyResolver.ReadyToRun(edges, new HashSet<string>(), new HashSet<string>()));
    }
}
