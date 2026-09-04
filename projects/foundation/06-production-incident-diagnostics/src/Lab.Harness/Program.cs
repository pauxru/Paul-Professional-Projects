using Lab.Diagnostics.Measurement;
using Lab.Scenarios;

var parsed = CommandLine.Parse(args);
var catalog = new ScenarioCatalog();
var root = FindProjectRoot(Directory.GetCurrentDirectory());
var scenarioIds = string.Equals(parsed.ScenarioId, "ALL", StringComparison.OrdinalIgnoreCase)
    ? catalog.All.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray()
    : [parsed.ScenarioId];
var anyFailed = false;

foreach (var scenarioId in scenarioIds)
{
    var scenario = catalog.GetRequired(scenarioId);
    var options = new ScenarioRunOptions(scenario.Id, parsed.Mode, parsed.Requests, parsed.Timeout);
    ScenarioReport report;
    try
    {
        using var runBudget = new CancellationTokenSource(parsed.Timeout + TimeSpan.FromSeconds(2));
        report = await scenario.RunAsync(options, runBudget.Token);
    }
    catch (OperationCanceledException)
    {
        anyFailed = true;
        report = FailureReport(scenario, options, "The hard harness deadline cancelled this scenario before it completed.");
    }
    catch (Exception exception)
    {
        anyFailed = true;
        report = FailureReport(scenario, options, $"{exception.GetType().Name}: {exception.Message}");
    }

    var directory = parsed.OutputDirectory is null
        ? Path.Combine(root, "incidents", ScenarioFolder.NameFor(scenarioId), "evidence")
        : Path.Combine(parsed.OutputDirectory, ScenarioFolder.NameFor(scenarioId));
    var paths = await ScenarioReportWriter.WriteAsync(report, directory, CancellationToken.None);
    Console.WriteLine($"{scenario.Id} {report.Mode}: {report.ElapsedMilliseconds:F2} ms; evidence:");
    Console.WriteLine($"  {paths.JsonPath}");
    Console.WriteLine($"  {paths.MarkdownPath}");
}

return anyFailed ? 1 : 0;

static ScenarioReport FailureReport(IIncidentScenario scenario, ScenarioRunOptions options, string evidence) =>
    new()
    {
        ScenarioId = scenario.Id,
        ScenarioName = scenario.Name,
        Mode = options.Mode,
        RequestedOperations = options.Requests,
        StartedAtUtc = DateTimeOffset.UtcNow,
        ElapsedMilliseconds = options.Timeout.TotalMilliseconds,
        CompletedWithinBudget = false,
        Evidence = [evidence],
        Limitations = ["The scenario did not complete; inspect the captured error before relying on this run."]
    };

static string FindProjectRoot(string startDirectory)
{
    var directory = new DirectoryInfo(startDirectory);
    while (directory is not null)
    {
        if (directory.GetFiles("*.slnx").Length > 0 || directory.GetFiles("*.sln").Length > 0)
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return startDirectory;
}

internal sealed record CommandLine(string ScenarioId, ScenarioMode Mode, int Requests, TimeSpan Timeout, string? OutputDirectory)
{
    public static CommandLine Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < arguments.Length; index++)
        {
            if (!arguments[index].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= arguments.Length)
            {
                throw new ArgumentException($"Missing value for {arguments[index]}.");
            }

            values[arguments[index][2..]] = arguments[++index];
        }

        var scenarioId = values.GetValueOrDefault("scenario", "INC-001").ToUpperInvariant();
        var modeText = values.GetValueOrDefault("mode", "broken");
        if (!Enum.TryParse<ScenarioMode>(modeText, ignoreCase: true, out var mode))
        {
            throw new ArgumentException("--mode must be broken or fixed.");
        }

        var requests = int.TryParse(values.GetValueOrDefault("requests"), out var suppliedRequests)
            ? Math.Max(1, suppliedRequests)
            : 20;
        var timeoutSeconds = int.TryParse(values.GetValueOrDefault("timeout-seconds"), out var suppliedTimeout)
            ? Math.Clamp(suppliedTimeout, 1, 90)
            : 30;
        var output = values.GetValueOrDefault("output");
        return new CommandLine(scenarioId, mode, requests, TimeSpan.FromSeconds(timeoutSeconds), output);
    }
}

internal static class ScenarioFolder
{
    private static readonly IReadOnlyDictionary<string, string> Folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["INC-001"] = "INC-001-n-plus-one",
        ["INC-002"] = "INC-002-missing-index",
        ["INC-003"] = "INC-003-connection-pool-exhaustion",
        ["INC-004"] = "INC-004-memory-leak",
        ["INC-005"] = "INC-005-blocking-async",
        ["INC-006"] = "INC-006-thread-pool-starvation",
        ["INC-007"] = "INC-007-downstream-timeout",
        ["INC-008"] = "INC-008-cascading-retry-storm",
        ["INC-009"] = "INC-009-poison-queue-message",
        ["INC-010"] = "INC-010-cache-stampede"
    };

    public static string NameFor(string scenarioId) =>
        Folders.TryGetValue(scenarioId, out var folder)
            ? folder
            : scenarioId;
}
