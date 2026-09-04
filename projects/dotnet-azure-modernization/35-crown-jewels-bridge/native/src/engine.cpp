// engine.cpp -- the maths. Identical in all three DLLs this project builds.
//
// The hardened DLL, the unhardened "as found in 2009" DLL and the /fp:fast DLL all
// compile this file from the same bytes. That is the point of the experiment: it lets
// the report attribute every behavioural difference either to the boundary shim or to
// the compiler flags, and never to someone quietly improving the engine.

#include "engine.hpp"

#include <algorithm>
#include <cmath>
#include <limits>

namespace contoso {

double norm_pdf(double x) noexcept {
    static const double inv_sqrt_2pi = 0.39894228040143267793994605993438;
    return inv_sqrt_2pi * std::exp(-0.5 * x * x);
}

double norm_cdf(double x) noexcept {
    // 0.5 * erfc(-x / sqrt(2)). erfc is accurate in the left tail where
    // 1 - 0.5*erfc(x/sqrt2) would cancel catastrophically.
    static const double inv_sqrt2 = 0.70710678118654752440084436210485;
    return 0.5 * std::erfc(-x * inv_sqrt2);
}

double norm_inv(double p) noexcept {
    // Acklam's rational approximation: relative error below 1.15e-9 across the range.
    static const double a[6] = {-3.969683028665376e+01,  2.209460984245205e+02,
                                -2.759285104469687e+02,  1.383577518672690e+02,
                                -3.066479806614716e+01,  2.506628277459239e+00};
    static const double b[5] = {-5.447609879822406e+01,  1.615858368580409e+02,
                                -1.556989798598866e+02,  6.680131188771972e+01,
                                -1.328068155288572e+01};
    static const double c[6] = {-7.784894002430293e-03, -3.223964580411365e-01,
                                -2.400758277161838e+00, -2.549732539343734e+00,
                                 4.374664141464968e+00,  2.938163982698783e+00};
    static const double d[4] = { 7.784695709041462e-03,  3.224671290700398e-01,
                                 2.445134137142996e+00,  3.754408661907416e+00};
    static const double p_low = 0.02425;
    static const double p_high = 1.0 - p_low;

    if (p <= 0.0) return -std::numeric_limits<double>::infinity();
    if (p >= 1.0) return  std::numeric_limits<double>::infinity();

    double x;
    if (p < p_low) {
        const double q = std::sqrt(-2.0 * std::log(p));
        x = (((((c[0]*q + c[1])*q + c[2])*q + c[3])*q + c[4])*q + c[5]) /
            ((((d[0]*q + d[1])*q + d[2])*q + d[3])*q + 1.0);
    } else if (p <= p_high) {
        const double q = p - 0.5;
        const double r = q * q;
        x = (((((a[0]*r + a[1])*r + a[2])*r + a[3])*r + a[4])*r + a[5]) * q /
            (((((b[0]*r + b[1])*r + b[2])*r + b[3])*r + b[4])*r + 1.0);
    } else {
        const double q = std::sqrt(-2.0 * std::log(1.0 - p));
        x = -(((((c[0]*q + c[1])*q + c[2])*q + c[3])*q + c[4])*q + c[5]) /
             ((((d[0]*q + d[1])*q + d[2])*q + d[3])*q + 1.0);
    }

    // One Halley refinement. Costs an erfc and buys roughly full double precision,
    // which matters because the antithetic variate assumes norm_inv(1-u) == -norm_inv(u)
    // and the raw Acklam approximation is not that symmetric.
    const double e = 0.5 * std::erfc(-x / std::sqrt(2.0)) - p;
    const double u = e * std::sqrt(2.0 * 3.14159265358979323846) * std::exp(x * x / 2.0);
    x = x - u / (1.0 + x * u / 2.0);
    return x;
}

namespace {

struct BsCore {
    double d1, d2, df_r, df_q, sqrt_t;
};

BsCore bs_core(const pj_option& o) noexcept {
    BsCore c{};
    c.sqrt_t = std::sqrt(o.years);
    // No guard. sigma == 0 or years == 0 divides by zero and produces NaN or inf,
    // and every line below propagates it silently. This is the 2009 behaviour and it
    // is preserved deliberately -- the hardened boundary refuses such inputs before
    // they ever arrive here.
    const double vol_sqrt_t = o.volatility * c.sqrt_t;
    c.d1 = (std::log(o.spot / o.strike)
            + (o.rate - o.dividend + 0.5 * o.volatility * o.volatility) * o.years)
           / vol_sqrt_t;
    c.d2 = c.d1 - vol_sqrt_t;
    c.df_r = std::exp(-o.rate * o.years);
    c.df_q = std::exp(-o.dividend * o.years);
    return c;
}

}  // namespace

double bs_price(const pj_option& o) noexcept {
    const BsCore c = bs_core(o);
    if (o.kind == PJ_CALL_OPTION) {
        return o.spot * c.df_q * norm_cdf(c.d1) - o.strike * c.df_r * norm_cdf(c.d2);
    }
    return o.strike * c.df_r * norm_cdf(-c.d2) - o.spot * c.df_q * norm_cdf(-c.d1);
}

pj_greeks bs_greeks(const pj_option& o) noexcept {
    const BsCore c = bs_core(o);
    const double pdf_d1 = norm_pdf(c.d1);
    const bool is_call = (o.kind == PJ_CALL_OPTION);

    pj_greeks g{};
    g.price = bs_price(o);
    g.delta = is_call ? c.df_q * norm_cdf(c.d1)
                      : c.df_q * (norm_cdf(c.d1) - 1.0);
    g.gamma = c.df_q * pdf_d1 / (o.spot * o.volatility * c.sqrt_t);
    g.vega  = o.spot * c.df_q * pdf_d1 * c.sqrt_t;

    const double common_theta =
        -(o.spot * c.df_q * pdf_d1 * o.volatility) / (2.0 * c.sqrt_t);
    if (is_call) {
        g.theta = common_theta
                  - o.rate * o.strike * c.df_r * norm_cdf(c.d2)
                  + o.dividend * o.spot * c.df_q * norm_cdf(c.d1);
        g.rho = o.strike * o.years * c.df_r * norm_cdf(c.d2);
    } else {
        g.theta = common_theta
                  + o.rate * o.strike * c.df_r * norm_cdf(-c.d2)
                  - o.dividend * o.spot * c.df_q * norm_cdf(-c.d1);
        g.rho = -o.strike * o.years * c.df_r * norm_cdf(-c.d2);
    }
    return g;
}

double crr_american(const pj_option& o, int steps) {
    // In 2009 this was `new double[steps + 1]`. It is a vector now because someone
    // ran it through a leak detector in 2014. Nobody added a bounds check, because
    // nobody had ever passed a negative step count. A vector with a negative size
    // converts to an enormous size_t and throws std::length_error or std::bad_alloc
    // -- a C++ exception, heading straight for a C ABI boundary.
    std::vector<double> values(static_cast<std::size_t>(steps) + 1);

    const double dt = o.years / static_cast<double>(steps);
    const double u = std::exp(o.volatility * std::sqrt(dt));
    const double d = 1.0 / u;
    const double disc = std::exp(-o.rate * dt);
    const double growth = std::exp((o.rate - o.dividend) * dt);
    const double p = (growth - d) / (u - d);
    const double q = 1.0 - p;
    const bool is_call = (o.kind == PJ_CALL_OPTION);

    // Terminal payoffs. Node j has had j up-moves and (steps - j) down-moves.
    for (int j = 0; j <= steps; ++j) {
        const double st = o.spot * std::pow(u, 2.0 * j - steps);
        values[static_cast<std::size_t>(j)] =
            is_call ? std::max(0.0, st - o.strike) : std::max(0.0, o.strike - st);
    }

    for (int i = steps - 1; i >= 0; --i) {
        for (int j = 0; j <= i; ++j) {
            const double cont = disc * (p * values[static_cast<std::size_t>(j) + 1]
                                        + q * values[static_cast<std::size_t>(j)]);
            const double st = o.spot * std::pow(u, 2.0 * j - i);
            const double exercise =
                is_call ? std::max(0.0, st - o.strike) : std::max(0.0, o.strike - st);
            values[static_cast<std::size_t>(j)] = std::max(cont, exercise);
        }
    }
    return values[0];
}

McResult monte_carlo(const pj_option& o, std::int64_t paths, std::uint64_t seed,
                     std::int64_t report_every,
                     pj_progress_fn progress, void* user) noexcept {
    McResult r{};
    Rng rng(seed);

    const double drift = (o.rate - o.dividend - 0.5 * o.volatility * o.volatility) * o.years;
    const double diffusion = o.volatility * std::sqrt(o.years);
    const double df = std::exp(-o.rate * o.years);
    const bool is_call = (o.kind == PJ_CALL_OPTION);

    // Antithetic pairs: one uniform gives two paths, z and -z. Because norm_inv is
    // refined to be symmetric, the pair is exact rather than approximately opposite.
    double sum = 0.0;
    std::int64_t done = 0;
    const std::int64_t pairs = paths / 2;

    for (std::int64_t i = 0; i < pairs; ++i) {
        const double z = norm_inv(rng.next_uniform());
        const double s_up = o.spot * std::exp(drift + diffusion * z);
        const double s_dn = o.spot * std::exp(drift - diffusion * z);
        const double pay_up = is_call ? std::max(0.0, s_up - o.strike)
                                      : std::max(0.0, o.strike - s_up);
        const double pay_dn = is_call ? std::max(0.0, s_dn - o.strike)
                                      : std::max(0.0, o.strike - s_dn);
        sum += pay_up + pay_dn;
        done += 2;

        if (progress != nullptr && report_every > 0 && (i + 1) % report_every == 0) {
            if (progress(done, paths, user) == 0) {
                r.cancelled = true;
                r.completed = done;
                r.price = (done > 0) ? df * sum / static_cast<double>(done) : 0.0;
                return r;
            }
        }
    }

    // Odd path count: one unpaired draw, so that `paths` means what it says.
    if (paths % 2 == 1) {
        const double z = norm_inv(rng.next_uniform());
        const double st = o.spot * std::exp(drift + diffusion * z);
        sum += is_call ? std::max(0.0, st - o.strike) : std::max(0.0, o.strike - st);
        done += 1;
    }

    r.completed = done;
    r.price = (done > 0) ? df * sum / static_cast<double>(done) : 0.0;
    r.cancelled = false;
    return r;
}

}  // namespace contoso
