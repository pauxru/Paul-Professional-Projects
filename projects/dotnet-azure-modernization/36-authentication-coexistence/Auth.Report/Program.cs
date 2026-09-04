using Auth.Report;

var mode = args.Length > 0 ? args[0] : "report";
var outputDirectory = args.Length > 1
    ? args[1]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs");

switch (mode)
{
    case "report":
    {
        Directory.CreateDirectory(outputDirectory);

        var full = Path.GetFullPath(Path.Combine(outputDirectory, "results.md"));
        var stable = Path.GetFullPath(Path.Combine(outputDirectory, "results-stable.md"));

        // Newline is forced rather than inherited so the byte comparison in test.ps1 means
        // the same thing on every machine.
        File.WriteAllText(full, ReportGenerator.Build(stable: false));
        File.WriteAllText(stable, ReportGenerator.Build(stable: true));

        Console.WriteLine($"wrote {full}");
        Console.WriteLine($"wrote {stable}");
        return 0;
    }

    case "stable-only":
    {
        Console.Out.Write(ReportGenerator.Build(stable: true));
        return 0;
    }

    case "demo":
        return Demo.Run();

    default:
        Console.Error.WriteLine($"unknown mode '{mode}'; expected report, stable-only or demo");
        return 2;
}
