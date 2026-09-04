// engine.hpp -- the pricing core. This is the part nobody is authorised to rewrite.
//
// It is written the way it was written in 2009: it computes, and it trusts. It does
// not validate its inputs, because in 2009 it had exactly one caller and that caller
// was known to be sane. Every function here is total in the mathematical sense and
// partial in the practical one: give it a zero volatility and it will hand you back a
// NaN with a straight face.
//
// Nothing in this header changes between the three DLLs this project builds. The
// difference between "safe to expose to the internet" and "will corrupt memory if you
// look at it wrong" lives entirely in abi.cpp.

#ifndef CONTOSO_ENGINE_HPP
#define CONTOSO_ENGINE_HPP

#include <cstdint>
#include <string>
#include <vector>

#include "pricing_abi.h"

namespace contoso {

// xoshiro256** -- chosen over std::mt19937 because its output is defined by the
// algorithm rather than by the standard library implementation, so a given seed gives
// the same stream under any compiler. That property is load-bearing: a Monte Carlo
// price that changes when you change vendors is not a price, it is an opinion.
class Rng {
public:
    explicit Rng(std::uint64_t seed) noexcept { reseed(seed); }

    void reseed(std::uint64_t seed) noexcept {
        // SplitMix64 to spread a single integer into four words of state.
        for (int i = 0; i < 4; ++i) {
            seed += 0x9E3779B97F4A7C15ull;
            std::uint64_t z = seed;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
            s_[i] = z ^ (z >> 31);
        }
    }

    std::uint64_t next_u64() noexcept {
        const std::uint64_t result = rotl(s_[1] * 5, 7) * 9;
        const std::uint64_t t = s_[1] << 17;
        s_[2] ^= s_[0];
        s_[3] ^= s_[1];
        s_[1] ^= s_[2];
        s_[0] ^= s_[3];
        s_[2] ^= t;
        s_[3] = rotl(s_[3], 45);
        return result;
    }

    // Uniform in (0, 1). The open interval matters: a 0 would become -inf under the
    // inverse CDF and poison the whole path.
    double next_uniform() noexcept {
        // 53 significant bits, then nudged off zero.
        const std::uint64_t bits = next_u64() >> 11;
        const double u = static_cast<double>(bits) * (1.0 / 9007199254740992.0);
        return u <= 0.0 ? 1.0 / 9007199254740992.0 : u;
    }

private:
    static std::uint64_t rotl(std::uint64_t x, int k) noexcept {
        return (x << k) | (x >> (64 - k));
    }
    std::uint64_t s_[4]{};
};

// Standard normal cumulative distribution, via erfc. Accurate to within an ulp or two
// across the whole range, which is more than the rest of the model deserves.
double norm_cdf(double x) noexcept;

// Standard normal density.
double norm_pdf(double x) noexcept;

// Inverse standard normal CDF (Acklam's rational approximation, refined by one
// Halley step against erfc). Used instead of Box-Muller because it consumes exactly
// one uniform per normal, which keeps the path count and the draw count in step and
// makes an antithetic pair exactly a sign flip.
double norm_inv(double p) noexcept;

// Black-Scholes-Merton closed form. No validation: sigma == 0 divides by zero here.
double bs_price(const pj_option& o) noexcept;

// Closed form plus the five sensitivities the desk actually looks at. Computed
// together because they share d1, d2 and the two discount factors -- and because
// returning them separately would mean five more boundary crossings.
pj_greeks bs_greeks(const pj_option& o) noexcept;

// Cox-Ross-Rubinstein lattice with early exercise at every node. Allocates a vector
// of `steps + 1` doubles. In 2009 nobody passed a negative step count.
double crr_american(const pj_option& o, int steps);

// Antithetic-variate Monte Carlo. Returns through `out`; may return false if the
// caller's progress callback asked to stop.
struct McResult { double price; bool cancelled; std::int64_t completed; };
McResult monte_carlo(const pj_option& o, std::int64_t paths, std::uint64_t seed,
                     std::int64_t report_every,
                     pj_progress_fn progress, void* user) noexcept;

// The engine object itself. Holds the seed and the last error message. Deliberately
// small: everything expensive is per call, so that the host's ownership story is about
// one handle rather than a graph of them.
struct Engine {
    std::uint64_t seed{};
    std::string last_error;
    // A canary either side of nothing in particular. The legacy build's
    // pj_last_error writes past the end of the caller's buffer; the fuzz harness
    // needs a way to observe that reliably rather than by hoping for a page fault.
    std::uint32_t guard_head{0xC0FFEE01u};
    std::uint32_t guard_tail{0xC0FFEE02u};
};

}  // namespace contoso

#endif  // CONTOSO_ENGINE_HPP
