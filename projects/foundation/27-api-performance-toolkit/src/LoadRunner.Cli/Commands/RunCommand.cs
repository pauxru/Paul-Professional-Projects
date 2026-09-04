using System.Text.Json;
using LoadRunner.Core.Execution;
using LoadRunner.Core.Results;
using LoadRunner.Core.Scenarios;
using LoadRunner.Reporting.Html;
using LoadRunner.Reporting.Markdown;

namespace LoadRunner.Cli.Commands;

public static class RunCommand
{
    public static async Task<int> ExecuteAsync(string scenarioPath, string? resultsDir, string? outDir, string? gitCommit, CancellationToken cancellationToken)
    {
        if (!File.Exists(scenarioPath))
        {
            Console.Error.WriteLine($"Scenario file not found: {scenarioPath}");
            return 2;
        }
        var scenario = ScenarioLoader.FromFile(scenarioPath);
        Console.WriteLine($"Running scenario '{scenario.Name}' against {scenario.BaseUrl}. Model={scenario.Load.Model}.");

        var runner = new ScenarioRunner();
        var result = await runner.RunAsync(scenario, cancellationToken);

        var storeDir = resultsDir ?? Path.Combine(Directory.GetCurrentDirectory(), "results");
        var store = new RunResultStore(storeDir);
        // annotate git commit if provided
        var finalResult = gitCommit is null
            ? result
            : result with { Environment = result.Environment with { GitCommit = gitCommit } };
        var savedPath = store.Save(finalResult);
        Console.WriteLine($"Saved run result: {savedPath}");

        var reportDir = outDir ?? storeDir;
        Directory.CreateDirectory(reportDir);
        var html = HtmlReport.Render(finalResult);
        var md = MarkdownReport.Render(finalResult);
        var htmlPath = Path.Combine(reportDir, $"{finalResult.RunId}.html");
        var mdPath = Path.Combine(reportDir, $"{finalResult.RunId}.md");
        await File.WriteAllTextAsync(htmlPath, html, cancellationToken);
        await File.WriteAllTextAsync(mdPath, md, cancellationToken);
        Console.WriteLine($"Saved HTML report: {htmlPath}");
        Console.WriteLine($"Saved Markdown report: {mdPath}");

        Console.WriteLine();
        PrintSummary(finalResult);

        var passed = finalResult.AllAssertionsPassed;
        return passed ? 0 : 1;
    }

    public static void PrintSummary(RunResult result)
    {
        Console.WriteLine($"Total requests: {result.Aggregate.Count:N0}  Errors: {result.Aggregate.Errors:N0} ({result.Aggregate.ErrorRate:P2})");
        Console.WriteLine($"Throughput: {result.Aggregate.ThroughputRps:F1} rps");
        Console.WriteLine($"Service latency  p50/p95/p99: {result.Aggregate.Service.P50Ms:F1} / {result.Aggregate.Service.P95Ms:F1} / {result.Aggregate.Service.P99Ms:F1} ms");
        Console.WriteLine($"Intended latency p95/p99:      {result.Aggregate.Intended.P95Ms:F1} / {result.Aggregate.Intended.P99Ms:F1} ms");
        if (result.Assertions.Count > 0)
        {
            Console.WriteLine("Assertions:");
            foreach (var a in result.Assertions)
                Console.WriteLine($"  [{(a.Passed ? "PASS" : "FAIL")}] {a.Metric} {a.Op} {a.Value:F3} => {a.Actual:F3}");
        }
        if (result.Knee is not null)
            Console.WriteLine($"Stress knee: {result.Knee.KneeRate?.ToString() ?? "unknown"} rps ({result.Knee.Method})");
        if (result.Capacity is not null)
            Console.WriteLine($"Capacity: max {result.Capacity.MaximumSustainedRate} rps (p95={result.Capacity.AchievedP95Ms:F1}ms, {result.Capacity.ProbesRun} probes)");
        if (result.SoakDrift is not null)
            Console.WriteLine($"Soak drift: latency {result.SoakDrift.LatencySlopeMsPerMinute:F2} ms/min, throughput {result.SoakDrift.ThroughputSlopeRpsPerMinute:F2} rps/min");
    }
}
