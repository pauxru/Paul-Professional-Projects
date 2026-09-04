using Bridge.Core;
using Bridge.Legacy;

namespace Bridge.Report;

/// <summary>
/// The three findings, live, in about ninety seconds.
/// </summary>
/// <remarks>
/// Deliberately not a summary of <c>results.md</c>. Everything printed here is computed
/// while you watch, against the same three DLLs, because the entire point of the project
/// is that these are measurements rather than opinions -- and a demo that prints numbers
/// it was told is indistinguishable from one that prints numbers it found.
/// </remarks>
public static class Demo
{
    private static void Rule(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('-', 76));
        Console.WriteLine("  " + title);
        Console.WriteLine(new string('-', 76));
    }

    public static unsafe int Run()
    {
        Console.WriteLine();
        Console.WriteLine("  Crown Jewels Bridge -- the same C++ engine behind two boundaries");
        Console.WriteLine("  engine.cpp is byte-identical in every DLL used below.");

        NegativeVolatility();
        ShortBuffer();
        FastMath();

        Rule("what to take away");
        Console.WriteLine();
        Console.WriteLine("  Not one line of engine.cpp was audited or changed. Every difference");
        Console.WriteLine("  above is argument validation, a capacity check and an exception barrier");
        Console.WriteLine("  in abi.cpp -- about 300 lines, in front of 60,000 nobody is allowed to");
        Console.WriteLine("  touch.");
        Console.WriteLine();
        Console.WriteLine("  Full report: docs/results.md");
        Console.WriteLine();
        return 0;
    }

    /// <summary>Finding 1: a sign error prices the opposite instrument, exactly.</summary>
    private static void NegativeVolatility()
    {
        Rule("1. a sign error in a volatility feed is not a NaN. It is a bookable price.");

        var call = new PricingOption(42, 40, 0.10, 0.0, 0.20, 0.5, OptionKind.Call);
        var put = call with { Kind = (int)OptionKind.Put };
        var flipped = call with { Volatility = -0.20 };

        using var legacy = Variants.Open(Variants.LegacyBoundary);
        var engine = legacy.CreateEngine();
        try
        {
            var trueCall = legacy.PriceOne(engine, call);
            var truePut = legacy.PriceOne(engine, put);
            var wrong = legacy.PriceOne(engine, flipped);

            Console.WriteLine();
            Console.WriteLine($"  a call, priced correctly                 {trueCall,14:F10}");
            Console.WriteLine($"  the same call with sigma = -0.20         {wrong,14:F10}");
            Console.WriteLine($"  the corresponding put, negated           {-truePut,14:F10}");
            Console.WriteLine();
            Console.WriteLine($"  difference between the last two          {Math.Abs(wrong + truePut),14:E3}");
            Console.WriteLine();
            Console.WriteLine("  Sigma appears squared in d1's numerator and once in its denominator,");
            Console.WriteLine("  so negating it maps d1 -> -d1 and d2 -> -d2, and the call formula");
            Console.WriteLine("  becomes minus the put formula. Exactly. The 2009 boundary returns it");
            Console.WriteLine("  with a success code: a small, finite, entirely plausible price for a");
            Console.WriteLine("  contract nobody traded.");
        }
        finally
        {
            unsafe { legacy.Destroy(engine); }
        }

        Console.WriteLine();
        using var safe = new PricingEngine();
        try
        {
            safe.PriceEuropean(flipped);
            Console.WriteLine("  hardened boundary:  ACCEPTED IT -- this demo is broken");
        }
        catch (PricingException ex)
        {
            Console.WriteLine($"  hardened boundary:  refused -- \"{safe.LastError()}\" ({ex.Status})");
        }
    }

    /// <summary>Finding 2: the overflow that does not fault.</summary>
    private static void ShortBuffer()
    {
        Rule("2. a short output buffer does not crash. That is what makes it dangerous.");

        Console.WriteLine();
        Console.WriteLine("  Sixty-four options, an output buffer with room for one.");
        Console.WriteLine("  Running it in a child process, because the answer is not always survival.");
        Console.WriteLine();

        var (exit, output) = Child("batch-short-buffer");
        var verdict = exit switch
        {
            LegacyProbe.SurvivedWithCorruption =>
                $"survived, and overwrote {output} doubles past the end of the buffer",
            LegacyProbe.Survived => "survived with the buffer intact",
            _ => $"the process died (exit {exit})",
        };
        Console.WriteLine($"  2009 boundary:      {verdict}");
        Console.WriteLine();
        Console.WriteLine("  No access violation. 512 bytes of overrun lands inside allocator padding,");
        Console.WriteLine("  so a test that waits for a fault reports this input as safe. The count");
        Console.WriteLine("  above comes from a guard pattern written past the buffer and counted");
        Console.WriteLine("  afterwards -- which is why it is an assertion and not an anecdote.");
        Console.WriteLine();

        using var safe = new PricingEngine();
        var options = new PricingOption[64];
        Array.Fill(options, new PricingOption(42, 40, 0.10, 0.0, 0.20, 0.5, OptionKind.Call));
        try
        {
            safe.PriceBatch(options, new double[1]);
            Console.WriteLine("  hardened boundary:  ACCEPTED IT -- this demo is broken");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  hardened boundary:  refused -- {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("  And the quieter one: a lattice with zero steps.");
        var (zeroExit, zeroOut) = Child("american-zero-steps");
        Console.WriteLine($"  2009 boundary:      exit {zeroExit}, returned {zeroOut}");

        var real = safe.PriceAmerican(
            new PricingOption(42, 40, 0.10, 0.0, 0.20, 0.5, OptionKind.Put), 2000);
        Console.WriteLine($"  what it is worth:   {real:F10}");
        Console.WriteLine();
        Console.WriteLine("  Zero is exactly what an out-of-the-money put is supposed to look like,");
        Console.WriteLine("  so nobody queries it. No crash, no NaN, no log line, no alert.");
    }

    /// <summary>Finding 3: the build flags change the number the business books.</summary>
    private static unsafe void FastMath()
    {
        Rule("3. recompiling the untouched engine changes the price.");

        using var precise = Variants.Open(Variants.Hardened);
        using var fast = Variants.Open(Variants.FastMath);
        var ep = precise.CreateEngine();
        var ef = fast.CreateEngine();

        try
        {
            var differing = 0;
            var worstUlp = 0L;
            var worstRelative = 0.0;
            const int positions = 4000;

            for (var i = 0; i < positions; i++)
            {
                var spot = 5.0 + i * 0.05;
                var o = new PricingOption(
                    spot, 40 + (i % 37), 0.01 + (i % 9) * 0.005, 0.0,
                    0.05 + (i % 61) * 0.004, 0.05 + (i % 23) * 0.15,
                    (i & 1) == 0 ? OptionKind.Call : OptionKind.Put);

                var a = precise.PriceOne(ep, o);
                var b = fast.PriceOne(ef, o);
                if (a.Equals(b)) continue;

                differing++;
                var ulp = Math.Abs(BitConverter.DoubleToInt64Bits(a) - BitConverter.DoubleToInt64Bits(b));
                if (ulp > worstUlp) worstUlp = ulp;

                var relative = Math.Abs(a - b) / Math.Max(1e-12, Math.Abs(a));
                if (relative > worstRelative) worstRelative = relative;
            }

            Console.WriteLine();
            Console.WriteLine($"  {positions:N0} positions priced through /fp:precise and /fp:fast.");
            Console.WriteLine($"  identical:          {positions - differing,10:N0}");
            Console.WriteLine($"  different:          {differing,10:N0}  ({100.0 * differing / positions:F2}%)");
            Console.WriteLine($"  worst, relative:    {worstRelative,10:E3}");
            Console.WriteLine($"  worst, in ULP:      {worstUlp,10:N0}");
            Console.WriteLine();
            Console.WriteLine("  Those two \"worst\" numbers disagree wildly, and the relative one is the");
            Console.WriteLine("  honest one. ULP distance is meaningless for a deep out-of-the-money");
            Console.WriteLine("  option worth 1e-11: two prices that are both effectively zero can be");
            Console.WriteLine("  thousands of representable doubles apart. Reporting the ULP figure");
            Console.WriteLine("  alone would make this look far worse than it is -- and picking the");
            Console.WriteLine("  metric after seeing the numbers is how a comparison stops being one.");
            Console.WriteLine();
            Console.WriteLine("  engine.cpp is byte-identical between these two DLLs. Only the");
            Console.WriteLine("  floating-point flags differ. The magnitudes are far below anything a");
            Console.WriteLine("  desk would notice on a single trade -- which is the problem, not the");
            Console.WriteLine("  reassurance. A parallel run will show a drip of tiny unexplained");
            Console.WriteLine("  breaks, someone will call them noise, and the programme loses the");
            Console.WriteLine("  ability to tell noise from a regression.");
        }
        finally
        {
            precise.Destroy(ep);
            fast.Destroy(ef);
        }
    }

    private static (int Exit, string Output) Child(string probe)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("legacy-probe");
        psi.ArgumentList.Add(probe);

        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout.Trim());
    }
}
