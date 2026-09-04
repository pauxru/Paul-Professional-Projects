using RagAssistant.Application.Retrieval;

namespace RagAssistant.UnitTests.Retrieval;

public sealed class Bm25IndexTests
{
    [Fact]
    public void Search_ReturnsDocumentContainingQueryTerm()
    {
        var index = new Bm25Index();
        index.Add(new Bm25Document("d1", "the cat sat on the mat"));
        index.Add(new Bm25Document("d2", "dogs bark loudly in the yard"));
        index.Add(new Bm25Document("d3", "cats and dogs sleep together"));

        var hits = index.Search("cat", 5);
        Assert.NotEmpty(hits);
        Assert.Equal("d1", hits[0].Id);
    }

    [Fact]
    public void Search_ScoresRarerTermHigher()
    {
        var index = new Bm25Index();
        index.Add(new Bm25Document("d1", "network firewall firewall firewall firewall"));
        index.Add(new Bm25Document("d2", "the network is up"));
        index.Add(new Bm25Document("d3", "keyboard mouse monitor"));

        var firewallHits = index.Search("firewall", 5);
        Assert.Equal("d1", firewallHits[0].Id);
        Assert.True(firewallHits[0].Score > 0);
    }

    [Fact]
    public void Search_HandCalculatedApprox()
    {
        var index = new Bm25Index(new Bm25Options(K1: 1.2, B: 0.75));
        index.Add(new Bm25Document("A", "apple banana cherry"));
        index.Add(new Bm25Document("B", "apple apple banana"));
        index.Add(new Bm25Document("C", "banana cherry date"));

        var hits = index.Search("apple", 5).ToDictionary(h => h.Id, h => h.Score);
        Assert.Contains("A", hits.Keys);
        Assert.Contains("B", hits.Keys);
        Assert.DoesNotContain("C", hits.Keys);
        Assert.True(hits["B"] > hits["A"], "Document B has higher term frequency and should score higher.");
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsEmpty()
    {
        var index = new Bm25Index();
        index.Add(new Bm25Document("d1", "content"));
        Assert.Empty(index.Search("", 5));
    }

    [Fact]
    public void Search_UnknownTerm_ReturnsEmpty()
    {
        var index = new Bm25Index();
        index.Add(new Bm25Document("d1", "alpha beta gamma"));
        Assert.Empty(index.Search("zzz", 5));
    }
}
