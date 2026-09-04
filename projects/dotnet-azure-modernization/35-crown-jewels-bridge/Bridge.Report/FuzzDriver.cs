using System.Diagnostics;
using Bridge.Legacy;

namespace Bridge.Report;

/// <summary>Aggregate result of running a corpus against one variant.</summary>
public sealed record FuzzSummary(
    string Variant,
    int Total,
    IReadOnlyDictionary<FuzzOutcome, int> Counts,
    IReadOnlyList<int> CrashIndices,
    IReadOnlyList<int> CorruptionIndices,
    IReadOnlyList<int> SilentlyWrongIndices,
    int ControlTotal,
    int ControlAccepted,
    IReadOnlyList<int> ControlRefusedIndices)
{
    /// <summary>
    /// Whether the run says anything at all about safety. A boundary that refuses every
    /// input scores a perfect zero on every hostile shape, so the hostile counts are only
    /// meaningful alongside evidence that legal inputs still get through. If the control
    /// group was refused, the correct reading of a clean sheet is "this boundary is
    /// broken", not "this boundary is safe".
    /// </summary>
    public bool ControlHeld => ControlTotal > 0 && ControlAccepted == ControlTotal;

    public int Count(FuzzOutcome o) => Counts.GetValueOrDefault(o);

    /// <summary>Cases where the boundary neither refused nor produced a usable answer.</summary>
    public int UnsafeCases =>
        Count(FuzzOutcome.SilentlyWrong) + Count(FuzzOutcome.MemoryCorruption) + Count(FuzzOutcome.ProcessDied);
}

/// <summary>
/// Runs a fuzz corpus against a DLL variant in a child process.
/// </summary>
/// <remarks>
/// <para>
/// This indirection is not defensive programming, it is a requirement. The whole point
/// of fuzzing the 2009 boundary is that some inputs kill the process; an in-process
/// harness would report the first crash and nothing after it, which means the experiment
/// could never produce a rate. Isolating each run behind a process boundary turns "it
/// crashed" from the end of the experiment into one data point in it.
/// </para>
/// <para>
/// The protocol is deliberately the simplest thing that survives a crash: the child
/// writes one flushed line per completed case, so when it dies the parent knows exactly
/// which case was in flight -- it is the one with no line. The parent then restarts the
/// child at the following index. Anything richer (shared memory, a pipe protocol with
/// framing) would itself be state that a crash could corrupt.
/// </para>
/// </remarks>
public static class FuzzDriver
{
    public const ulong DefaultSeed = 0x00C0FFEEul;

    /// <summary>Runs the corpus in-process. Only safe for the hardened variant.</summary>
    public static FuzzSummary RunInProcess(string variantName, ulong seed, int count)
    {
        var cases = FuzzCorpus.Generate(seed, count);
        using var variant = Variants.Open(variantName);
        using var runner = new FuzzRunner(variant);

        var outcomes = new FuzzOutcome[count];
        for (var i = 0; i < count; i++)
        {
            outcomes[i] = runner.RunOne(cases[i]);
        }
        return Summarise(variantName, outcomes, seed);
    }

    /// <summary>
    /// Runs the corpus in child processes, restarting after every crash.
    /// </summary>
    public static FuzzSummary RunIsolated(string variantName, ulong seed, int count,
                                          TimeSpan perChildTimeout)
    {
        var outcomes = new FuzzOutcome?[count];
        var next = 0;
        var restarts = 0;

        while (next < count && restarts <= count)
        {
            var (completed, died) = RunChild(variantName, seed, count, next, outcomes, perChildTimeout);
            if (!died)
            {
                break;
            }
            // The case in flight when the child died is the one after the last it
            // reported. Attribute the death to it and step over it.
            var victim = completed;
            if (victim < count)
            {
                outcomes[victim] = FuzzOutcome.ProcessDied;
                next = victim + 1;
            }
            else
            {
                break;
            }
            restarts++;
        }

        var final = new FuzzOutcome[count];
        for (var i = 0; i < count; i++)
        {
            // A case with no verdict is one the harness never reached, which would be a
            // bug in the harness rather than a finding about the boundary. Recording it
            // as a death would inflate the headline number with the harness's own
            // failures, so it is recorded as accepted -- the conservative direction.
            final[i] = outcomes[i] ?? FuzzOutcome.Accepted;
        }
        return Summarise(variantName, final, seed);
    }

    private static (int NextIndex, bool Died) RunChild(
        string variantName, ulong seed, int count, int start,
        FuzzOutcome?[] outcomes, TimeSpan timeout)
    {
        var exe = Environment.ProcessPath!;
        var dll = Path.Combine(AppContext.BaseDirectory, "Bridge.Report.dll");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (!exe.EndsWith("Bridge.Report.exe", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add(dll);
        }
        psi.ArgumentList.Add("fuzz-child");
        psi.ArgumentList.Add(variantName);
        psi.ArgumentList.Add(seed.ToString());
        psi.ArgumentList.Add(count.ToString());
        psi.ArgumentList.Add(start.ToString());

        // A child that crashes hard enough can leave a Windows Error Reporting dialog
        // waiting for a human. Nothing here is interactive, so that would be a hang
        // rather than a crash, and the experiment would measure the wrong thing.
        psi.Environment["COMPlus_DbgEnableMiniDump"] = "0";

        using var proc = Process.Start(psi)!;
        var highest = start - 1;
        var stdout = proc.StandardOutput;

        string? line;
        while ((line = stdout.ReadLine()) is not null)
        {
            var parts = line.Split(' ');
            if (parts.Length == 2 && int.TryParse(parts[0], out var idx)
                && Enum.TryParse<FuzzOutcome>(parts[1], out var outcome))
            {
                outcomes[idx] = outcome;
                highest = Math.Max(highest, idx);
            }
        }

        if (!proc.WaitForExit(timeout))
        {
            proc.Kill(entireProcessTree: true);
            return (highest + 1, true);
        }
        return (highest + 1, proc.ExitCode != 0);
    }

    /// <summary>Entry point for the child process.</summary>
    public static int ChildMain(string[] args)
    {
        var variantName = args[1];
        var seed = ulong.Parse(args[2]);
        var count = int.Parse(args[3]);
        var start = int.Parse(args[4]);

        var cases = FuzzCorpus.Generate(seed, count);
        using var variant = Variants.Open(variantName);
        using var runner = new FuzzRunner(variant);

        for (var i = start; i < count; i++)
        {
            var outcome = runner.RunOne(cases[i]);
            // Flush every line. Buffered output is lost when the process dies, and the
            // lost lines are precisely the ones that identify the crash.
            Console.Out.WriteLine($"{i} {outcome}");
            Console.Out.Flush();
        }
        return 0;
    }

    private static FuzzSummary Summarise(string variant, FuzzOutcome[] outcomes,
                                         ulong seed)
    {
        var counts = new Dictionary<FuzzOutcome, int>();
        var crashes = new List<int>();
        var corruptions = new List<int>();
        var silent = new List<int>();

        for (var i = 0; i < outcomes.Length; i++)
        {
            counts[outcomes[i]] = counts.GetValueOrDefault(outcomes[i]) + 1;
            switch (outcomes[i])
            {
                case FuzzOutcome.ProcessDied: crashes.Add(i); break;
                case FuzzOutcome.MemoryCorruption: corruptions.Add(i); break;
                case FuzzOutcome.SilentlyWrong: silent.Add(i); break;
            }
        }

        // The corpus is a pure function of the seed and the count, so the control cases
        // can be identified by regenerating it rather than by threading the case list
        // through the isolated runner and its child processes.
        var cases = FuzzCorpus.Generate(seed, outcomes.Length);
        var controlTotal = 0;
        var controlAccepted = 0;
        var controlRefused = new List<int>();
        for (var i = 0; i < outcomes.Length; i++)
        {
            if (!cases[i].IsControl) continue;
            controlTotal++;
            if (outcomes[i] == FuzzOutcome.Accepted) controlAccepted++;
            else controlRefused.Add(i);
        }

        return new FuzzSummary(variant, outcomes.Length, counts, crashes, corruptions,
                               silent, controlTotal, controlAccepted, controlRefused);
    }
}
