using System.Text;
using Sagas.Core;

namespace Sagas.Tests;

/// <summary>
/// The test that makes the rest of the repository trustworthy.
///
/// `docs/results.md` states specific numbers -- state counts, violation counts,
/// growth ratios -- and then draws conclusions from them in prose. The usual
/// failure mode for a document like that is not that it is written dishonestly;
/// it is that the code changes six months later and nobody regenerates it, so it
/// becomes wrong by neglect while still looking authoritative.
///
/// This re-renders the report and byte-compares it. Any change to the checker
/// that moves a number fails the build until the file is regenerated.
/// </summary>
public class ResultsIntegrityTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SagaExtractor.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void ResultsMatchTheCommittedDocument()
    {
        var path = Path.Combine(RepoRoot(), "docs", "results.md");
        Assert.True(File.Exists(path), $"{path} has never been generated; run `dotnet run --project src/Sagas.Report`");

        var onDisk = File.ReadAllText(path);
        var fresh = Experiments.Render();

        if (onDisk == fresh)
        {
            return;
        }

        // A 25KB diff dumped into a test runner is unreadable. Report the first
        // divergent line and its neighbours, which is enough to see what moved.
        var a = onDisk.Replace("\r\n", "\n").Split('\n');
        var b = fresh.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i])
        {
            i++;
        }

        var sb = new StringBuilder();
        sb.AppendLine("docs/results.md is stale. Regenerate with:");
        sb.AppendLine("    dotnet run --project src/Sagas.Report -c Release");
        sb.AppendLine($"First divergence at line {i + 1}:");
        sb.AppendLine($"  on disk:   {(i < a.Length ? a[i] : "<end of file>")}");
        sb.AppendLine($"  generated: {(i < b.Length ? b[i] : "<end of file>")}");
        Assert.Fail(sb.ToString());
    }

    [Fact]
    public void TheDocumentHasNoWindowsLineEndings()
    {
        // Written from C# with an explicit encoding rather than by shell
        // redirection, precisely so this holds. PowerShell's Out-File would add a
        // BOM and CRLF and make the comparison above platform-dependent.
        var path = Path.Combine(RepoRoot(), "docs", "results.md");
        Assert.DoesNotContain('\r', File.ReadAllText(path));
    }

    [Fact]
    public void TheDocumentHasNoByteOrderMark()
    {
        var path = Path.Combine(RepoRoot(), "docs", "results.md");
        var bytes = File.ReadAllBytes(path);
        Assert.False(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "results.md starts with a UTF-8 BOM; it was probably written by shell redirection");
    }

    [Fact]
    public void EveryPredictionInTheReportIsResolved()
    {
        // Render() throws if an Expect has no Found, so reaching this line at all
        // is the assertion. Stated explicitly so the guarantee is visible in the
        // test list rather than buried in the DSL.
        var text = Experiments.Render();
        var predicted = Count(text, "**Predicted.**");
        var resolved = Count(text, "**Held.**") + Count(text, "**Contradicted.**");
        Assert.Equal(predicted, resolved);
        Assert.True(predicted >= 8, $"only {predicted} predictions were registered");
    }

    [Fact]
    public void TheReportContradictsItselfEnoughToBeCredible()
    {
        // Not a joke. A report in which every prediction held is a report whose
        // predictions were written afterwards. This asserts the document contains
        // real surprises, which is the only external evidence that the Expect
        // calls came first.
        var text = Experiments.Render();
        Assert.True(
            Count(text, "**Contradicted.**") >= 4,
            "fewer than four predictions were contradicted, which is suspicious rather than impressive");
    }

    [Fact]
    public void TheReportDocumentsItsOwnBound()
    {
        var text = Experiments.Render();
        Assert.Contains("crash budget", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("What this does not do", text, StringComparison.Ordinal);
    }

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        var i = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (i >= 0)
        {
            n++;
            i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal);
        }

        return n;
    }
}
