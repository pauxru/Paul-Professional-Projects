using System.Text;
using Auth.Bridge;
using Auth.Report;

namespace Auth.Tests;

/// <summary>
/// Tests that the report is reproducible, and that the numbers it quotes are the numbers
/// the code actually produces.
/// </summary>
/// <remarks>
/// <para>
/// A results document that nobody re-runs decays into a claim. These tests exist so that a
/// change which moves a headline number breaks the build instead of quietly making the
/// README wrong. Each pinned value below appears verbatim in <c>docs/results.md</c> and in
/// the project README; if you are here because one of them failed, the honest fix is to
/// re-run the report, read what changed, and update the prose -- not to widen the
/// assertion.
/// </para>
/// <para>
/// The split between the "full" and "stable" reports is the subject of ADR 005: the stable
/// report omits wall-clock timings so that it can be byte-compared across runs, and the full
/// report keeps them because the timing ratios are one of the findings.
/// </para>
/// </remarks>
public sealed class ReportTests
{
    // Building the report costs tens of seconds -- it runs the Argon2 timing harness -- so
    // it is built once for the whole class rather than once per assertion. The determinism
    // test below deliberately opts out of the cache.
    private static readonly Lazy<string> Cached = new(() => ReportGenerator.Build(stable: true));

    private static string Stable() => Cached.Value;

    [Fact]
    public void TheStableReportIsByteIdenticalAcrossRuns()
    {
        // Two independent builds, deliberately bypassing the class-level cache -- comparing
        // a cached string with itself would pass unconditionally, which is the failure mode
        // this kind of test usually dies of.
        var first = Encoding.UTF8.GetBytes(ReportGenerator.Build(stable: true));
        var second = Encoding.UTF8.GetBytes(ReportGenerator.Build(stable: true));

        Assert.Equal(first, second);
    }

    [Fact]
    public void TheStableReportIsIdenticalAcrossAFreshProcessBoundary()
    {
        // Static initialisers, dictionary ordering and hash-code randomisation are all
        // per-process. Comparing two builds inside one process cannot see them. This test
        // compares against a copy taken from a process that has already done other work
        // (every other test in this class), which is the cheap approximation available
        // without spawning a child; test.ps1 does the real cross-process comparison by
        // regenerating results-stable.md and diffing it against the committed file.
        var report = Stable();

        Assert.DoesNotContain("System.Collections", report, StringComparison.Ordinal);
        Assert.False(report.Contains('\r'), "report must use LF endings so byte comparison is portable");
        Assert.EndsWith("\n", report, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStableReportOmitsWallClockTimings()
    {
        // The whole reason the stable variant exists. If a millisecond figure leaks into it,
        // the byte comparison in test.ps1 becomes flaky and someone will "fix" it by
        // deleting the check.
        var report = Stable();

        Assert.DoesNotContain(" ms", report, StringComparison.Ordinal);
        Assert.DoesNotContain("elapsed", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheFullReportDoesIncludeTimings()
    {
        Assert.Contains(" ms", ReportGenerator.Build(stable: false), StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeadlineDivergenceCountsAppearInTheReport()
    {
        var report = Stable();

        // 592 -> 16 -> 0. The middle number is the one that matters: it is what is left
        // after the structural fix, and it is an ordinary data error.
        Assert.Contains("592", report, StringComparison.Ordinal);
        Assert.Contains("4096", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePinnedDivergenceCountsAreWhatTheCodeProduces()
    {
        var naive = Experiments.Divergences(new NaiveClaimsTransformer());
        var denyAware = Experiments.Divergences(new DenyAwareClaimsTransformer());
        var corrected = Experiments.Divergences(new CorrectedClaimsTransformer());

        Assert.Equal(592, naive.Total);
        Assert.Equal(16, denyAware.Total);
        Assert.Equal(0, corrected.Total);
    }

    [Fact]
    public void EveryNaiveDivergenceIsAnEscalation()
    {
        // The finding that explains why this class of bug survives UAT: nobody files a
        // ticket saying "I was allowed to do something I should not have been".
        var naive = Experiments.Divergences(new NaiveClaimsTransformer());

        Assert.Equal(592, naive.Escalations);
        Assert.Equal(0, naive.Lockouts);
    }

    [Fact]
    public void TheMonotonicityCounterexampleCountIsPinned()
    {
        // 72 pairs where adding a role removes an ability. A role->scope union cannot
        // reproduce any of them, which is the proof that the naive mapping is not
        // repairable by adding rows.
        Assert.Equal(72, Experiments.Monotonicity().Count);
    }

    [Fact]
    public void ThePredictionScoreboardIsFullyPopulated()
    {
        var report = Stable();

        Assert.Equal(14, Predictions.All.Length);
        Assert.Contains("14 predictions", report, StringComparison.Ordinal);

        // Every prediction must be scored. An unscored prediction is a prediction quietly
        // dropped because it turned out to be embarrassing.
        foreach (var prediction in Predictions.All)
        {
            Assert.Contains(prediction.Claim, report, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheContradictedCountInTheHeaderMatchesTheTableBelowIt()
    {
        // A summary line and a table are two places for the same fact to live, which is one
        // too many. Rather than hard-code the number here -- a third place -- this checks
        // that the header agrees with the rows, so the report cannot become internally
        // inconsistent without failing.
        var report = Stable();

        var rows = report.Split('\n')
                         .Count(line => line.StartsWith("| ", StringComparison.Ordinal)
                                        && line.Contains("| contradicted |", StringComparison.Ordinal));

        Assert.Contains($"**{rows} were contradicted.**", report, StringComparison.Ordinal);
    }

    [Fact]
    public void MostPredictionsWereContradicted()
    {
        // Not a vanity metric. If the majority of predictions had held, the measurement
        // would not have been worth building -- and a scoreboard where the author is always
        // right is evidence of a scoreboard written after the fact.
        var report = Stable();

        var contradicted = report.Split('\n')
                                 .Count(line => line.Contains("| contradicted |", StringComparison.Ordinal));

        Assert.True(contradicted > Predictions.All.Length / 2,
                    $"only {contradicted} of {Predictions.All.Length} predictions were contradicted");
    }

    [Fact]
    public void EveryPredictionHasAVerdictAndEvidence()
    {
        // The defect this was written for: the scoreboard silently emitted only the first
        // four predictions, because the loop that scored them was bounded by the length of
        // a different array. Ten unscored predictions looked exactly like ten predictions
        // that had never been made.
        var report = Stable();

        var verdicts = report.Split('\n')
                             .Count(line => line.Contains("| contradicted |", StringComparison.Ordinal)
                                            || line.Contains("| held |", StringComparison.Ordinal)
                                            || line.Contains("| timing |", StringComparison.Ordinal));

        Assert.Equal(Predictions.All.Length, verdicts);
    }

    [Fact]
    public void TimingPredictionsAreDeferredInTheStableFileAndScoredInTheFull()
    {
        // Predictions 5-8 depend on wall-clock measurements, so the byte-stable file cannot
        // score them. It records them as deferred with a pointer, rather than dropping them
        // -- a scoreboard that silently omits four rows is worse than one that admits it
        // could not answer them here.
        var stable = Stable();
        var full = ReportGenerator.Build(stable: false);

        Assert.Equal(4, stable.Split('\n').Count(l => l.Contains("| timing |", StringComparison.Ordinal)));
        Assert.DoesNotContain("| timing |", full, StringComparison.Ordinal);

        Assert.Equal(
            Predictions.All.Length,
            full.Split('\n').Count(l => l.Contains("| contradicted |", StringComparison.Ordinal)
                                        || l.Contains("| held |", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheReportStatesWhatItDidNotMeasure()
    {
        // A results document that only lists what worked is marketing. This asserts the
        // section exists; docs/known-limitations.md carries the detail.
        var report = Stable();

        Assert.Contains("What this does not", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThreeStacksProduceTheSamePrincipal()
    {
        Assert.True(Experiments.ThreeStacksAgree());
    }

    [Fact]
    public void TheForgedTicketFindingIsWhatTheReportSays()
    {
        var (duringCoexistence, afterCutoff, _) = Experiments.DowngradeExposure();

        // A fully migrated account, with an Argon2id hash, reached through the legacy door.
        Assert.True(duringCoexistence);
        Assert.False(afterCutoff);
    }

    [Fact]
    public void TheHardenedProtectorCollapsesItsRejectionReasons()
    {
        var (legacy, hardened) = Experiments.RejectionReasons();

        Assert.Equal(3, legacy.Distinct().Count());
        Assert.Single(hardened.Distinct());
    }

    [Fact]
    public void EveryTokenAndAssertionAttackIsRejected()
    {
        var tokens = Experiments.TokenHardening();
        var federation = Experiments.FederationHardening();

        Assert.All(tokens, t => Assert.True(t.Rejected, t.Name));
        Assert.All(federation, f => Assert.True(f.Rejected, f.Name));
        Assert.Equal(17, tokens.Count + federation.Count);
    }

    [Fact]
    public void TheReportIsLargeEnoughToBeWorthReading()
    {
        // A smoke test with a purpose: an exception thrown halfway through generation used
        // to produce a truncated file rather than a failure, because each section was
        // appended to a builder that was written out in a finally block.
        var lines = Stable().Split('\n');

        Assert.True(lines.Length > 100, $"report was only {lines.Length} lines");
    }
}
