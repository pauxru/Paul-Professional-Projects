using System.Globalization;
using ReconEngine.Application.DataGeneration;

// ReconEngine synthetic data generator.
// Usage:
//   dotnet run --project src/ReconEngine.DataGen -- --rows 250000 --seed 42 \
//       --inject duplicates,amount-mismatch,missing,fees,refunds [--out ./data] [--no-fixed]
//
// Emits internal.csv + external.csv (+ external.fixed.txt) and a manifest.json recording the EXACT
// number of each injected defect class, so downstream reconciliation counts can be asserted against it.

var argv = args;
var options = ParseArgs(argv);

var rows = options.TryGetValue("rows", out var r) ? int.Parse(r, CultureInfo.InvariantCulture) : 10_000;
var seed = options.TryGetValue("seed", out var s) ? int.Parse(s, CultureInfo.InvariantCulture) : 42;
var inject = options.TryGetValue("inject", out var inj) ? inj : "all";
var outDir = options.TryGetValue("out", out var o)
    ? o
    : Path.Combine(Directory.GetCurrentDirectory(), "data");
var writeFixed = !options.ContainsKey("no-fixed");

var genOptions = GenerationOptions.Parse(rows, seed, inject);

Console.WriteLine($"ReconEngine DataGen — rows={rows} seed={seed} inject=[{inject}] out={outDir}");
var sw = System.Diagnostics.Stopwatch.StartNew();

var generator = new SyntheticDataGenerator();
var dataset = generator.Generate(genOptions);
var files = await DatasetFileWriter.WriteAsync(dataset, outDir, writeFixed);

sw.Stop();
var m = dataset.Manifest;

Console.WriteLine();
Console.WriteLine($"  internal rows : {m.TotalInternalRows:N0}  -> {files.InternalCsv}");
Console.WriteLine($"  external rows : {m.TotalExternalRows:N0}  -> {files.ExternalCsv}");
if (writeFixed)
    Console.WriteLine($"  external (fw) : {m.TotalExternalRows:N0}  -> {files.ExternalFixedWidth}");
Console.WriteLine($"  manifest      : {files.Manifest}");
Console.WriteLine();
Console.WriteLine("  Ground-truth defect counts:");
Console.WriteLine($"    clean pairs         : {m.CleanPairs:N0}");
Console.WriteLine($"    duplicate internal  : {m.DuplicateInternal}");
Console.WriteLine($"    duplicate external  : {m.DuplicateExternal}");
Console.WriteLine($"    amount mismatch     : {m.AmountMismatch}");
Console.WriteLine($"    missing in external : {m.MissingInExternal}");
Console.WriteLine($"    missing in internal : {m.MissingInInternal}");
Console.WriteLine($"    currency mismatch   : {m.CurrencyMismatch}");
Console.WriteLine($"    status mismatch     : {m.StatusMismatch}");
Console.WriteLine($"    date out of window  : {m.DateOutOfWindow}");
Console.WriteLine($"    fee-adjusted clean  : {m.FeeAdjustedClean}");
Console.WriteLine($"    fee variance        : {m.FeeVariance}");
Console.WriteLine($"    refunds             : {m.Refunds}");
Console.WriteLine();
Console.WriteLine($"  expected auto matches : {m.ExpectedAutoMatches:N0}");
Console.WriteLine($"  expected exceptions   : {m.ExpectedExceptions:N0}");
Console.WriteLine($"  generated in {sw.Elapsed.TotalSeconds:F2}s");

return 0;

static Dictionary<string, string> ParseArgs(string[] argv)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < argv.Length; i++)
    {
        var token = argv[i];
        if (!token.StartsWith("--", StringComparison.Ordinal))
            continue;
        var key = token[2..];
        if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            map[key] = argv[++i];
        }
        else
        {
            map[key] = "true"; // flag with no value
        }
    }
    return map;
}

