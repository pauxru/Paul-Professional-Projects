using Lakehouse.Application.Orchestration;
using Lakehouse.Application.Pipelines;
using Lakehouse.UnitTests.Support;

namespace Lakehouse.UnitTests;

/// <summary>
/// Orchestration: deterministic topological execution, per-task retries, blocking of downstream tasks
/// when an upstream fails, partial re-runs limited to the downstream closure, backfill of multiple
/// windows, and concurrency control preventing overlapping runs of the same window.
/// </summary>
public sealed class DagTests
{
    private static StepResult Ok(string id) => new(id, 1, 1);

    [Fact]
    public void Topological_order_respects_dependencies()
    {
        var dag = new Dag();
        dag.Add("c", _ => Ok("c"), 0, "b");
        dag.Add("b", _ => Ok("b"), 0, "a");
        dag.Add("a", _ => Ok("a"));
        dag.Add("d", _ => Ok("d"), 0, "a");

        var order = dag.TopologicalOrder().ToList();
        Assert.True(order.IndexOf("a") < order.IndexOf("b"));
        Assert.True(order.IndexOf("b") < order.IndexOf("c"));
        Assert.True(order.IndexOf("a") < order.IndexOf("d"));
    }

    [Fact]
    public void Cycle_is_detected()
    {
        var dag = new Dag();
        dag.Add("a", _ => Ok("a"), 0, "b");
        dag.Add("b", _ => Ok("b"), 0, "a");
        Assert.Throws<InvalidOperationException>(() => dag.TopologicalOrder());
    }

    [Fact]
    public void Task_is_retried_until_it_succeeds()
    {
        using var lh = new TempLake();
        var attempts = 0;
        var dag = new Dag();
        dag.Add("flaky", _ =>
        {
            attempts++;
            if (attempts < 3) throw new InvalidOperationException("transient");
            return Ok("flaky");
        }, maxRetries: 2);

        var record = lh.Runner().Run(dag, RunContext.Full("r1"));

        var task = record.Tasks.Single();
        Assert.Equal(TaskState.Succeeded, task.State);
        Assert.Equal(3, task.Attempts);
    }

    [Fact]
    public void Exhausted_retries_fail_and_block_downstream()
    {
        using var lh = new TempLake();
        var ranDownstream = false;
        var dag = new Dag();
        dag.Add("bad", _ => throw new InvalidOperationException("always"), 1);
        dag.Add("child", _ => { ranDownstream = true; return Ok("child"); }, 0, "bad");

        var record = lh.Runner().Run(dag, RunContext.Full("r1"));

        Assert.Equal(TaskState.Failed, record.Tasks.Single(t => t.TaskId == "bad").State);
        Assert.Equal(TaskState.Blocked, record.Tasks.Single(t => t.TaskId == "child").State);
        Assert.False(ranDownstream);
        Assert.False(record.Success);
    }

    [Fact]
    public void Partial_rerun_executes_only_downstream_closure()
    {
        using var lh = new TempLake();
        var runner = lh.Runner();
        var ran = new HashSet<string>();
        Dag Build()
        {
            var dag = new Dag();
            dag.Add("a", _ => { ran.Add("a"); return Ok("a"); });
            dag.Add("b", _ => { ran.Add("b"); return Ok("b"); }, 0, "a");
            dag.Add("c", _ => { ran.Add("c"); return Ok("c"); }, 0, "b");
            dag.Add("d", _ => { ran.Add("d"); return Ok("d"); }, 0, "a");
            return dag;
        }

        runner.Run(Build(), new RunContext("r1", "w1"));
        ran.Clear();
        runner.Run(Build(), new RunContext("r2", "w2"), onlyTasks: new[] { "b" });

        Assert.Contains("b", ran);
        Assert.Contains("c", ran);      // downstream of b
        Assert.DoesNotContain("a", ran); // upstream — not re-run
        Assert.DoesNotContain("d", ran); // sibling branch — not re-run
    }

    [Fact]
    public void Backfill_runs_every_window()
    {
        using var lh = new TempLake();
        var windows = new List<string>();
        Dag Build()
        {
            var dag = new Dag();
            dag.Add("t", ctx => { windows.Add(ctx.Window); return Ok("t"); });
            return dag;
        }

        var records = lh.Runner().Backfill(Build(), new[] { "2026-01-01", "2026-01-02", "2026-01-03" },
            w => new RunContext($"bf-{w}", w));

        Assert.Equal(3, records.Count);
        Assert.Equal(new[] { "2026-01-01", "2026-01-02", "2026-01-03" }, windows);
    }

    [Fact]
    public void Overlapping_run_of_same_window_is_rejected()
    {
        using var lh = new TempLake();
        var runner = lh.Runner();
        Exception? caught = null;

        var outer = new Dag();
        outer.Add("reenter", ctx =>
        {
            var inner = new Dag();
            inner.Add("noop", _ => Ok("noop"));
            try { runner.Run(inner, ctx); }   // same window -> must be rejected
            catch (Exception ex) { caught = ex; }
            return Ok("reenter");
        });

        runner.Run(outer, new RunContext("r1", "same-window"));
        Assert.IsType<OverlappingRunException>(caught);
    }
}
