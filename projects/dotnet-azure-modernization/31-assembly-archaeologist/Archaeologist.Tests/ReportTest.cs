using Archaeologist.Core;

namespace Archaeologist.Tests;

/// <summary>
/// The report is a program that refuses to lie. It cannot render while a prediction is
/// unsettled, it cannot contain a character the console will mangle, and it cannot quietly
/// drop a table column. Those refusals are the only thing standing between a generated
/// document and a plausible one.
/// </summary>
public class ReportTest
{
    private static Report R() => new();

    [Fact]
    public void APredictionStartsUnsettled()
    {
        Assert.Equal(Verdict.Unsettled, R().Expect("P1", "something").Verdict);
    }

    [Fact]
    public void RenderingWithAnUnsettledPredictionThrowsRatherThanPrintingAGap()
    {
        var r = R();
        r.Expect("P1", "never checked");
        var ex = Assert.Throws<InvalidOperationException>(() => r.Render());
        Assert.Contains("P1", ex.Message);
    }

    [Fact]
    public void RenderingSucceedsOnceEveryPredictionIsSettled()
    {
        var r = R();
        var p = r.Expect("P1", "checked");
        p.Held("because the numbers said so");
        r.Settle(p);
        Assert.Contains("P1", r.Render());
    }

    [Fact]
    public void APredictionCannotBeSettledTwiceBecauseThatHidesTheFirstAnswer()
    {
        var r = R();
        var p = r.Expect("P1", "s");
        p.Held("e1");
        Assert.Throws<InvalidOperationException>(() => p.Contradicted("e2"));
    }

    [Fact]
    public void TwoPredictionsCannotShareAnId()
    {
        var r = R();
        r.Expect("P1", "a");
        Assert.Throws<InvalidOperationException>(() => r.Expect("P1", "b"));
    }

    [Fact]
    public void SettlingRequiresEvidenceNotJustAVerdict()
    {
        var p = R().Expect("P1", "s");
        Assert.Throws<ArgumentException>(() => p.Held("   "));
    }

    [Fact]
    public void BothVerdictsAppearInTheRenderedText()
    {
        var r = R();
        var a = r.Expect("P1", "holds");
        a.Held("evidence a");
        var b = r.Expect("P2", "fails");
        b.Contradicted("evidence b");
        r.Settle(a).Settle(b);
        var text = r.Render();
        Assert.Contains("HELD", text);
        Assert.Contains("CONTRADICTED", text);
        Assert.Contains("evidence b", text);
    }

    [Fact]
    public void NonAsciiIsRejectedBecauseTheConsoleThisRunsOnCannotShowIt()
    {
        // A report full of mojibake is not a report. Better to fail at generation time.
        var r = R();
        r.P("the tau value was 0.567\u2026");
        Assert.Throws<InvalidOperationException>(() => r.Render());
    }

    [Fact]
    public void TheNonAsciiErrorNamesTheOffendingCharacter()
    {
        var r = R();
        r.P("smart \u201cquotes\u201d");
        var ex = Assert.Throws<InvalidOperationException>(() => r.Render());
        Assert.Contains("201c", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlainAsciiPunctuationIsAccepted()
    {
        var r = R();
        r.P("tau = 0.567; greedy >= exact -- always.");
        Assert.Contains("greedy >= exact", r.Render());
    }

    [Fact]
    public void ARowWithTheWrongNumberOfCellsIsRejected()
    {
        var r = R();
        Assert.Throws<ArgumentException>(() =>
            r.Table(["a", "b"], [new[] { "1", "2" }, new[] { "1" }]));
    }

    [Fact]
    public void ATableWithNoHeadersIsRejected()
    {
        Assert.Throws<ArgumentException>(() => R().Table([], [new[] { "1" }]));
    }

    [Fact]
    public void TablesRenderAsMarkdownWithASeparatorRow()
    {
        var text = R().Table(["left", "right"], [new[] { "1", "2" }]).Render();
        Assert.Contains("| left | right |", text);
        Assert.Contains("|---|---|", text);
        Assert.Contains("| 1 | 2 |", text);
    }

    [Fact]
    public void BulletsRenderOnePerLine()
    {
        var text = R().Bullets(["first", "second"]).Render();
        Assert.Contains("- first\n- second", text);
    }

    [Fact]
    public void CodeBlocksAreFenced()
    {
        var text = R().Code("some_output = 1").Render();
        Assert.Contains("```\nsome_output = 1\n```", text);
    }

    [Fact]
    public void TheTitleAndSubtitleReachTheOutput()
    {
        var text = new Report().H1("A Title").P("A Subtitle").Render();
        Assert.Contains("# A Title", text);
        Assert.Contains("A Subtitle", text);
    }

    [Fact]
    public void RenderingIsIdempotentSoTheReportCanBeInspectedBeforeItIsWritten()
    {
        var r = R();
        var p = r.Expect("P1", "s");
        p.Held("e");
        r.Settle(p).P("body");
        Assert.Equal(r.Render(), r.Render());
    }

    [Fact]
    public void LineEndingsAreAlwaysLfSoTheByteComparisonMeansSomethingOnWindows()
    {
        var text = R().P("one").P("two").Render();
        Assert.DoesNotContain("\r", text);
    }

    [Fact]
    public void APredictionSettledButNeverRegisteredIsStillMissingFromTheReport()
    {
        // Expect() registers; Settle() only records the answer. Forgetting Settle() must
        // not silently drop the prediction from the summary.
        var r = R();
        var p = r.Expect("P1", "s");
        p.Held("e");
        Assert.Contains("P1", r.Render());
    }
}
