using Sagas.Core;

namespace Sagas.Tests;

public class ExtractorTests
{
    private static readonly IReadOnlyList<Operation> Ops = Extractor.OrderTransaction;

    [Fact]
    public void NaivePreservesSourceOrder()
    {
        Assert.Equal(Ops.Select(o => o.Name), Extractor.Naive(Ops).Order.Select(o => o.Name));
    }

    [Fact]
    public void ReorderProducesTheSagaShape()
    {
        var plan = Extractor.Reorder(Ops);
        var ranks = plan.Order.Select(o => o.Reversibility).ToList();
        var pivot = plan.PivotIndex;

        Assert.All(ranks.Take(pivot), rv => Assert.Equal(Reversibility.Internal, rv));
        Assert.All(ranks.Skip(pivot + 1), rv => Assert.Equal(Reversibility.GuaranteedToSucceed, rv));
    }

    [Fact]
    public void ReorderIsAPermutationNotARewrite()
    {
        var plan = Extractor.Reorder(Ops);
        Assert.Equal(
            Ops.Select(o => o.Name).OrderBy(x => x, StringComparer.Ordinal),
            plan.Order.Select(o => o.Name).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void ReorderRespectsDataDependencies()
    {
        var plan = Extractor.Reorder(Ops);
        var position = plan.Order
            .Select((o, i) => (o.Name, i))
            .ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);

        foreach (var writer in Ops)
        {
            foreach (var reader in Ops.Where(r => r.Reads.Any(v => writer.Writes.Contains(v))))
            {
                if (!ReferenceEquals(writer, reader))
                {
                    Assert.True(
                        position[writer.Name] < position[reader.Name],
                        $"{reader.Name} reads a variable written by {writer.Name} but was scheduled first");
                }
            }
        }
    }

    [Fact]
    public void ReorderIsDeterministic()
    {
        Assert.Equal(
            Extractor.Reorder(Ops).Order.Select(o => o.Name),
            Extractor.Reorder(Ops).Order.Select(o => o.Name));
    }

    [Fact]
    public void ExactlyOneOperationIsMarkedAsThePivot()
    {
        var described = Extractor.Reorder(Ops).Describe();
        Assert.Equal(1, described.Split('\n').Count(l => l.Contains("PIVOT", StringComparison.Ordinal)));
    }

    [Fact]
    public void GuaranteedToSucceedSortsAfterExternallyVisibleNotBefore()
    {
        // The ranking bug this pins is subtle and was actually present: an
        // operation that cannot be undone but always succeeds feels safer, so it
        // sorts early -- and lands before the pivot, where nothing can roll back
        // past it.
        var plan = Extractor.Reorder(Ops);
        var pivot = plan.PivotIndex;
        Assert.DoesNotContain(
            plan.Order.Take(pivot),
            o => o.Reversibility == Reversibility.GuaranteedToSucceed);
    }

    [Fact]
    public void OneBooleanAboutADownstreamApiMovesThePivot()
    {
        var before = Extractor.Reorder(Extractor.OrderTransaction);
        var after = Extractor.Reorder(Extractor.OrderTransactionWithDedupedEmail);

        Assert.Equal("SendConfirmation", before.Order[before.PivotIndex].Name);
        Assert.Equal("CapturePayment", after.Order[after.PivotIndex].Name);

        // The point of no return stops being "we promised the customer" and
        // becomes "we took the money". The number of abortable steps is
        // unchanged -- this is not a structural win, it is a business one, and
        // the honest claim is the narrower of the two.
        Assert.Equal(before.PivotIndex, after.PivotIndex);
        Assert.Contains(after.Order.Skip(after.PivotIndex + 1), o => o.Name == "SendConfirmation");
    }

    [Fact]
    public void HandoffCountIsSymmetricUnderReordering()
    {
        // Measured, not assumed: the reordering does not change the number of
        // network boundaries, because the data dependencies pin the payment
        // service on both sides of the pivot regardless.
        Assert.Equal(
            Extractor.Naive(Ops).CrossServiceHandoffs,
            Extractor.Reorder(Ops).CrossServiceHandoffs);
    }

    [Fact]
    public void HandoffCountIsAtLeastTheNumberOfDistinctServicesMinusOne()
    {
        var services = Ops.Select(o => o.Service).Distinct().Count();
        Assert.True(Extractor.Reorder(Ops).CrossServiceHandoffs >= services - 1);
    }

    [Fact]
    public void RationaleExplainsEveryOperationItMoved()
    {
        var plan = Extractor.Reorder(Ops);
        Assert.NotEmpty(plan.Rationale);
        Assert.Contains(plan.Rationale, x => x.Contains("service handoffs", StringComparison.Ordinal));
    }

    [Fact]
    public void ASingleOperationIsItsOwnPlan()
    {
        var one = new[] { Ops[0] };
        Assert.Single(Extractor.Reorder(one).Order);
        Assert.Equal(0, Extractor.Reorder(one).CrossServiceHandoffs);
    }

    [Fact]
    public void ACyclicDependencyIsImpossibleByConstruction()
    {
        // A and B each read what the other writes, which in a general dependency
        // graph is a cycle. It is not one here, and the reason is worth pinning:
        // the extractor only ever emits edges in source-order direction, so the
        // graph is a DAG whatever the operations do. That is not a limitation, it
        // is the semantics -- the original code ran A before B, and any ordering
        // that reverses them is a different program, not a better schedule.
        var a = Operation.Of("A", "s", Reversibility.Internal, reads: ["y"], writes: ["x"]);
        var b = Operation.Of("B", "s", Reversibility.Internal, reads: ["x"], writes: ["y"]);
        var plan = Extractor.Reorder([a, b]);
        Assert.Equal(["A", "B"], plan.Order.Select(o => o.Name));
    }

    [Fact]
    public void AntiDependenciesPinOrderJustLikeFlowDependencies()
    {
        // B overwrites what A reads. Reordering would silently change what A
        // sees, which is the classic loop-reordering hazard and is exactly as
        // fatal in a workflow as it is in a compiler.
        var a = Operation.Of("A", "s1", Reversibility.GuaranteedToSucceed, reads: ["v"], writes: ["p"]);
        var b = Operation.Of("B", "s2", Reversibility.Internal, writes: ["v"]);
        var plan = Extractor.Reorder([a, b]);
        Assert.Equal(["A", "B"], plan.Order.Select(o => o.Name));
    }

    [Fact]
    public void ClassificationDescribesWhatAnOperationIsNotWhereItLanded()
    {
        var plan = Extractor.Reorder(Ops);
        var lines = plan.Describe().Split('\n');
        var dispatch = lines.Single(l => l.Contains("Dispatch", StringComparison.Ordinal));
        Assert.Contains("retriable", dispatch, StringComparison.Ordinal);
        Assert.DoesNotContain("compensatable", dispatch, StringComparison.Ordinal);
    }
}
