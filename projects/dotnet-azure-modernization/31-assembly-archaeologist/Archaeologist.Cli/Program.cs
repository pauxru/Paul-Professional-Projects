using System.Text;
using Archaeologist.Core;

// Assembly Archaeologist.
//   (no args)   emit the estate, analyse it, write docs/results.md
//   --stdout    write the report to stdout instead
//   --keep DIR  emit the estate to DIR and leave it there

var toStdout = args.Contains("--stdout");
var keepIndex = Array.IndexOf(args, "--keep");
var keep = keepIndex >= 0 && keepIndex + 1 < args.Length ? args[keepIndex + 1] : null;

var repoRoot = FindRepoRoot();
var dir = keep ?? Path.Combine(Path.GetTempPath(), "archaeologist-estate");
if (Directory.Exists(dir)) Directory.Delete(dir, true);

var spec = CorpusSpec.Contoso();
var emitted = CorpusBuilder.Emit(spec, dir);
var estate = IlReader.Read(dir);
var report = Experiments.Run(estate, spec.EntryPoint);

if (toStdout)
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.Write(report);
}
else
{
    var outPath = Path.Combine(repoRoot, "docs", "results.md");
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    File.WriteAllText(outPath, report, new UTF8Encoding(false));
    Console.WriteLine($"emitted {emitted.Count} assemblies to {dir}");
    Console.WriteLine($"wrote {new FileInfo(outPath).Length} bytes to {outPath}");
}

if (keep is null && Directory.Exists(dir)) Directory.Delete(dir, true);
return 0;

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "AssemblyArchaeologist.slnx")))
        d = d.Parent;
    // Falling back to the working directory would write the report wherever the tool
    // happened to be invoked from, which is how a stale results.md gets checked in.
    return d?.FullName ?? throw new InvalidOperationException(
        $"could not find AssemblyArchaeologist.slnx above {AppContext.BaseDirectory}");
}
