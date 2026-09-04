using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lab.Diagnostics.Measurement;

public enum ScenarioMode
{
    Broken,
    Fixed
}

public sealed record ScenarioRunOptions(
    string ScenarioId,
    ScenarioMode Mode,
    int Requests,
    TimeSpan Timeout)
{
    public int BoundedRequests(int minimum, int maximum) => Math.Clamp(Requests, minimum, maximum);
}

public sealed class ScenarioReport
{
    public required string ScenarioId { get; init; }

    public required string ScenarioName { get; init; }

    public required ScenarioMode Mode { get; init; }

    public required int RequestedOperations { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required double ElapsedMilliseconds { get; init; }

    public bool CompletedWithinBudget { get; init; } = true;

    public Dictionary<string, object?> Metrics { get; init; } = new(StringComparer.Ordinal);

    public List<string> Evidence { get; init; } = [];

    public List<string> Limitations { get; init; } = [];
}

public static class ScenarioReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static async Task<(string JsonPath, string MarkdownPath)> WriteAsync(
        ScenarioReport report,
        string directory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);
        var mode = report.Mode.ToString().ToLowerInvariant();
        var stem = $"{report.ScenarioId.ToLowerInvariant()}-{mode}";
        var jsonPath = Path.Combine(directory, $"{stem}.json");
        var markdownPath = Path.Combine(directory, $"{stem}.md");

        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(report, JsonOptions),
            Encoding.UTF8,
            cancellationToken);
        await File.WriteAllTextAsync(
            markdownPath,
            ToMarkdown(report),
            Encoding.UTF8,
            cancellationToken);

        return (jsonPath, markdownPath);
    }

    public static string ToMarkdown(ScenarioReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {report.ScenarioId} — {report.ScenarioName} ({report.Mode})");
        builder.AppendLine();
        builder.AppendLine($"- Started (UTC): {report.StartedAtUtc:O}");
        builder.AppendLine($"- Requested operations: {report.RequestedOperations}");
        builder.AppendLine($"- Elapsed: {report.ElapsedMilliseconds:F2} ms");
        builder.AppendLine($"- Completed within budget: {report.CompletedWithinBudget}");
        builder.AppendLine();
        builder.AppendLine("## Metrics");
        builder.AppendLine();

        foreach (var (key, value) in report.Metrics)
        {
            if (value is string text && text.Contains(Environment.NewLine, StringComparison.Ordinal))
            {
                builder.AppendLine($"### {key}");
                builder.AppendLine("```text");
                builder.AppendLine(text);
                builder.AppendLine("```");
            }
            else
            {
                builder.AppendLine($"- **{key}:** {FormatValue(value)}");
            }
        }

        AppendList(builder, "Evidence", report.Evidence);
        AppendList(builder, "Limitations", report.Limitations);
        return builder.ToString();
    }

    private static void AppendList(StringBuilder builder, string heading, IReadOnlyCollection<string> lines)
    {
        builder.AppendLine();
        builder.AppendLine($"## {heading}");
        builder.AppendLine();
        if (lines.Count == 0)
        {
            builder.AppendLine("- None.");
            return;
        }

        foreach (var line in lines)
        {
            builder.AppendLine($"- {line}");
        }
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        double number => number.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
        float number => number.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };
}
