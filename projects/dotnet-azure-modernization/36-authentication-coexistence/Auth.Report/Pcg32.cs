namespace Auth.Report;

/// <summary>
/// PCG32. Small, fast, and -- the only property that matters here -- specified, so the same
/// seed produces the same stream on every runtime and every machine.
/// </summary>
/// <remarks>
/// <c>System.Random</c> is explicitly documented as not guaranteeing a stable sequence across
/// .NET versions. Every simulated number in this report would therefore be a number that
/// could change under a runtime upgrade, and a report whose findings move when you patch the
/// framework is not a report. Sixteen lines of arithmetic buys the property outright.
/// </remarks>
public sealed class Pcg32
{
    private const ulong Multiplier = 6364136223846793005UL;

    private ulong _state;
    private readonly ulong _increment;

    public Pcg32(ulong seed, ulong sequence = 1UL)
    {
        _increment = (sequence << 1) | 1UL;
        _state = 0UL;
        Next();
        _state += seed;
        Next();
    }

    public uint Next()
    {
        var previous = _state;
        _state = previous * Multiplier + _increment;
        var xorshifted = (uint)(((previous >> 18) ^ previous) >> 27);
        var rotation = (int)(previous >> 59);
        return (xorshifted >> rotation) | (xorshifted << ((-rotation) & 31));
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (Next() >> 8) * (1.0 / (1 << 24));

    public int NextInt(int exclusiveUpperBound)
    {
        // Rejection sampling. The modulo shortcut biases towards small values, which would
        // quietly skew every cohort in the migration model.
        var threshold = (uint)((0x100000000UL - (ulong)exclusiveUpperBound) % (ulong)exclusiveUpperBound);
        while (true)
        {
            var value = Next();
            if (value >= threshold) return (int)(value % (uint)exclusiveUpperBound);
        }
    }

    public bool NextBool(double probability) => NextDouble() < probability;
}
