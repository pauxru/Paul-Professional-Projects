namespace Bridge.Tests;

/// <summary>
/// An independent managed implementation of the closed-form Black-Scholes price.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the tests can check the C++ engine against something, rather than
/// against a table of numbers copied out of the C++ engine's own output. Pinned
/// constants only prove the engine still does what it did the day the constants were
/// copied; they cannot tell a correct answer from a wrong one that has always been
/// wrong. Two implementations, written from the formula independently, disagreeing is
/// a signal. One implementation agreeing with its own history is not.
/// </para>
/// <para>
/// The normal CDF is Hart's rational approximation, which is accurate to roughly 1e-15
/// across the whole real line. The cheap Abramowitz-Stegun 7.1.26 polynomial that shows
/// up in most textbooks is only good to about 7.5e-8 -- fine for a plot, useless for
/// distinguishing "these two implementations agree" from "these two implementations are
/// both approximately right".
/// </para>
/// </remarks>
public static class ReferenceModel
{
    /// <summary>Cumulative standard normal distribution, Hart 1968.</summary>
    public static double NormalCdf(double x)
    {
        if (double.IsNaN(x)) return double.NaN;

        var a = Math.Abs(x);
        if (a > 37.0)
        {
            // Beyond this the result is 0 or 1 to within a double's precision, and the
            // exp() below underflows to zero anyway. Returning early keeps the sign
            // logic from producing a spurious -0.0.
            return x > 0 ? 1.0 : 0.0;
        }

        var e = Math.Exp(-a * a / 2.0);
        double c;
        if (a < 7.07106781186547)
        {
            var build = 3.52624965998911e-02 * a + 0.700383064443688;
            build = build * a + 6.37396220353165;
            build = build * a + 33.912866078383;
            build = build * a + 112.079291497871;
            build = build * a + 221.213596169931;
            build = build * a + 220.206867912376;
            c = e * build;
            build = 8.83883476483184e-02 * a + 1.75566716318264;
            build = build * a + 16.064177579207;
            build = build * a + 86.7807322029461;
            build = build * a + 296.564248779674;
            build = build * a + 637.333633378831;
            build = build * a + 793.826512519948;
            build = build * a + 440.413735824752;
            c /= build;
        }
        else
        {
            var build = a + 0.65;
            build = a + 4.0 / build;
            build = a + 3.0 / build;
            build = a + 2.0 / build;
            build = a + 1.0 / build;
            c = e / build / 2.506628274631;
        }

        return x > 0 ? 1.0 - c : c;
    }

    /// <summary>
    /// Closed-form European price with a continuous dividend yield.
    /// </summary>
    /// <remarks>
    /// Deliberately unguarded. The engine's behaviour on degenerate input is one of the
    /// things under test, so the reference has to be willing to produce whatever the
    /// formula produces -- including NaN -- rather than substitute a house opinion.
    /// </remarks>
    public static double European(double spot, double strike, double rate,
                                  double dividend, double vol, double years, bool call)
    {
        var sqrtT = Math.Sqrt(years);
        var d1 = (Math.Log(spot / strike) + (rate - dividend + vol * vol / 2.0) * years)
                 / (vol * sqrtT);
        var d2 = d1 - vol * sqrtT;

        var df = Math.Exp(-rate * years);
        var qf = Math.Exp(-dividend * years);

        return call
            ? spot * qf * NormalCdf(d1) - strike * df * NormalCdf(d2)
            : strike * df * NormalCdf(-d2) - spot * qf * NormalCdf(-d1);
    }

    /// <summary>
    /// Cox-Ross-Rubinstein binomial price. Used to check the native lattice, and to
    /// demonstrate the parity oscillation the convergence test relies on.
    /// </summary>
    public static double Crr(double spot, double strike, double rate, double dividend,
                             double vol, double years, bool call, bool american, int steps)
    {
        var dt = years / steps;
        var u = Math.Exp(vol * Math.Sqrt(dt));
        var d = 1.0 / u;
        var disc = Math.Exp(-rate * dt);
        var p = (Math.Exp((rate - dividend) * dt) - d) / (u - d);

        var values = new double[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            var s = spot * Math.Pow(u, 2.0 * i - steps);
            values[i] = call ? Math.Max(s - strike, 0.0) : Math.Max(strike - s, 0.0);
        }

        for (var step = steps - 1; step >= 0; step--)
        {
            for (var i = 0; i <= step; i++)
            {
                values[i] = disc * (p * values[i + 1] + (1 - p) * values[i]);
                if (american)
                {
                    var s = spot * Math.Pow(u, 2.0 * i - step);
                    var intrinsic = call ? s - strike : strike - s;
                    if (intrinsic > values[i]) values[i] = intrinsic;
                }
            }
        }

        return values[0];
    }
}
