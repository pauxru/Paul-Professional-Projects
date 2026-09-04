using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ReconEngine.Application.DataGeneration;
using ReconEngine.Application.Ingestion;
using ReconEngine.Application.Reconciliation;
using ReconEngine.Domain.Abstractions;
using ReconEngine.Domain.Entities;
using ReconEngine.Domain.ValueObjects;
using ReconEngine.Infrastructure.Ingestion;

// ReconEngine performance harness.
// Usage:
//   dotnet run -c Release --project src/ReconEngine.PerfHarness -- [--out docs/performance.md] [--sizes 10000,100000,250000]
//
// Measures REAL throughput (rows/sec), wall time and peak working set for the reconcile hot path over
// 10k / 100k / 250k rows, plus a naive O(n²) vs indexed exact-match comparison. All numbers are measured
// on this host at run time — nothing is hard-coded.

var options = ParseArgs(args);
var outPath = options.TryGetValue("out", out var o)
    ? o
    : Path.Combine(Directory.GetCurrentDirectory(), "docs", "performance.md");
var sizes = options.TryGetValue("sizes", out var sz)
    ? sz.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
         .Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray()
    : new[] { 10_000, 100_000, 250_000 };

var clock = new SystemClock();
var calculator = ReconciliationCalculator.CreateDefault();

Console.WriteLine("ReconEngine performance harness");
Console.WriteLine($"  host: {Environment.ProcessorCount} logical cores | {RuntimeInformation.OSDescription}");
Console.WriteLine($"  runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine();

// Warm up the JIT / engine so the first measured size is not penalised.
Warmup(calculator);

var reconcileRows = new List<ReconcileResult>();
foreach (var n in sizes)
{
    var result = MeasureReconcile(calculator, n);
    reconcileRows.Add(result);
    Console.WriteLine($"  reconcile {n,8:N0} pairs | {result.WallMs,9:N1} ms | {result.RowsPerSec,12:N0} rows/s | peak WS {result.PeakWorkingSetMb,7:N1} MB");
}

var parse = MeasureParse(clock, 100_000);
Console.WriteLine($"  parse (CSV stream) {parse.Rows,8:N0} rows | {parse.WallMs,9:N1} ms | {parse.RowsPerSec,12:N0} rows/s");

var (naive, indexed, naiveN) = MeasureExactMatchBeforeAfter(20_000);
var speedup = naive.RowsPerSec == 0 ? 0 : indexed.RowsPerSec / naive.RowsPerSec;
Console.WriteLine($"  exact-match naive   O(n^2) | {naive.WallMs,9:N1} ms | {naive.RowsPerSec,12:N0} rows/s");
Console.WriteLine($"  exact-match indexed O(n)   | {indexed.WallMs,9:N1} ms | {indexed.RowsPerSec,12:N0} rows/s | {speedup:N1}x");

var report = BuildReport(reconcileRows, parse, naive, indexed, naiveN, speedup, sizes);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
await File.WriteAllTextAsync(outPath, report);
Console.WriteLine();
Console.WriteLine($"  wrote {outPath}");
return 0;

static void Warmup(ReconciliationCalculator calculator)
{
    var ds = new SyntheticDataGenerator().Generate(new GenerationOptions { Rows = 2_000, Seed = 1 });
    _ = calculator.Calculate(ds.Internal, ds.External, MatchingRuleSetDefinition.Default, 1_000_000);
}

static ReconcileResult MeasureReconcile(ReconciliationCalculator calculator, int n)
{
    // Clean-only dataset so the whole set flows through dedup + the matching pipeline + classification.
    var ds = new SyntheticDataGenerator().Generate(new GenerationOptions { Rows = n, Seed = 42 });
    var totalRows = ds.Internal.Count + ds.External.Count;

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var allocBefore = GC.GetTotalAllocatedBytes(true);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = calculator.Calculate(ds.Internal, ds.External, MatchingRuleSetDefinition.Default, 1_000_000);
    sw.Stop();

    var allocAfter = GC.GetTotalAllocatedBytes(true);
    using var proc = System.Diagnostics.Process.GetCurrentProcess();
    proc.Refresh();

    return new ReconcileResult(
        n, totalRows, sw.Elapsed.TotalMilliseconds,
        totalRows / Math.Max(sw.Elapsed.TotalSeconds, 1e-9),
        proc.PeakWorkingSet64 / 1024.0 / 1024.0,
        (allocAfter - allocBefore) / 1024.0 / 1024.0,
        result.MatchCount, result.ExceptionCount);
}

static ParseResult MeasureParse(IClock clock, int n)
{
    var ds = new SyntheticDataGenerator().Generate(new GenerationOptions { Rows = n, Seed = 7 });

    // Serialise internal rows to an in-memory CSV, then stream-parse it back (no disk, no DB).
    var profile = BuiltInProfiles.InternalCsv;
    var sb = new StringBuilder();
    sb.AppendLine("TransactionId,MerchantOrderId,Amount,Currency,TransactionDate,Status");
    foreach (var r in ds.Internal)
    {
        var major = decimal.Divide(r.AmountMinor, CurrencyInfo.ScaleFor(r.Currency))
            .ToString("F2", CultureInfo.InvariantCulture);
        var date = r.ValueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 12:00:00";
        sb.Append(r.RawReference).Append(',').Append(r.CounterpartyReference).Append(',')
          .Append(major).Append(',').Append(r.Currency).Append(',')
          .Append(date).Append(',').Append(r.Status).Append('\n');
    }
    var bytes = Encoding.UTF8.GetBytes(sb.ToString());

    var tokenizer = new CsvRowTokenizer();
    var mapped = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    CountAsync().GetAwaiter().GetResult();
    sw.Stop();

    return new ParseResult(mapped, sw.Elapsed.TotalMilliseconds, mapped / Math.Max(sw.Elapsed.TotalSeconds, 1e-9));

    async Task CountAsync()
    {
        using var ms = new MemoryStream(bytes);
        IReadOnlyDictionary<string, int>? header = null;
        var first = true;
        await foreach (var row in tokenizer.TokenizeAsync(ms, profile))
        {
            if (row.IsBlank) continue;
            if (first)
            {
                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < row.Fields.Count; i++) map[row.Fields[i].Trim()] = i;
                header = map;
                first = false;
                continue;
            }
            var res = RecordMapper.Map(row, profile, header, clock);
            if (res.Ok) mapped++;
        }
    }
}

static (PhaseResult Naive, PhaseResult Indexed, int N) MeasureExactMatchBeforeAfter(int n)
{
    var ds = new SyntheticDataGenerator().Generate(new GenerationOptions { Rows = n, Seed = 99 });
    var internals = ds.Internal;
    var externals = ds.External;
    var totalRows = internals.Count + externals.Count;

    // BEFORE: naive O(n*m) nested scan.
    var swNaive = System.Diagnostics.Stopwatch.StartNew();
    var naiveMatches = 0;
    var claimed = new bool[externals.Count];
    foreach (var i in internals)
    {
        for (var j = 0; j < externals.Count; j++)
        {
            if (claimed[j]) continue;
            var e = externals[j];
            if (e.AmountMinor == i.AmountMinor
                && string.Equals(e.Currency, i.Currency, StringComparison.Ordinal)
                && string.Equals(e.CanonicalReference, i.CanonicalReference, StringComparison.Ordinal))
            {
                claimed[j] = true;
                naiveMatches++;
                break;
            }
        }
    }
    swNaive.Stop();

    // AFTER: dictionary index keyed by (currency, reference, amount) — the shipped strategy.
    var swIndex = System.Diagnostics.Stopwatch.StartNew();
    var index = new Dictionary<(string, string, long), Queue<ReconRecord>>();
    foreach (var e in externals)
    {
        var key = (e.Currency, e.CanonicalReference, e.AmountMinor);
        if (!index.TryGetValue(key, out var q)) index[key] = q = new Queue<ReconRecord>();
        q.Enqueue(e);
    }
    var indexedMatches = 0;
    foreach (var i in internals)
    {
        var key = (i.Currency, i.CanonicalReference, i.AmountMinor);
        if (index.TryGetValue(key, out var q) && q.Count > 0) { q.Dequeue(); indexedMatches++; }
    }
    swIndex.Stop();

    var naive = new PhaseResult(swNaive.Elapsed.TotalMilliseconds, totalRows / Math.Max(swNaive.Elapsed.TotalSeconds, 1e-9), naiveMatches);
    var indexed = new PhaseResult(swIndex.Elapsed.TotalMilliseconds, totalRows / Math.Max(swIndex.Elapsed.TotalSeconds, 1e-9), indexedMatches);
    return (naive, indexed, n);
}

static string BuildReport(
    List<ReconcileResult> reconcile, ParseResult parse, PhaseResult naive, PhaseResult indexed,
    int naiveN, double speedup, int[] sizes)
{
    var sb = new StringBuilder();
    var utc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    sb.AppendLine("# Performance");
    sb.AppendLine();
    sb.AppendLine("> **These numbers are real** — they are produced by `ReconEngine.PerfHarness`, which measures the");
    sb.AppendLine("> engine on this host at run time. Re-run the command below to reproduce them; they are not hand-edited.");
    sb.AppendLine();
    sb.AppendLine("## Host");
    sb.AppendLine();
    sb.AppendLine($"- Logical cores: `{Environment.ProcessorCount}`");
    sb.AppendLine($"- OS: `{RuntimeInformation.OSDescription}`");
    sb.AppendLine($"- Runtime: `{RuntimeInformation.FrameworkDescription}`");
    sb.AppendLine($"- Architecture: `{RuntimeInformation.OSArchitecture}` / process `{RuntimeInformation.ProcessArchitecture}`");
    sb.AppendLine($"- Measured at: `{utc}`");
    sb.AppendLine();
    sb.AppendLine("## Command");
    sb.AppendLine();
    sb.AppendLine("```powershell");
    sb.AppendLine($"dotnet run -c Release --project src/ReconEngine.PerfHarness -- --sizes {string.Join(",", sizes)}");
    sb.AppendLine("```");
    sb.AppendLine();
    sb.AppendLine("## Reconcile hot path (dedup + 7-rule pipeline + classification + balance)");
    sb.AppendLine();
    sb.AppendLine("`pairs` is the number of internal transactions; `rows` counts both internal and external records fed to the engine.");
    sb.AppendLine();
    sb.AppendLine("| pairs | rows | wall time (ms) | throughput (rows/s) | peak working set (MB) | alloc (MB) | matches | exceptions |");
    sb.AppendLine("|------:|-----:|---------------:|--------------------:|----------------------:|-----------:|--------:|-----------:|");
    foreach (var r in reconcile)
    {
        sb.AppendLine($"| {r.Pairs:N0} | {r.TotalRows:N0} | {r.WallMs:N1} | {r.RowsPerSec:N0} | {r.PeakWorkingSetMb:N1} | {r.AllocMb:N1} | {r.Matches:N0} | {r.Exceptions:N0} |");
    }
    sb.AppendLine();
    sb.AppendLine("## Ingestion (streaming CSV parse + normalise, no DB)");
    sb.AppendLine();
    sb.AppendLine("| rows | wall time (ms) | throughput (rows/s) |");
    sb.AppendLine("|-----:|---------------:|--------------------:|");
    sb.AppendLine($"| {parse.Rows:N0} | {parse.WallMs:N1} | {parse.RowsPerSec:N0} |");
    sb.AppendLine();
    sb.AppendLine("## Hot-path optimisation: naive scan vs indexed lookup (before / after)");
    sb.AppendLine();
    sb.AppendLine($"Exact-reference matching over {naiveN:N0} clean pairs ({naiveN * 2:N0} rows). The naive matcher does an");
    sb.AppendLine("O(n·m) nested scan; the shipped engine builds an O(1) dictionary index keyed by (currency, reference, amount).");
    sb.AppendLine();
    sb.AppendLine("| strategy | complexity | wall time (ms) | throughput (rows/s) | matches |");
    sb.AppendLine("|----------|------------|---------------:|--------------------:|--------:|");
    sb.AppendLine($"| naive (before) | O(n·m) | {naive.WallMs:N1} | {naive.RowsPerSec:N0} | {naive.Matches:N0} |");
    sb.AppendLine($"| indexed (after) | O(n) | {indexed.WallMs:N1} | {indexed.RowsPerSec:N0} | {indexed.Matches:N0} |");
    sb.AppendLine();
    sb.AppendLine($"**Speed-up: {speedup:N1}×** on this host. The gap widens as row counts grow, which is why the shipped");
    sb.AppendLine("pipeline never uses nested scans — every rule is backed by a dictionary or day-bucketed index.");
    sb.AppendLine();
    sb.AppendLine("## Method & honesty notes");
    sb.AppendLine();
    sb.AppendLine("- Throughput is wall-clock; a JIT warm-up run precedes the measured runs.");
    sb.AppendLine("- Peak working set is `Process.PeakWorkingSet64` (process-wide, monotonic), so later rows report the high-water mark.");
    sb.AppendLine("- Allocations are `GC.GetTotalAllocatedBytes` deltas around the measured call.");
    sb.AppendLine("- The datasets are produced by the same `SyntheticDataGenerator` used by the tests, seeded for repeatability.");
    return sb.ToString();
}

static Dictionary<string, string> ParseArgs(string[] argv)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < argv.Length; i++)
    {
        if (!argv[i].StartsWith("--", StringComparison.Ordinal)) continue;
        var key = argv[i][2..];
        if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal)) map[key] = argv[++i];
        else map[key] = "true";
    }
    return map;
}

internal readonly record struct ReconcileResult(
    int Pairs, int TotalRows, double WallMs, double RowsPerSec,
    double PeakWorkingSetMb, double AllocMb, int Matches, int Exceptions);

internal readonly record struct ParseResult(int Rows, double WallMs, double RowsPerSec);

internal readonly record struct PhaseResult(double WallMs, double RowsPerSec, int Matches);
