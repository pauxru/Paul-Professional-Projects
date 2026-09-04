using Collab.Domain.Structured;
using Xunit;

namespace Collab.UnitTests.Structured;

/// <summary>
/// Convergence and last-writer-wins semantics for the structured (checklist) document, plus a
/// randomised concurrent-merge property test with a fixed seed.
/// </summary>
public sealed class StructuredDocumentTests
{
    [Fact]
    public void ConcurrentFieldWrites_HighestStampWins()
    {
        var a = new StructuredDocument();
        var b = new StructuredDocument();

        var add = StructuredOperation.AddItem("task-1", new LwwStamp(1, "a"));
        var setA = StructuredOperation.SetField("task-1", "text", "Draft", new LwwStamp(2, "a"));
        var setB = StructuredOperation.SetField("task-1", "text", "Review", new LwwStamp(3, "b"));

        a.Apply(add); a.Apply(setA); a.Apply(setB);
        b.Apply(setB); b.Apply(add); b.Apply(setA);

        Assert.Equal(a.ToCanonicalJson(), b.ToCanonicalJson());
        Assert.Equal("Review", a.GetField("task-1", "text")); // stamp 3 > stamp 2
    }

    [Fact]
    public void RemoveWinsWhenNewerThanAdd_AndAddWinsWhenNewer()
    {
        var doc = new StructuredDocument();
        doc.Apply(StructuredOperation.AddItem("t", new LwwStamp(1, "a")));
        doc.Apply(StructuredOperation.RemoveItem("t", new LwwStamp(2, "a")));
        Assert.False(doc.HasItem("t"));

        doc.Apply(StructuredOperation.AddItem("t", new LwwStamp(3, "a")));
        Assert.True(doc.HasItem("t")); // re-add with newer stamp brings it back
    }

    [Fact]
    public void VersionVector_DetectsConcurrency()
    {
        var doc = new StructuredDocument();
        doc.Apply(StructuredOperation.SetField("t", "text", "x", new LwwStamp(5, "a")));
        doc.Apply(StructuredOperation.SetField("t", "done", "true", new LwwStamp(4, "b")));

        Assert.Equal(5, doc.Version.Get("a"));
        Assert.Equal(4, doc.Version.Get("b"));
    }

    [Fact]
    public void RandomConcurrentStructuredMerges_Converge()
    {
        var seedRng = new Random(20260903);
        for (var iter = 0; iter < 100; iter++)
        {
            var rng = new Random(seedRng.Next());
            var replicaCount = 3 + rng.Next(3);
            var docs = Enumerable.Range(0, replicaCount).Select(_ => new StructuredDocument()).ToList();
            var all = new List<StructuredOperation>();

            var lamport = 0;
            var opCount = 20 + rng.Next(30);
            for (var i = 0; i < opCount; i++)
            {
                lamport++;
                var replica = $"r{rng.Next(replicaCount)}";
                var stamp = new LwwStamp(lamport, replica);
                var item = $"item-{rng.Next(6)}";
                var op = rng.Next(4) switch
                {
                    0 => StructuredOperation.AddItem(item, stamp),
                    1 => StructuredOperation.RemoveItem(item, stamp),
                    2 => StructuredOperation.SetField(item, "text", $"v{rng.Next(100)}", stamp),
                    _ => StructuredOperation.SetField(item, "done", (rng.Next(2) == 0).ToString(), stamp)
                };
                all.Add(op);
            }

            // Apply the same set of ops to every replica in a different shuffled order.
            foreach (var doc in docs)
            {
                var shuffled = all.OrderBy(_ => rng.Next()).ToList();
                foreach (var op in shuffled) doc.Apply(op);
            }

            var reference = docs[0].ToCanonicalJson();
            foreach (var doc in docs)
                Assert.Equal(reference, doc.ToCanonicalJson());
        }
    }
}
