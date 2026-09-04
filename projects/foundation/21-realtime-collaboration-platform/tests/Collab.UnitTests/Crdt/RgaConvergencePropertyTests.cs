using Collab.Domain.Crdt;
using Xunit;
using Xunit.Abstractions;

namespace Collab.UnitTests.Crdt;

/// <summary>
/// The signature convergence property: many random concurrent operation streams from several
/// clients, delivered to each replica in a different (shuffled) order, must always converge to a
/// byte-identical document. Uses a FIXED SEED so any failure is perfectly reproducible.
/// </summary>
public sealed class RgaConvergencePropertyTests
{
    private const int Seed = 20260903;
    private readonly ITestOutputHelper _output;

    public RgaConvergencePropertyTests(ITestOutputHelper output) => _output = output;

    private sealed class Replica(string id)
    {
        public string Id { get; } = id;
        public RgaDocument Doc { get; } = new();
        public LamportClock Clock { get; } = new();

        public IReadOnlyList<RgaOperation> RandomEdit(Random rng)
        {
            var len = Doc.Length;
            if (len > 0 && rng.Next(100) < 30)
            {
                var count = 1 + rng.Next(Math.Min(3, len));
                var start = rng.Next(len - count + 1);
                var ops = Doc.BuildDelete(start, count);
                foreach (var op in ops) Doc.Apply(op);
                return ops;
            }
            else
            {
                var index = rng.Next(len + 1);
                var length = 1 + rng.Next(3);
                var text = new string(Enumerable.Range(0, length)
                    .Select(_ => (char)('a' + rng.Next(26))).ToArray());
                var ops = Doc.BuildInsert(index, text, Clock, Id);
                foreach (var op in ops) Doc.Apply(op);
                return ops;
            }
        }

        public void Receive(RgaOperation op)
        {
            Clock.Observe(Math.Max(op.Id.Lamport, op.Reference.Lamport));
            Doc.Apply(op);
        }
    }

    [Fact]
    public void RandomConcurrentEdits_FromMultipleClients_AlwaysConverge()
    {
        const int iterations = 150;
        var seedRng = new Random(Seed);
        var maxOps = 0;

        for (var iter = 0; iter < iterations; iter++)
        {
            var rng = new Random(seedRng.Next());
            var replicaCount = 3 + rng.Next(3); // 3..5 clients
            var replicas = Enumerable.Range(0, replicaCount)
                .Select(i => new Replica($"r{i}"))
                .ToList();

            // Generation phase: each replica edits its OWN local copy — nobody has seen anyone
            // else's operations yet, so these edits are genuinely concurrent.
            var tagged = new List<(RgaOperation Op, string Origin)>();
            var rounds = 6 + rng.Next(8);
            for (var round = 0; round < rounds; round++)
            {
                foreach (var r in replicas)
                {
                    var edits = rng.Next(3);
                    for (var e = 0; e < edits; e++)
                        foreach (var op in r.RandomEdit(rng))
                            tagged.Add((op, r.Id));
                }
            }
            maxOps = Math.Max(maxOps, tagged.Count);

            // Delivery phase: every replica receives all foreign operations in a shuffled order.
            foreach (var r in replicas)
            {
                var foreign = tagged.Where(t => t.Origin != r.Id).Select(t => t.Op).ToList();
                Shuffle(foreign, new Random(rng.Next()));
                foreach (var op in foreign) r.Receive(op);
            }

            // All replicas must have drained their buffers and converged identically.
            var reference = replicas[0].Doc.Materialize();
            foreach (var r in replicas)
            {
                Assert.Equal(0, r.Doc.PendingCount);
                Assert.Equal(reference, r.Doc.Materialize());
            }

            // Cross-check: a canonical replay of the full op set (in id order) yields the same text.
            var canonical = new RgaDocument();
            foreach (var op in tagged.Select(t => t.Op).OrderBy(o => o.Id))
                canonical.Apply(op);
            Assert.Equal(0, canonical.PendingCount);
            Assert.Equal(reference, canonical.Materialize());
        }

        _output.WriteLine($"Converged over {iterations} iterations; largest op set = {maxOps}.");
    }

    private static void Shuffle<T>(IList<T> list, Random rng)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
