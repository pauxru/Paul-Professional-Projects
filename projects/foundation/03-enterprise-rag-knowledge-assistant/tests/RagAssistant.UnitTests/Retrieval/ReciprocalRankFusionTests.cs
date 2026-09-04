using RagAssistant.Application.Retrieval;

namespace RagAssistant.UnitTests.Retrieval;

public sealed class ReciprocalRankFusionTests
{
    [Fact]
    public void Fuse_TwoIdenticalRankings_DoublesEachScore()
    {
        var ranking1 = new[] { "a", "b", "c" };
        var ranking2 = new[] { "a", "b", "c" };
        var results = ReciprocalRankFusion.Fuse(new[] { ranking1, ranking2 });

        Assert.Equal(3, results.Count);
        Assert.Equal("a", results[0].Id);
        Assert.Equal("b", results[1].Id);
        Assert.Equal("c", results[2].Id);
        Assert.InRange(results[0].Score, 0.032, 0.033);
    }

    [Fact]
    public void Fuse_ConflictingRankings_PrefersHighAverageRank()
    {
        var keyword = new[] { "x", "y", "z" };
        var dense = new[] { "y", "z", "x" };
        var results = ReciprocalRankFusion.Fuse(new[] { keyword, dense });

        Assert.Equal(3, results.Count);
        Assert.Equal("y", results[0].Id);
    }

    [Fact]
    public void Fuse_ThrowsForInvalidK()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReciprocalRankFusion.Fuse(new[] { new[] { "a" } }, k: 0));
    }

    [Fact]
    public void Fuse_SingleRanking_ReturnsSameOrder()
    {
        var ranking = new[] { "one", "two", "three" };
        var results = ReciprocalRankFusion.Fuse(new[] { ranking });

        Assert.Equal("one", results[0].Id);
        Assert.Equal("two", results[1].Id);
        Assert.Equal("three", results[2].Id);
    }

    [Fact]
    public void Fuse_EmptyInputs_ReturnsEmpty()
    {
        var results = ReciprocalRankFusion.Fuse<string>(Array.Empty<IReadOnlyList<string>>());
        Assert.Empty(results);
    }
}
