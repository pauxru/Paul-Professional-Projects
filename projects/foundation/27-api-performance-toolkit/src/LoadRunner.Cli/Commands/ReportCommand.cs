using LoadRunner.Core.Results;
using LoadRunner.Reporting.Html;
using LoadRunner.Reporting.Markdown;

namespace LoadRunner.Cli.Commands;

public static class ReportCommand
{
    public static async Task<int> ExecuteAsync(string resultPath, string? outDir, CancellationToken cancellationToken)
    {
        if (!File.Exists(resultPath) && !File.Exists(resultPath + ".json"))
        {
            Console.Error.WriteLine($"Result file not found: {resultPath}");
            return 2;
        }
        var storeDir = Path.GetDirectoryName(Path.GetFullPath(resultPath)) ?? Directory.GetCurrentDirectory();
        var store = new RunResultStore(storeDir);
        var result = store.Load(resultPath);
        var target = outDir ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(target);
        var htmlPath = Path.Combine(target, $"{result.RunId}.html");
        var mdPath = Path.Combine(target, $"{result.RunId}.md");
        await File.WriteAllTextAsync(htmlPath, HtmlReport.Render(result), cancellationToken);
        await File.WriteAllTextAsync(mdPath, MarkdownReport.Render(result), cancellationToken);
        Console.WriteLine($"HTML: {htmlPath}");
        Console.WriteLine($"Markdown: {mdPath}");
        return 0;
    }
}
