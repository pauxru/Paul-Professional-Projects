using Collab.Domain.Crdt;
using Xunit;

namespace Collab.UnitTests.Crdt;

/// <summary>
/// Deterministic tests for every transform/merge pair the RGA must handle, plus the causal buffer.
/// Operations are built with explicit ElementIds so the converged result is fully predictable.
/// </summary>
public sealed class RgaDocumentTests
{
    private static RgaDocument WithBase(out ElementId x)
    {
        // A one-character base document "X" shared by both replicas.
        var doc = new RgaDocument();
        x = new ElementId(1, "S");
        doc.Apply(RgaOperation.Insert(x, ElementId.Root, 'X'));
        return doc;
    }

    [Fact]
    public void ConcurrentInsertSamePosition_ConvergesDeterministically()
    {
        var a = WithBase(out _);
        var b = WithBase(out _);

        var opA = RgaOperation.Insert(new ElementId(2, "A"), ElementId.Root, 'a');
        var opB = RgaOperation.Insert(new ElementId(2, "B"), ElementId.Root, 'b');

        // Apply in opposite orders on the two replicas.
        a.Apply(opA); a.Apply(opB);
        b.Apply(opB); b.Apply(opA);

        Assert.Equal(a.Materialize(), b.Materialize());
        // Higher id sorts first among siblings: (2,"B") > (2,"A") > (1,"S").
        Assert.Equal("baX", a.Materialize());
    }

    [Fact]
    public void ConcurrentInsertAndDelete_Converge()
    {
        var a = WithBase(out var x);
        var b = WithBase(out _);

        var ins = RgaOperation.Insert(new ElementId(2, "A"), x, 'y'); // insert after X
        var del = RgaOperation.Delete(x);                             // delete X

        a.Apply(ins); a.Apply(del);
        b.Apply(del); b.Apply(ins);

        Assert.Equal(a.Materialize(), b.Materialize());
        Assert.Equal("y", a.Materialize()); // X deleted, y remains
    }

    [Fact]
    public void ConcurrentDeleteSameElement_IsIdempotentAndConverges()
    {
        var a = WithBase(out var x);
        var b = WithBase(out _);

        var del1 = RgaOperation.Delete(x);
        var del2 = RgaOperation.Delete(x);

        a.Apply(del1); a.Apply(del2);
        b.Apply(del2); b.Apply(del1);

        Assert.Equal(a.Materialize(), b.Materialize());
        Assert.Equal("", a.Materialize());
        Assert.Equal(0, a.Length);
    }

    [Fact]
    public void AdjacentInserts_Converge()
    {
        var a = WithBase(out var x);
        var b = WithBase(out _);

        var insBefore = RgaOperation.Insert(new ElementId(2, "A"), ElementId.Root, 'p'); // before X
        var insAfter = RgaOperation.Insert(new ElementId(2, "B"), x, 'q');               // after X

        a.Apply(insBefore); a.Apply(insAfter);
        b.Apply(insAfter); b.Apply(insBefore);

        Assert.Equal(a.Materialize(), b.Materialize());
        Assert.Equal("pXq", a.Materialize());
    }

    [Fact]
    public void OverlappingDeletes_Converge()
    {
        // Build "abc" then two replicas delete overlapping ranges concurrently.
        var seed = new RgaDocument();
        var ia = new ElementId(1, "S");
        var ib = new ElementId(2, "S");
        var ic = new ElementId(3, "S");
        seed.Apply(RgaOperation.Insert(ia, ElementId.Root, 'a'));
        seed.Apply(RgaOperation.Insert(ib, ia, 'b'));
        seed.Apply(RgaOperation.Insert(ic, ib, 'c'));

        var a = RgaDocument.FromState(seed.ExportState());
        var b = RgaDocument.FromState(seed.ExportState());

        // A deletes {a,b}; B deletes {b,c}. Overlap on b.
        a.Apply(RgaOperation.Delete(ia)); a.Apply(RgaOperation.Delete(ib));
        b.Apply(RgaOperation.Delete(ib)); b.Apply(RgaOperation.Delete(ic));

        // Cross-deliver.
        a.Apply(RgaOperation.Delete(ic));
        b.Apply(RgaOperation.Delete(ia));

        Assert.Equal(a.Materialize(), b.Materialize());
        Assert.Equal("", a.Materialize());
    }

    [Fact]
    public void OutOfOrderInsert_IsBufferedThenAppliedWhenParentArrives()
    {
        var doc = new RgaDocument();
        var parent = new ElementId(1, "A");
        var child = new ElementId(2, "A");

        // Child arrives before its parent.
        var childStatus = doc.Apply(RgaOperation.Insert(child, parent, 'c'));
        Assert.Equal(ApplyStatus.Buffered, childStatus);
        Assert.Equal(1, doc.PendingCount);
        Assert.Equal("", doc.Materialize());

        // Parent arrives; the buffered child is drained automatically.
        var parentStatus = doc.Apply(RgaOperation.Insert(parent, ElementId.Root, 'b'));
        Assert.Equal(ApplyStatus.Applied, parentStatus);
        Assert.Equal(0, doc.PendingCount);
        Assert.Equal("bc", doc.Materialize());
    }

    [Fact]
    public void OutOfOrderDelete_IsBufferedUntilTargetArrives()
    {
        var doc = new RgaDocument();
        var target = new ElementId(5, "Z");

        var delStatus = doc.Apply(RgaOperation.Delete(target));
        Assert.Equal(ApplyStatus.Buffered, delStatus);
        Assert.Equal(1, doc.PendingCount);

        var insStatus = doc.Apply(RgaOperation.Insert(target, ElementId.Root, 'z'));
        Assert.Equal(ApplyStatus.Applied, insStatus);
        Assert.Equal(0, doc.PendingCount);
        Assert.Equal("", doc.Materialize()); // inserted then deleted
    }

    [Fact]
    public void DuplicateOperation_IsIgnored()
    {
        var doc = new RgaDocument();
        var id = new ElementId(1, "A");
        Assert.Equal(ApplyStatus.Applied, doc.Apply(RgaOperation.Insert(id, ElementId.Root, 'a')));
        Assert.Equal(ApplyStatus.Duplicate, doc.Apply(RgaOperation.Insert(id, ElementId.Root, 'a')));
        Assert.Equal("a", doc.Materialize());
    }

    [Fact]
    public void BuildInsert_AtIndex_ProducesExpectedText()
    {
        var doc = new RgaDocument();
        var clock = new LamportClock();
        foreach (var op in doc.BuildInsert(0, "Hello", clock, "A")) doc.Apply(op);
        Assert.Equal("Hello", doc.Materialize());

        // Insert " world" at the end.
        foreach (var op in doc.BuildInsert(5, " world", clock, "A")) doc.Apply(op);
        Assert.Equal("Hello world", doc.Materialize());

        // Insert "," after "Hello" (index 5).
        foreach (var op in doc.BuildInsert(5, ",", clock, "A")) doc.Apply(op);
        Assert.Equal("Hello, world", doc.Materialize());
    }

    [Fact]
    public void BuildDelete_RemovesRange()
    {
        var doc = new RgaDocument();
        var clock = new LamportClock();
        foreach (var op in doc.BuildInsert(0, "Hello, world", clock, "A")) doc.Apply(op);
        foreach (var op in doc.BuildDelete(5, 7)) doc.Apply(op); // remove ", world"
        Assert.Equal("Hello", doc.Materialize());
    }

    [Fact]
    public void ExportImportState_RoundTrips()
    {
        var doc = new RgaDocument();
        var clock = new LamportClock();
        foreach (var op in doc.BuildInsert(0, "runbook", clock, "A")) doc.Apply(op);
        foreach (var op in doc.BuildDelete(0, 3)) doc.Apply(op); // -> "book"

        var restored = RgaDocument.FromState(doc.ExportState());
        Assert.Equal(doc.Materialize(), restored.Materialize());
        Assert.Equal("book", restored.Materialize());
    }
}
