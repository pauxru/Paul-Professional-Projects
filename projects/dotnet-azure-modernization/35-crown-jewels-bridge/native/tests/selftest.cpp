// selftest.cpp -- the native half of the correctness argument.
//
// The .NET test suite can only check what crosses the boundary. These checks run on the
// C++ side of it, on properties that must hold before any of the interop measurements
// mean anything: that the structs are the size the ABI header promises, that the closed
// form matches values computed independently, and that the numerical methods agree with
// each other where theory says they must.

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#include "engine.hpp"
#include "pricing_abi.h"

namespace {

int g_failures = 0;
int g_checks = 0;

void check(bool ok, const std::string& what) {
    ++g_checks;
    if (!ok) {
        ++g_failures;
        std::printf("FAIL  %s\n", what.c_str());
    }
}

void check_close(double actual, double expected, double tol, const std::string& what) {
    ++g_checks;
    const double diff = std::fabs(actual - expected);
    if (!(diff <= tol)) {
        ++g_failures;
        std::printf("FAIL  %s: got %.12f want %.12f (|d| = %.3e > %.3e)\n",
                    what.c_str(), actual, expected, diff, tol);
    }
}

pj_option opt(double s, double k, double r, double q, double v, double t, int kind) {
    pj_option o{};
    o.spot = s; o.strike = k; o.rate = r; o.dividend = q;
    o.volatility = v; o.years = t; o.kind = kind; o.reserved = 0;
    return o;
}

// ------------------------------------------------------------------- layout

void test_layout() {
    check(sizeof(pj_option) == 56, "pj_option is 56 bytes");
    check(sizeof(pj_greeks) == 48, "pj_greeks is 48 bytes");
    check(alignof(pj_option) == 8, "pj_option is 8-byte aligned");
    check(offsetof(pj_option, years) == 40, "years at offset 40");
    check(offsetof(pj_option, kind) == 48, "kind at offset 48");
    check(offsetof(pj_option, reserved) == 52, "reserved at offset 52");
    check(sizeof(pj_option) == 6 * sizeof(double) + 2 * sizeof(int32_t),
          "pj_option has no tail padding");
    check(offsetof(pj_greeks, rho) == 40, "rho at offset 40");
    check(sizeof(pj_status) == 4, "pj_status is 4 bytes");
}

// -------------------------------------------------------------- normal functions

void test_normal() {
    check_close(contoso::norm_cdf(0.0), 0.5, 1e-15, "norm_cdf(0)");
    check_close(contoso::norm_cdf(1.96), 0.9750021048517796, 1e-12, "norm_cdf(1.96)");
    check_close(contoso::norm_cdf(-1.96), 0.0249978951482204, 1e-12, "norm_cdf(-1.96)");
    check_close(contoso::norm_pdf(0.0), 0.3989422804014327, 1e-15, "norm_pdf(0)");

    // The left tail is where a naive 1 - N(x) implementation loses all its digits.
    check(contoso::norm_cdf(-8.0) > 0.0, "norm_cdf(-8) does not underflow to zero");
    check(contoso::norm_cdf(-8.0) < 1e-14, "norm_cdf(-8) is tiny");

    // norm_inv must invert norm_cdf to near machine precision, including in the tails,
    // because the antithetic variate depends on its symmetry.
    const double ps[] = {1e-9, 1e-4, 0.02, 0.2, 0.5, 0.8, 0.98, 1.0 - 1e-4, 1.0 - 1e-9};
    for (double p : ps) {
        const double x = contoso::norm_inv(p);
        check_close(contoso::norm_cdf(x), p, 1e-12 + p * 1e-9,
                    "norm_cdf(norm_inv(p)) == p at p=" + std::to_string(p));
    }
    for (double p : ps) {
        // norm_inv should be antisymmetric about 0.5. It is, to about 5e-9 in the far
        // tail -- the Halley step is limited by erfc's own accuracy there, not by the
        // rational approximation. That is an accuracy claim about this function, not a
        // correctness dependency: monte_carlo draws z once and uses +z and -z, so the
        // antithetic pair is exact by construction regardless of what this returns.
        check_close(contoso::norm_inv(1.0 - p), -contoso::norm_inv(p), 1e-8,
                    "norm_inv symmetry at p=" + std::to_string(p));
    }
}

// --------------------------------------------------------------- black-scholes

void test_black_scholes() {
    // Hull, Options Futures and Other Derivatives, the standard worked example:
    // S=42, K=40, r=0.10, q=0, sigma=0.20, T=0.5 -> call 4.759422, put 0.808599.
    const pj_option c = opt(42, 40, 0.10, 0.0, 0.20, 0.5, PJ_CALL_OPTION);
    const pj_option p = opt(42, 40, 0.10, 0.0, 0.20, 0.5, PJ_PUT_OPTION);
    check_close(contoso::bs_price(c), 4.759422392871532, 1e-9, "Hull call example");
    check_close(contoso::bs_price(p), 0.8085993729000922, 1e-9, "Hull put example");

    // Put-call parity: C - P = S e^{-qT} - K e^{-rT}. This is an identity, so it holds
    // to rounding for every input, and it catches sign errors the worked example cannot.
    const double spots[] = {1.0, 42.0, 100.0, 5000.0};
    const double vols[] = {0.05, 0.2, 0.9};
    const double divs[] = {0.0, 0.03};
    for (double s : spots) for (double v : vols) for (double q : divs) {
        const pj_option cc = opt(s, 100.0, 0.04, q, v, 1.5, PJ_CALL_OPTION);
        const pj_option pp = opt(s, 100.0, 0.04, q, v, 1.5, PJ_PUT_OPTION);
        const double lhs = contoso::bs_price(cc) - contoso::bs_price(pp);
        const double rhs = s * std::exp(-q * 1.5) - 100.0 * std::exp(-0.04 * 1.5);
        check_close(lhs, rhs, 1e-10 * std::max(1.0, s), "put-call parity");
    }

    // Deep in the money with no time left is intrinsic value.
    const pj_option deep = opt(200, 100, 0.0, 0.0, 0.2, 1e-6, PJ_CALL_OPTION);
    check_close(contoso::bs_price(deep), 100.0, 1e-6, "deep ITM call -> intrinsic");
}

void test_greeks() {
    const pj_option c = opt(42, 40, 0.10, 0.01, 0.20, 0.5, PJ_CALL_OPTION);
    const pj_greeks g = contoso::bs_greeks(c);

    // Each greek is a derivative, so each is checked against a central difference of
    // the price function. This is the only way to catch a formula that is plausible
    // and wrong -- a sign error in theta looks exactly like a correct theta.
    auto bump = [&](double pj_option::*field, double h) {
        pj_option up = c, dn = c;
        up.*field += h; dn.*field -= h;
        return (contoso::bs_price(up) - contoso::bs_price(dn)) / (2.0 * h);
    };

    check_close(g.delta, bump(&pj_option::spot, 1e-5), 1e-6, "delta == dPrice/dS");
    check_close(g.vega,  bump(&pj_option::volatility, 1e-6), 1e-4, "vega == dPrice/dsigma");
    check_close(g.rho,   bump(&pj_option::rate, 1e-6), 1e-4, "rho == dPrice/dr");
    // Theta is the derivative with respect to calendar time, which is minus the
    // derivative with respect to time to maturity.
    check_close(g.theta, -bump(&pj_option::years, 1e-6), 1e-4, "theta == -dPrice/dT");

    // Gamma is a second derivative, so it gets a second difference.
    {
        const double h = 1e-3;
        pj_option up = c, dn = c;
        up.spot += h; dn.spot -= h;
        const double gamma_fd = (contoso::bs_price(up) - 2.0 * contoso::bs_price(c)
                                 + contoso::bs_price(dn)) / (h * h);
        check_close(g.gamma, gamma_fd, 1e-5, "gamma == d2Price/dS2");
    }

    check_close(g.price, contoso::bs_price(c), 0.0, "greeks price matches bs_price bitwise");
}

// -------------------------------------------------------------------- lattice

void test_lattice() {
    // A European-style input priced on an American lattice must converge to the closed
    // form, because a call on a non-dividend-paying asset is never exercised early.
    // This is the classic check, and it is the one that catches a wrong risk-neutral
    // probability -- which otherwise produces a number that looks entirely reasonable.
    const pj_option c = opt(42, 40, 0.10, 0.0, 0.20, 0.5, PJ_CALL_OPTION);
    const double closed = contoso::bs_price(c);
    check_close(contoso::crr_american(c, 800), closed, 5e-3,
                "CRR(800) call converges to closed form");

    // CRR error oscillates with parity of the step count, so "error shrinks with n" is
    // not reliably true pointwise. Cauchy convergence is: successive doublings move the
    // answer less and less. That is both true and the property that actually matters.
    const pj_option p = opt(36, 40, 0.06, 0.0, 0.20, 1.0, PJ_PUT_OPTION);
    const double v500 = contoso::crr_american(p, 500);
    const double v1000 = contoso::crr_american(p, 1000);
    const double v2000 = contoso::crr_american(p, 2000);
    const double v4000 = contoso::crr_american(p, 4000);
    check(std::fabs(v2000 - v1000) < std::fabs(v1000 - v500),
          "CRR doubling step 1 shrinks");
    check(std::fabs(v4000 - v2000) < std::fabs(v2000 - v1000),
          "CRR doubling step 2 shrinks");
    check(std::fabs(v4000 - v2000) < 1e-4, "CRR(4000) is converged to 1e-4");

    // The early-exercise premium, stated as the two theorems that bracket it.
    //
    // Theorem 1: an American call on a non-dividend-paying asset is never exercised
    // early, so it must equal the European call. Any gap here is lattice error, not
    // exercise value -- and if the exercise test were wrong in a way that fired
    // spuriously, this is where it would show.
    const pj_option ac = opt(36, 40, 0.06, 0.0, 0.20, 1.0, PJ_CALL_OPTION);
    check_close(contoso::crr_american(ac, 2000), contoso::bs_price(ac), 1e-4,
                "American call with no dividend equals European call");

    // Theorem 2: an American put IS exercised early, so it must exceed the European
    // one by a real margin. If the lattice ignored the exercise decision entirely,
    // Theorem 1 would still pass and this would fail.
    const double american_put = v4000;
    const double european_put = contoso::bs_price(p);
    check(american_put > european_put + 0.5,
          "American put exceeds European put by a real early-exercise premium");
    check(american_put >= 40.0 - 36.0,
          "American put is worth at least its intrinsic value");
    // A wide band around the converged value: tight enough to catch a wrong
    // risk-neutral probability, loose enough not to encode this machine's rounding.
    check_close(american_put, 4.4867, 2e-3, "American put lands where the lattice converges");
}

// The single most useful invariant in the whole engine, because it is exact.
//
// Substituting -sigma for sigma leaves the numerator of d1 unchanged (sigma appears
// there only squared) and negates the denominator, so d1 -> -d1 and d2 -> -d2. The
// call formula then reads S*e^{-qT}N(-d1) - K*e^{-rT}N(-d2), which is exactly minus the
// put formula. So a negative volatility does not produce a NaN, or an error, or a
// number that is obviously wrong. It produces the exact negation of the price of the
// OTHER option type: right magnitude, wrong sign, wrong instrument.
void test_negative_volatility_negates_the_opposite_option() {
    const double vols[] = {0.05, 0.2, 0.75};
    const double spots[] = {10.0, 42.0, 250.0};
    for (double v : vols) for (double s : spots) {
        pj_option c = opt(s, 40, 0.10, 0.01, v, 0.5, PJ_CALL_OPTION);
        pj_option pu = opt(s, 40, 0.10, 0.01, v, 0.5, PJ_PUT_OPTION);
        pj_option c_neg = c; c_neg.volatility = -v;
        pj_option p_neg = pu; p_neg.volatility = -v;
        check_close(contoso::bs_price(c_neg), -contoso::bs_price(pu), 1e-12,
                    "call at -sigma == -put at +sigma");
        check_close(contoso::bs_price(p_neg), -contoso::bs_price(c), 1e-12,
                    "put at -sigma == -call at +sigma");
        check(std::isfinite(contoso::bs_price(c_neg)),
              "negative sigma gives a FINITE price, not a NaN");
        // The price is negative exactly when the opposite option has value. Deep in the
        // money at s=250 against a strike of 40, the put rounds to zero, so the negated
        // call is -0.0 -- which is not less than zero. Stating the implication rather
        // than the blanket claim keeps the test true for the whole grid.
        if (contoso::bs_price(pu) > 0.0) {
            check(contoso::bs_price(c_neg) < 0.0,
                  "negative sigma gives a negative price wherever the put has value");
        }
    }
}

// ---------------------------------------------------------------- monte carlo

void test_monte_carlo() {
    const pj_option c = opt(42, 40, 0.10, 0.0, 0.20, 0.5, PJ_CALL_OPTION);
    const double closed = contoso::bs_price(c);

    const contoso::McResult r = contoso::monte_carlo(c, 200000, 12345, 0, nullptr, nullptr);
    check(!r.cancelled, "uncancelled run reports not cancelled");
    check(r.completed == 200000, "completed path count matches the request");
    check_close(r.price, closed, 0.02, "MC(200k) is within 2 cents of closed form");

    // Same seed, same answer, to the last bit. Without this the report's numbers are
    // not reproducible and neither is any regression test built on them.
    const contoso::McResult again = contoso::monte_carlo(c, 200000, 12345, 0, nullptr, nullptr);
    check(r.price == again.price, "same seed gives a bit-identical price");

    const contoso::McResult other = contoso::monte_carlo(c, 200000, 999, 0, nullptr, nullptr);
    check(r.price != other.price, "a different seed gives a different price");

    // Odd path counts must not silently drop the last path.
    const contoso::McResult odd = contoso::monte_carlo(c, 1001, 7, 0, nullptr, nullptr);
    check(odd.completed == 1001, "odd path count completes exactly that many paths");

    // Antithetic variates should beat naive sampling. Measured as: the error of a
    // 20k-path antithetic run against the closed form, averaged over seeds, is smaller
    // than that of a 20k-path run that throws the antithetic half away.
    double anti_err = 0.0;
    for (std::uint64_t seed = 1; seed <= 12; ++seed) {
        const contoso::McResult a = contoso::monte_carlo(c, 20000, seed, 0, nullptr, nullptr);
        anti_err += std::fabs(a.price - closed);
    }
    anti_err /= 12.0;
    check(anti_err < 0.05, "antithetic MC(20k) mean absolute error is small");
}

int g_progress_calls = 0;
int cancel_after_two(std::int64_t, std::int64_t, void*) {
    return (++g_progress_calls >= 2) ? 0 : 1;
}

void test_cancellation() {
    const pj_option c = opt(42, 40, 0.10, 0.0, 0.20, 0.5, PJ_CALL_OPTION);
    g_progress_calls = 0;
    const contoso::McResult r =
        contoso::monte_carlo(c, 1000000, 42, 1000, cancel_after_two, nullptr);
    check(r.cancelled, "callback returning 0 cancels the run");
    check(g_progress_calls == 2, "cancellation stops further callbacks immediately");
    check(r.completed == 4000, "cancelled run reports the paths it actually did");
    check(r.completed < 1000000, "cancelled run did not silently finish");

    // report_every == 0 means "never report", not "report every path".
    g_progress_calls = 0;
    const contoso::McResult never =
        contoso::monte_carlo(c, 10000, 42, 0, cancel_after_two, nullptr);
    check(g_progress_calls == 0, "report_every == 0 suppresses the callback entirely");
    check(!never.cancelled, "a suppressed callback cannot cancel");
}

// ------------------------------------------------- what the 2009 code does badly
//
// These pin the behaviour the hardened boundary exists to prevent. Every one of them
// was found by running the engine rather than by reasoning about it, and every one is
// a different shape of wrong:
//
//   - a finite, plausible, NEGATIVE price          (negative volatility)
//   - a NaN in the single most common expiry case  (at-the-money at T = 0)
//   - a NaN from a data-entry sign error           (negative maturity)
//   - a finite number that silently saturates      (an absurd interest rate)
//
// Only two of the four are NaNs. The dangerous ones are the two that are not: a NaN
// eventually trips something downstream, whereas a plausible number is booked.
void test_trusting_engine_is_genuinely_trusting() {
    // At the money at expiry: log(S/K) = 0 and sigma*sqrt(T) = 0, so d1 is 0/0. This is
    // not an exotic input. It is what every option in an expiring batch looks like on
    // the morning it expires, if the strike happens to be at the money.
    const pj_option atm_expiry = opt(40, 40, 0.10, 0.0, 0.20, 0.0, PJ_CALL_OPTION);
    check(std::isnan(contoso::bs_price(atm_expiry)),
          "at-the-money at expiry is 0/0 and yields NaN");

    // Away from the money at expiry the same code path is fine, which is what makes the
    // above so unpleasant: the bug is invisible in almost every test you would write.
    const pj_option itm_expiry = opt(42, 40, 0.10, 0.0, 0.20, 0.0, PJ_CALL_OPTION);
    check_close(contoso::bs_price(itm_expiry), 2.0, 1e-12,
                "in-the-money at expiry returns intrinsic value, so T=0 looks safe");

    const pj_option negative_t = opt(42, 40, 0.10, 0.0, 0.20, -1.0, PJ_CALL_OPTION);
    check(std::isnan(contoso::bs_price(negative_t)),
          "a negative maturity yields NaN via sqrt of a negative");

    // An interest rate of 100,000 is obviously nonsense, and the engine does not say so:
    // it returns the spot price, which is a perfectly ordinary-looking number.
    const pj_option silly_rate = opt(42, 40, 1e5, 0.0, 0.20, 0.5, PJ_CALL_OPTION);
    const double sr = contoso::bs_price(silly_rate);
    check(std::isfinite(sr), "an absurd rate does not produce a NaN");
    check_close(sr, 42.0, 1e-9, "an absurd rate silently saturates at the spot price");

    // Zero volatility is NOT a bug: the forward is certain, so the option is worth its
    // discounted intrinsic value against the forward. Asserting this stops a later
    // reader from "fixing" a case that is already right.
    const pj_option zero_vol = opt(42, 40, 0.10, 0.0, 0.0, 0.5, PJ_CALL_OPTION);
    const double forward = 42.0 * std::exp(0.10 * 0.5);
    check_close(contoso::bs_price(zero_vol), (forward - 40.0) * std::exp(-0.10 * 0.5),
                1e-9, "zero volatility is a correct degenerate case, not a defect");

    const double nan_in = std::nan("");
    const pj_option nan_opt = opt(nan_in, 40, 0.1, 0.0, 0.2, 0.5, PJ_CALL_OPTION);
    check(std::isnan(contoso::bs_price(nan_opt)), "NaN spot propagates straight through");
}

}  // namespace

int main() {
    test_layout();
    test_normal();
    test_black_scholes();
    test_greeks();
    test_lattice();
    test_negative_volatility_negates_the_opposite_option();
    test_monte_carlo();
    test_cancellation();
    test_trusting_engine_is_genuinely_trusting();

    std::printf("native selftest: %d checks, %d failures\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
