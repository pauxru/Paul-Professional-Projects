using LoadRunner.Core.Results;
using LoadRunner.Reporting.Markdown;

namespace LoadRunner.Cli.Commands;

public static class CompareCommand
{
    public static async Task<int> ExecuteAsync(string baselinePath, string candidatePath, string? outDir, CancellationToken cancellationToken)
    {
        if (!File.Exists(baselinePath) && !File.Exists(baselinePath + ".json"))
        {
            Console.Error.WriteLine($"Baseline file not found: {baselinePath}");
            return 2;
        }
        if (!File.Exists(candidatePath) && !File.Exists(candidatePath + ".json"))
        {
            Console.Error.WriteLine($"Candidate file not found: {candidatePath}");
            return 2;
        }
        var storeDir = Path.GetDirectoryName(Path.GetFullPath(baselinePath)) ?? Directory.GetCurrentDirectory();
        var store = new RunResultStore(storeDir);
        var baseline = store.Load(baselinePath);
        var candidate = store.Load(candidatePath);
        var report = ComparisonReportBuilder.Build(baseline, candidate);
        var target = outDir ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(target);
        var mdPath = Path.Combine(target, $"compare-{baseline.RunId}-vs-{candidate.RunId}.md");
        var htmlPath = Path.Combine(target, $"compare-{baseline.RunId}-vs-{candidate.RunId}.html");
        await File.WriteAllTextAsync(mdPath, report.Markdown, cancellationToken);
        await File.WriteAllTextAsync(htmlPath, report.Html, cancellationToken);
        Console.WriteLine($"Comparison Markdown: {mdPath}");
        Console.WriteLine($"Comparison HTML: {htmlPath}");
        Console.WriteLine();
        Console.WriteLine(report.Markdown);
        var combined = report.MannWhitney.Verdict == report.Bootstrap.Verdict
            ? report.MannWhitney.Verdict
            : report.MannWhitney.Verdict == LoadRunner.Core.Statistics.SignificanceTest.Verdict.NoSignificantChange
                ? report.Bootstrap.Verdict
                : report.MannWhitney.Verdict;
        return combined == LoadRunner.Core.Statistics.SignificanceTest.Verdict.Regressed ? 1 : 0;
    }
}
