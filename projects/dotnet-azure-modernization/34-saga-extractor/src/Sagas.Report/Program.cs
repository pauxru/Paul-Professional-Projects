using System.Text;
using Sagas.Core;

// Writes docs/results.md, or prints it, depending on the flag.
//
// The file is written from here rather than by shell redirection on purpose.
// PowerShell's Out-File adds a UTF-8 BOM and CRLF line endings, which would make
// the byte-comparison in the results-integrity test fail on Windows and pass
// everywhere else -- the worst possible outcome for a check whose entire job is
// to be trustworthy.

var toStdout = args.Contains("--stdout");

if (args.Contains("--demo"))
{
    RunDemo();
    return 0;
}

var text = Experiments.Render();

if (toStdout)
{
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.Out.Write(text);
    return 0;
}

var root = FindRepoRoot();
if (root is null)
{
    Console.Error.WriteLine("could not locate the repository root (no SagaExtractor.slnx above the cwd)");
    return 1;
}

var target = Path.Combine(root, "docs", "results.md");
Directory.CreateDirectory(Path.GetDirectoryName(target)!);

var existing = File.Exists(target) ? File.ReadAllText(target) : null;
if (existing == text)
{
    Console.WriteLine($"{target} is up to date ({text.Length:N0} bytes)");
    return 0;
}

File.WriteAllText(target, text, new UTF8Encoding(false));
Console.WriteLine(existing is null
    ? $"wrote {target} ({text.Length:N0} bytes)"
    : $"updated {target} ({existing.Length:N0} -> {text.Length:N0} bytes)");
return 0;

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "SagaExtractor.slnx")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    return null;
}

static void RunDemo()
{
    var opts = new CheckerOptions { CrashBudget = 1 };

    Rule("1. the transaction, as the extractor sees it");
    var plan = Extractor.Reorder(Extractor.OrderTransaction);
    Console.WriteLine(plan.Describe());
    Console.WriteLine();
    foreach (var line in plan.Rationale)
    {
        Console.WriteLine("  - " + line);
    }

    Rule("2. seven versions, each fixing one thing the checker found");
    Console.WriteLine($"  {"ver",-5} {"states",8} {"violations",11}  shortest  fix");
    foreach (var (label, note, build) in Catalogue.Progression)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = Checker.Check(build(), opts);
        sw.Stop();
        var shortest = res.Violations.Count == 0 ? "--" : res.Violations.Min(v => v.Depth).ToString();
        Console.WriteLine($"  {label,-5} {res.StatesExplored,8} {res.Violations.Count,11}  {shortest,8}  {note}");
    }

    Rule("3. the shortest counterexample the checker can produce for v6");
    var v6 = Catalogue.V6_IdempotentReservation();
    var stuck = Checker.Check(v6, opts).Violations.OrderBy(v => v.Depth).First();
    Console.WriteLine($"  {stuck.Kind} at depth {stuck.Depth}");
    Console.WriteLine($"  {stuck.Detail}");
    Console.WriteLine();
    Console.WriteLine(stuck.Render(v6.Initial));

    Rule("4. the counter-intuitive one: skipping the uncertain step is unsafe");
    var v7 = Catalogue.V7_QueryablePivot();
    foreach (var scope in Enum.GetValues<CompensationScope>())
    {
        var res = Checker.Check(v7, opts with { Scope = scope });
        Console.WriteLine($"  {scope,-32} {res.Violations.Count} violation(s)");
        foreach (var v in res.Violations.OrderBy(v => v.Depth))
        {
            Console.WriteLine($"      [depth {v.Depth}] {v.Detail}");
        }
    }

    Rule("5. is every design fix load-bearing?");
    foreach (var (label, saga) in DesignMutations.All())
    {
        var res = Checker.Check(saga, opts);
        var verdict = res.Violations.Count == 0
            ? "NOT LOAD-BEARING"
            : $"{res.Violations.Count} violation(s), shortest {res.Violations.Min(v => v.Depth)}";
        Console.WriteLine($"  {label,-56} {verdict}");
    }

    Rule("done");
    Console.WriteLine("  Full write-up with predictions registered before the numbers: docs/results.md");
}

static void Rule(string title)
{
    Console.WriteLine();
    Console.WriteLine(new string('-', 78));
    Console.WriteLine("  " + title);
    Console.WriteLine(new string('-', 78));
}
