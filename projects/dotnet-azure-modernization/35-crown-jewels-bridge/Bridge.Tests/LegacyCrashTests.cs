using System.Diagnostics;
using Bridge.Core;
using Bridge.Report;

namespace Bridge.Tests;

/// <summary>
/// The claims about the 2009 boundary that cannot be asserted from inside the process
/// making them, because the process does not survive.
/// </summary>
/// <remarks>
/// <para>
/// Every probe below runs in a child. That is not caution, it is the only way the
/// experiment can produce an answer: the in-process version of the first test here
/// takes the test host with it, reports nothing, and every test scheduled after it
/// never runs. This was discovered the way these things usually are -- by writing the
/// in-process version first and watching the run abort at test nine of a hundred and
/// forty.
/// </para>
/// <para>
/// The hardened counterpart of each probe is asserted in-process, immediately after it,
/// so each pair reads as a single claim: the same C++, compiled twice, with and without
/// the boundary.
/// </para>
/// <para>
/// Three of the five probes kill the child. Two do not, and those two are the reason
/// this project exists: an unchecked boundary that crashes has told you it is broken.
/// An unchecked boundary that returns zero for an option worth eighty-one cents has
/// not.
/// </para>
/// </remarks>
public class LegacyCrashTests
{
    private static readonly PricingOption Sane =
        new(42, 40, 0.10, 0.0, 0.20, 0.5, OptionKind.Put);

    private sealed record ProbeResult(int ExitCode, string Stdout)
    {
        /// <summary>
        /// Windows reports an access violation as 0xC0000005, which .NET surfaces as a
        /// negative Int32. Any code the probe does not set itself means the process did
        /// not return: it was terminated.
        /// </summary>
        public bool Died => ExitCode is not (LegacyProbe.Survived
                                          or LegacyProbe.UnknownProbe
                                          or LegacyProbe.SurvivedWithGarbage
                                          or LegacyProbe.SurvivedWithCorruption);

        public override string ToString() => $"exit {ExitCode}, stdout '{Stdout}'";
    }

    private static ProbeResult RunProbe(string probe)
    {
        var exe = FindReportExecutable();
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("legacy-probe");
        psi.ArgumentList.Add(probe);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {exe}");

        // Read before waiting. A child that fills its pipe buffer and blocks on the
        // write, while the parent blocks on the exit, is a deadlock that only appears
        // when the output grows past a few kilobytes -- which for a crashing child is
        // exactly when the stack trace arrives.
        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();

        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"probe '{probe}' did not finish within a minute");
        }

        return new ProbeResult(process.ExitCode, stdout.Trim());
    }

    private static string FindReportExecutable()
    {
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            if (File.Exists(Path.Combine(probe.FullName, "CrownJewelsBridge.slnx")))
            {
                var exe = Path.Combine(probe.FullName,
                    "Bridge.Report", "bin", "Release", "net10.0", "Bridge.Report.exe");
                if (File.Exists(exe)) return exe;
                throw new InvalidOperationException(
                    $"{exe} does not exist; run build.ps1 before the tests");
            }
            probe = probe.Parent;
        }
        throw new InvalidOperationException(
            "could not find CrownJewelsBridge.slnx above " + AppContext.BaseDirectory);
    }

    // ------------------------------------------------------------------- it crashes

    [Fact]
    public void A_negative_step_count_kills_a_process_using_the_2009_boundary()
    {
        var result = RunProbe("american-negative-steps");
        Assert.True(result.Died, $"expected the child to die; got {result}");
    }

    [Fact]
    public void The_hardened_boundary_refuses_a_negative_step_count()
    {
        using var engine = new PricingEngine();
        var ex = Assert.Throws<PricingException>(() => engine.PriceAmerican(Sane, -1));
        Assert.Equal(PricingStatus.BadArgument, ex.Status);
    }

    [Fact]
    public void A_null_option_pointer_kills_a_process_using_the_2009_boundary()
    {
        var result = RunProbe("null-option");
        Assert.True(result.Died, $"expected the child to die; got {result}");
    }

    /// <summary>
    /// The most uncomfortable result here: the caller doing the documented thing is
    /// the one who dies.
    /// </summary>
    /// <remarks>
    /// Every C API that returns a variable-length string documents the two-call idiom
    /// -- pass no buffer to learn the required size, then allocate and ask again. The
    /// 2009 build reaches <c>strcpy</c> with a null destination on the first call. So
    /// the careful caller, who refuses to guess a buffer size, is punished, and the
    /// careless one who hard-codes 256 bytes gets away with it until a message is
    /// longer than that.
    /// </remarks>
    [Fact]
    public void Asking_the_2009_boundary_how_long_its_error_message_is_kills_the_process()
    {
        var result = RunProbe("error-probe-with-null-buffer");
        Assert.True(result.Died, $"expected the child to die; got {result}");
    }

    [Fact]
    public void The_hardened_boundary_answers_the_same_question_without_a_buffer()
    {
        // LastError uses exactly the idiom that kills the legacy build: a zero-capacity
        // probe call, then a second call with a buffer of the size it reported.
        using var engine = new PricingEngine();
        Assert.Throws<PricingException>(() => engine.PriceEuropean(Sane with { Volatility = -1 }));
        Assert.NotEqual(string.Empty, engine.LastError());
    }

    // ------------------------------------------------------ it does something worse

    /// <summary>
    /// Writes sixty-three doubles past the end of a buffer it was told had room for
    /// one, and returns success.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The overflow is detected with a guard pattern rather than by waiting for a
    /// fault, because the fault is not reliable and its absence is the point. Sixty-four
    /// doubles is 512 bytes; an allocator that has already rounded the request up, or a
    /// GC heap with another object after this one, absorbs the write silently. The
    /// process continues, having corrupted something that will fail later, elsewhere,
    /// in code that is entirely innocent.
    /// </para>
    /// <para>
    /// Sixty-three is not an approximation. The batch was sixty-four options and the
    /// declared capacity was one, so exactly sixty-three slots beyond the buffer were
    /// written. The count is deterministic, which is what makes it an assertion rather
    /// than an observation.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_short_output_buffer_corrupts_exactly_the_memory_after_it()
    {
        var result = RunProbe("batch-short-buffer");

        Assert.False(result.Died, $"expected the child to survive the corruption; got {result}");
        Assert.Equal(LegacyProbe.SurvivedWithCorruption, result.ExitCode);
        Assert.Equal("63", result.Stdout);
    }

    [Fact]
    public void The_hardened_boundary_refuses_the_short_buffer_and_writes_nothing()
    {
        using var engine = new PricingEngine();
        var options = new PricingOption[64];
        Array.Fill(options, Sane);

        // The managed wrapper stops it first, which is the cheapest possible place to
        // stop it. The native capacity check behind it is the one that protects callers
        // who are not using this wrapper, and it is exercised by the fuzz corpus.
        Assert.Throws<ArgumentException>(() => engine.PriceBatch(options, new double[1]));
    }

    /// <summary>
    /// The quietest failure of the five, and therefore the worst.
    /// </summary>
    /// <remarks>
    /// A zero-step lattice runs its loop no times and returns whatever the accumulator
    /// was initialised to. The status is success. The number is zero. The option is a
    /// put struck at 40 with the spot at 42 and six months to run, which is worth about
    /// eighty-one cents -- and zero is exactly what an out-of-the-money put is supposed
    /// to look like, so nobody queries it. No crash, no NaN, no log line, no alert.
    /// </remarks>
    [Fact]
    public void A_zero_step_lattice_returns_zero_for_an_option_that_is_worth_something()
    {
        var result = RunProbe("american-zero-steps");

        Assert.False(result.Died, $"expected the child to survive; got {result}");
        Assert.Equal(LegacyProbe.Survived, result.ExitCode);
        Assert.Equal("0", result.Stdout);

        // What the option is actually worth, from the boundary that checks.
        using var engine = new PricingEngine();
        var real = engine.PriceAmerican(Sane, 2000);
        Assert.True(real > 0.8, $"expected a materially non-zero price, got {real}");
    }

    [Fact]
    public void The_hardened_boundary_refuses_a_zero_step_lattice()
    {
        using var engine = new PricingEngine();
        var ex = Assert.Throws<PricingException>(() => engine.PriceAmerican(Sane, 0));
        Assert.Contains("steps", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ the harness itself

    [Fact]
    public void The_probe_harness_distinguishes_a_crash_from_a_refusal()
    {
        // Guards against the failure mode where every test above passes because the
        // harness cannot start the child at all and a startup failure is being read as
        // a crash. If this test fails, none of the others mean anything.
        var result = RunProbe("nonsense-probe-name");

        Assert.False(result.Died);
        Assert.Equal(LegacyProbe.UnknownProbe, result.ExitCode);
    }
}
