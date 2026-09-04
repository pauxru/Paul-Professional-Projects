using Collab.Domain.Comments;
using Xunit;

namespace Collab.UnitTests.Comments;

public sealed class AnchorRebaserTests
{
    [Fact]
    public void InsertBeforeAnchor_ShiftsBoth()
    {
        var a = new TextAnchor(5, 10);
        var r = AnchorRebaser.OnInsert(a, 2, 3);
        Assert.Equal(8, r.Start);
        Assert.Equal(13, r.End);
        Assert.False(r.Orphaned);
    }

    [Fact]
    public void InsertInsideAnchor_GrowsEnd()
    {
        var a = new TextAnchor(5, 10);
        var r = AnchorRebaser.OnInsert(a, 7, 4);
        Assert.Equal(5, r.Start);
        Assert.Equal(14, r.End);
    }

    [Fact]
    public void InsertAfterAnchor_LeavesUnchanged()
    {
        var a = new TextAnchor(5, 10);
        var r = AnchorRebaser.OnInsert(a, 20, 3);
        Assert.Equal(a, r);
    }

    [Fact]
    public void DeleteBeforeAnchor_ShiftsLeft()
    {
        var a = new TextAnchor(10, 15);
        var r = AnchorRebaser.OnDelete(a, 2, 4);
        Assert.Equal(6, r.Start);
        Assert.Equal(11, r.End);
        Assert.False(r.Orphaned);
    }

    [Fact]
    public void DeletePartiallyOverlapping_ShrinksAnchor()
    {
        var a = new TextAnchor(5, 10);
        var r = AnchorRebaser.OnDelete(a, 7, 5); // delete [7,12)
        Assert.Equal(5, r.Start);
        Assert.Equal(7, r.End); // 10 collapses into the deletion at 7
        Assert.False(r.Orphaned);
    }

    [Fact]
    public void DeleteCoveringEntireAnchor_Orphans()
    {
        var a = new TextAnchor(5, 10);
        var r = AnchorRebaser.OnDelete(a, 3, 12); // delete [3,15) covers [5,10)
        Assert.True(r.Orphaned);
        Assert.True(r.IsEmpty);
    }

    [Fact]
    public void DeleteExactlyAnchor_Orphans()
    {
        var a = new TextAnchor(5, 10);
        var r = AnchorRebaser.OnDelete(a, 5, 5);
        Assert.True(r.Orphaned);
    }

    [Fact]
    public void Caret_RebasesAroundEdits()
    {
        Assert.Equal(8, AnchorRebaser.CaretOnInsert(5, 2, 3));
        Assert.Equal(5, AnchorRebaser.CaretOnInsert(5, 9, 3));
        Assert.Equal(3, AnchorRebaser.CaretOnDelete(6, 1, 3));
        Assert.Equal(2, AnchorRebaser.CaretOnDelete(4, 2, 5)); // caret inside deletion collapses
    }

    [Fact]
    public void OrphanedAnchor_IsNotFurtherModified()
    {
        var a = new TextAnchor(5, 5, Orphaned: true);
        Assert.Equal(a, AnchorRebaser.OnInsert(a, 0, 5));
        Assert.Equal(a, AnchorRebaser.OnDelete(a, 0, 5));
    }
}
