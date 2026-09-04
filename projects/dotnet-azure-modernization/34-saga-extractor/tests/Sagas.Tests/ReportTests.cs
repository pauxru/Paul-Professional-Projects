using Sagas.Core;

namespace Sagas.Tests;

/// <summary>
/// The report DSL, whose only real job is to refuse to produce a document in
/// which a result was recorded without a prediction. That guarantee is worth
/// nothing unless it is tested, because the failure mode is silence.
/// </summary>
public class ReportTests
{
    private static Report New() => new("T", "intro", "gen", "env");

    [Fact]
    public void ARenderedReportContainsItsTitleAndIntro()
    {
        var text = New().Render();
        Assert.Contains("# T", text, StringComparison.Ordinal);
        Assert.Contains("intro", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FoundWithoutExpectIsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => New().Found("x", held: true));
        Assert.Contains("no matching Expect", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnclosedPredictionBlocksRendering()
    {
        var r = New().Expect("something");
        var ex = Assert.Throws<InvalidOperationException>(() => r.Render());
        Assert.Contains("still open", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnclosedPredictionBlocksTheNextSection()
    {
        var r = New().Expect("something");
        Assert.Throws<InvalidOperationException>(() => r.H2("next"));
    }

    [Fact]
    public void TwoExpectsInARowAreRejected()
    {
        var r = New().Expect("first");
        Assert.Throws<InvalidOperationException>(() => r.Expect("second"));
    }

    [Fact]
    public void APredictionAndItsResultBothAppear()
    {
        var text = New().Expect("the sky is green").Found("it is blue", held: false).Render();
        Assert.Contains("**Predicted.** the sky is green", text, StringComparison.Ordinal);
        Assert.Contains("**Contradicted.** it is blue", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HeldAndContradictedAreLabelledDifferently()
    {
        Assert.Contains("**Held.**", New().Expect("p").Found("r", held: true).Render(), StringComparison.Ordinal);
        Assert.Contains("**Contradicted.**", New().Expect("p").Found("r", held: false).Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheScoreboardCountsBothOutcomes()
    {
        var text = New()
            .H2("a").Expect("p1").Found("r1", held: true)
            .H2("b").Expect("p2").Found("r2", held: false)
            .H2("c").Expect("p3").Found("r3", held: false)
            .Render();

        Assert.Contains("3 predictions were registered", text, StringComparison.Ordinal);
        Assert.Contains("| a | held |", text, StringComparison.Ordinal);
        Assert.Contains("| b | **contradicted** |", text, StringComparison.Ordinal);
        Assert.Contains("| c | **contradicted** |", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProseIsHardWrappedForReviewableDiffs()
    {
        var text = New().Para(string.Join(" ", Enumerable.Repeat("word", 200))).Render();
        Assert.All(
            text.Split('\n').Where(l => !l.StartsWith('|')),
            l => Assert.True(l.Length <= 80, $"line of {l.Length} chars: {l}"));
    }

    [Fact]
    public void CodeBlocksAreNotWrapped()
    {
        var long_ = new string('x', 200);
        var text = New().Code(long_).Render();
        Assert.Contains(long_, text, StringComparison.Ordinal);
    }

    [Fact]
    public void TablesArePaddedToAlignedColumns()
    {
        var text = New()
            .Table(["a", "bbbb"], [["xxxxx", "y"], ["z", "w"]])
            .Render();
        var rows = text.Split('\n')
            .TakeWhile(l => !l.StartsWith("## Scoreboard", StringComparison.Ordinal))
            .Where(l => l.StartsWith('|'))
            .ToList();
        Assert.Equal(4, rows.Count);
        Assert.Single(rows.Select(x => x.Length).Distinct());
    }

    [Fact]
    public void RenderedOutputUsesLfOnly()
    {
        // The results-integrity test byte-compares this against a file on disk.
        // A stray CR would make it pass on one platform and fail on another.
        Assert.DoesNotContain('\r', New().Para("x").Render());
    }

    [Fact]
    public void RenderIsIdempotent()
    {
        var r = New().Expect("p").Found("f", held: true);
        Assert.Equal(r.Render(), r.Render());
    }
}
