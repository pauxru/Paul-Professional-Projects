using Collab.Domain.Diffing;
using Xunit;

namespace Collab.UnitTests.Diffing;

public sealed class DiffEngineTests
{
    [Fact]
    public void DiffChars_InsertionInMiddle_IsDetected()
    {
        var segments = DiffEngine.DiffChars("Helo", "Hello");
        var reconstructedOld = string.Concat(segments
            .Where(s => s.Kind != DiffKind.Insert).Select(s => s.Text));
        var reconstructedNew = string.Concat(segments
            .Where(s => s.Kind != DiffKind.Delete).Select(s => s.Text));

        Assert.Equal("Helo", reconstructedOld);
        Assert.Equal("Hello", reconstructedNew);
        Assert.Contains(segments, s => s.Kind == DiffKind.Insert && s.Text == "l");
    }

    [Fact]
    public void DiffChars_Deletion_IsDetected()
    {
        var segments = DiffEngine.DiffChars("incident", "indent");
        var reconstructedNew = string.Concat(segments
            .Where(s => s.Kind != DiffKind.Delete).Select(s => s.Text));
        Assert.Equal("indent", reconstructedNew);
        Assert.Contains(segments, s => s.Kind == DiffKind.Delete);
    }

    [Fact]
    public void DiffLines_ReplacedLine_ProducesInsertAndDelete()
    {
        var oldText = "step 1\nstep 2\nstep 3";
        var newText = "step 1\nstep two\nstep 3";
        var segments = DiffEngine.DiffLines(oldText, newText);

        Assert.Contains(segments, s => s.Kind == DiffKind.Delete && s.Text.Contains("step 2"));
        Assert.Contains(segments, s => s.Kind == DiffKind.Insert && s.Text.Contains("step two"));
        Assert.Contains(segments, s => s.Kind == DiffKind.Equal && s.Text.Contains("step 1"));
    }

    [Fact]
    public void DiffChars_IdenticalText_IsAllEqual()
    {
        var segments = DiffEngine.DiffChars("runbook", "runbook");
        Assert.All(segments, s => Assert.Equal(DiffKind.Equal, s.Kind));
    }

    [Fact]
    public void ReconstructingFromDiff_AlwaysYieldsBothSides()
    {
        // Property-ish check across a handful of pairs.
        var pairs = new[]
        {
            ("", "abc"),
            ("abc", ""),
            ("abcdef", "abXdeYf"),
            ("the quick brown fox", "the slow brown cat")
        };
        foreach (var (o, n) in pairs)
        {
            var segs = DiffEngine.DiffChars(o, n);
            var oldSide = string.Concat(segs.Where(s => s.Kind != DiffKind.Insert).Select(s => s.Text));
            var newSide = string.Concat(segs.Where(s => s.Kind != DiffKind.Delete).Select(s => s.Text));
            Assert.Equal(o, oldSide);
            Assert.Equal(n, newSide);
        }
    }
}
