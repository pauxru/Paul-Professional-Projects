using Archaeologist.Core;

namespace Archaeologist.Tests;

/// <summary>
/// docs/results.md is checked in, which means it can drift from the code that produced it.
/// A stale report is worse than no report: it is a confident set of numbers that no longer
/// describes anything. These tests make drift a build failure.
/// </summary>
public class ReportFreshnessTest : IClassFixture<EstateFixture>
{
    private readonly EstateFixture f;
    public ReportFreshnessTest(EstateFixture fixture) => f = fixture;

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "AssemblyArchaeologist.slnx")))
            d = d.Parent;
        Assert.NotNull(d);
        return d.FullName;
    }

    private string Generate() => Experiments.Run(f.Estate, f.Spec.EntryPoint);

    [Fact]
    public void TheCheckedInReportIsWhatTheCodeProducesToday()
    {
        var onDisk = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "results.md")).Replace("\r\n", "\n");
        Assert.Equal(Generate(), onDisk);
    }

    [Fact]
    public void GeneratingTheReportTwiceGivesTheSameBytes()
    {
        Assert.Equal(Generate(), Generate());
    }

    [Fact]
    public void TheReportIsGeneratedFromAFreshlyBuiltEstateNotACachedOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), "arch-fresh-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var spec = CorpusSpec.Contoso();
            CorpusBuilder.Emit(spec, dir);
            Assert.Equal(Generate(), Experiments.Run(IlReader.Read(dir), spec.EntryPoint));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EveryPredictionInTheReportHasAVerdict()
    {
        var text = Generate();
        var expected = System.Text.RegularExpressions.Regex.Matches(text, @"\*\*(P\d+) -- expected\.\*\*")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(expected);
        foreach (var id in expected)
            Assert.Matches($@"\*\*{id} -- (HELD|CONTRADICTED)\.\*\*", text);
    }

    [Fact]
    public void ThePredictionSetContainsBothOutcomesSoItIsNotRigged()
    {
        // A report where everything was predicted correctly is a report written after the
        // fact. A report where nothing was is a set of straw men. Both must appear.
        var text = Generate();
        Assert.Contains("-- HELD.", text);
        Assert.Contains("-- CONTRADICTED.", text);
    }

    [Fact]
    public void PredictionIdsAreContiguousFromOne()
    {
        var ids = System.Text.RegularExpressions.Regex.Matches(Generate(), @"\*\*P(\d+) -- expected\.\*\*")
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal(Enumerable.Range(1, ids.Count), ids);
    }

    [Fact]
    public void TheReportIsAsciiOnly()
    {
        Assert.All(Generate(), c => Assert.True(c < 128, $"U+{(int)c:X4}"));
    }

    [Fact]
    public void TheReportUsesLfEndingsOnly()
    {
        Assert.DoesNotContain('\r', Generate());
    }

    [Fact]
    public void TheReportIsSubstantialEnoughToBeWorthCheckingIn()
    {
        Assert.True(Generate().Length > 10_000);
    }
}
