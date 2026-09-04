// abi.cpp -- the boundary.
//
// This file is the whole project. engine.cpp is 2009 code that computes correctly and
// trusts absolutely; this shim is what stands between it and a garbage-collected
// runtime that will happily hand it a null, a negative length, or a NaN.
//
// It compiles two ways from the same source:
//
//   PJ_HARDENED=1  -> pricing.dll        the boundary as it must be
//   PJ_HARDENED=0  -> pricing_legacy.dll the boundary as it was found
//
// Both link the identical engine.cpp. Every behavioural difference the fuzz
// differential in docs/results.md reports is therefore attributable to this file and
// to nothing else -- which is the argument the project exists to make: the engine did
// not need to change.

#define PJ_BUILDING_DLL 1

#include "pricing_abi.h"
#include "engine.hpp"

#include <cstdio>
#include <cstring>
#include <cmath>
#include <atomic>
#include <new>
#include <string>

#ifndef PJ_HARDENED
#  define PJ_HARDENED 1
#endif

// ---------------------------------------------------------------- layout contract
//
// The .NET side declares structs it believes are identical to these. If that belief is
// wrong, nothing fails loudly: fields silently read from the wrong offsets and the
// desk books a price computed from a strike that was actually a dividend yield. These
// assertions are the only thing that turns that into a build error.

static_assert(sizeof(pj_option) == 56, "pj_option must be exactly 56 bytes");
static_assert(sizeof(pj_greeks) == 48, "pj_greeks must be exactly 48 bytes");
static_assert(alignof(pj_option) == 8, "pj_option must be 8-byte aligned");
static_assert(offsetof(pj_option, spot) == 0, "pj_option.spot at offset 0");
static_assert(offsetof(pj_option, strike) == 8, "pj_option.strike at offset 8");
static_assert(offsetof(pj_option, rate) == 16, "pj_option.rate at offset 16");
static_assert(offsetof(pj_option, dividend) == 24, "pj_option.dividend at offset 24");
static_assert(offsetof(pj_option, volatility) == 32, "pj_option.volatility at offset 32");
static_assert(offsetof(pj_option, years) == 40, "pj_option.years at offset 40");
static_assert(offsetof(pj_option, kind) == 48, "pj_option.kind at offset 48");
static_assert(offsetof(pj_option, reserved) == 52, "pj_option.reserved at offset 52");
// kind and reserved fill the trailing 8 bytes so the struct needs no tail padding. If
// either moved, the .NET struct would still be 56 bytes and would still "work" on every
// value that happened to be zero -- which is exactly why each offset is asserted and
// not merely the total.
static_assert(sizeof(double) == 8, "IEEE-754 binary64 assumed throughout");

// -------------------------------------------------------------------- diagnostics

namespace {
std::atomic<std::int64_t> g_created{0};
std::atomic<std::int64_t> g_destroyed{0};

// The engine's max lattice size. Above this the O(steps^2) loop stops being a
// computation and becomes a denial of service; the legacy boundary has no such notion.
constexpr int kMaxSteps = 20000;
constexpr std::int64_t kMaxPaths = 200000000LL;
constexpr std::int32_t kMaxBatch = 10000000;

void set_error(contoso::Engine* e, const char* msg) noexcept {
    if (e == nullptr) return;
    try {
        e->last_error.assign(msg);
    } catch (...) {
        // A failure to record why something failed must not itself fail.
    }
}

#if PJ_HARDENED
// The largest ratio between spot and strike the boundary will price.
//
// Above this the option's value is its intrinsic to far beyond anything a double can
// represent, so nothing of economic content is lost by refusing. Below it, nothing is
// refused that anybody could ever want. The only way a pair this far apart arrives is
// corruption -- a strike read from the wrong column, a spot in minor units scaled once
// too often -- and the answer such a pair produces is the worst kind: finite,
// non-negative, and completely detached from the trade.
constexpr double kMaxMoneyness = 1e12;

// Returns nullptr if the option is fit to price, or a message naming the field that
// is not.
//
// This returns a string rather than a bool because of what the alternative costs
// downstream. A boundary that rejects cleanly and reports only "bad argument" has not
// solved the problem, it has moved it: the trade still does not price, and now the
// desk has to bisect a 56-byte struct by hand to find out why. Every field checked
// here can say which one it was, so it does.
//
// Every field is checked for finiteness as well as for its domain. A NaN that reaches
// the engine comes back out as a NaN that looks like a number all the way to the
// ledger.
const char* option_defect(const pj_option& o) noexcept {
    if (!std::isfinite(o.spot))       return "spot is not a finite number";
    if (o.spot <= 0.0)                return "spot must be greater than zero";
    if (!std::isfinite(o.strike))     return "strike is not a finite number";
    if (o.strike <= 0.0)              return "strike must be greater than zero";
    if (!std::isfinite(o.rate))       return "rate is not a finite number";
    if (std::fabs(o.rate) > 10.0)     return "rate is outside the supported range of +/-1000%";
    if (!std::isfinite(o.dividend))   return "dividend is not a finite number";
    if (std::fabs(o.dividend) > 10.0) return "dividend is outside the supported range of +/-1000%";
    if (!std::isfinite(o.volatility)) return "volatility is not a finite number";
    if (o.volatility < 0.0)           return "volatility must not be negative";
    if (o.volatility > 100.0)         return "volatility is outside the supported range of 10000%";
    if (!std::isfinite(o.years))      return "years is not a finite number";
    if (o.years < 0.0)                return "years must not be negative";
    if (o.years > 200.0)              return "years is outside the supported range of 200";
    if (o.kind != PJ_CALL_OPTION && o.kind != PJ_PUT_OPTION)
        return "kind must be 0 for a call or 1 for a put";
    // Reserved means reserved. A non-zero value is a caller who believes this field
    // carries something, which means their struct and this one have diverged.
    if (o.reserved != 0)              return "reserved must be zero";
    if (o.strike > o.spot * kMaxMoneyness)
        return "strike is implausibly large relative to spot";
    if (o.spot > o.strike * kMaxMoneyness)
        return "spot is implausibly large relative to strike";
    return nullptr;
}

// Handles the two legal inputs the 2009 core cannot evaluate.
//
// At zero volatility, or at expiry, d1 is a division by zero and the core -- which is
// compiled byte for byte identically into both DLLs and has to stay that way, because
// that identity is the entire claim of this project -- propagates the resulting NaN
// without comment. When spot equals strike at expiry it is 0/0 in the numerator too.
//
// The first version of this boundary handled that by refusing both. That was wrong,
// and wrong in the most expensive direction: every option expiring today has years
// equal to zero, on every expiry date, for every strike on the book. A boundary whose
// safety comes from rejecting the most routine input in the business is not safe, it
// is unusable, and it will be switched off by the first person who has to close the
// books on a Friday.
//
// Both cases have a deterministic payoff, so the boundary evaluates the limit itself
// rather than asking the core a question the core was never written to answer. This is
// what an anti-corruption layer is for: the legacy is not modified, and the seam
// absorbs the difference.
bool degenerate_price(const pj_option& o, double* out) noexcept {
    if (o.years > 0.0 && o.volatility > 0.0) return false;
    const double forward = o.spot * std::exp((o.rate - o.dividend) * o.years);
    const double intrinsic =
        (o.kind == PJ_CALL_OPTION) ? forward - o.strike : o.strike - forward;
    *out = std::exp(-o.rate * o.years) * (intrinsic > 0.0 ? intrinsic : 0.0);
    return true;
}
#endif

}  // namespace

// The two macros below are the whole of the exception story. A C++ exception unwinding
// through a C ABI frame is undefined behaviour, and on MSVC/x64 it terminates the
// process -- which for a .NET host means the CLR dies with no stack, no finally blocks,
// and no chance to flush anything. The hardened build wraps every entry point; the
// legacy build expands both macros to nothing, which is precisely what it did in 2009.
#if PJ_HARDENED
#  define PJ_TRY try {
#  define PJ_CATCH(engine)                                                    \
      } catch (const std::bad_alloc&) {                                       \
          set_error((engine), "allocation failed inside the engine");         \
          return PJ_ERR_OVERFLOW;                                             \
      } catch (const std::exception& ex) {                                    \
          set_error((engine), ex.what());                                     \
          return PJ_ERR_INTERNAL;                                             \
      } catch (...) {                                                         \
          set_error((engine), "unknown C++ exception at the ABI boundary");   \
          return PJ_ERR_INTERNAL;                                             \
      }
#else
#  define PJ_TRY
#  define PJ_CATCH(engine)
#endif

// In the legacy build the validation blocks vanish, and with them every use of the
// engine pointer and the capacity argument. Marking them consumed keeps /W4 /WX
// meaningful for both builds instead of silencing the warning globally.
#if PJ_HARDENED
#  define PJ_LEGACY_UNUSED(x) ((void)0)
#else
#  define PJ_LEGACY_UNUSED(x) ((void)(x))
#endif

extern "C" {

PJ_API int32_t PJ_CALL pj_abi_version(void) {
    return 1 * 10000 + 0 * 100 + 0;
}

PJ_API pj_status PJ_CALL pj_engine_create(uint64_t seed, pj_engine** out) {
#if PJ_HARDENED
    if (out == nullptr) return PJ_ERR_NULL_ARG;
    *out = nullptr;
#endif
    PJ_TRY
        auto* e = new contoso::Engine();
        e->seed = seed;
        g_created.fetch_add(1, std::memory_order_relaxed);
        *out = reinterpret_cast<pj_engine*>(e);
        return PJ_OK;
    PJ_CATCH(nullptr)
}

PJ_API void PJ_CALL pj_engine_destroy(pj_engine* engine) {
    if (engine == nullptr) return;
    auto* e = reinterpret_cast<contoso::Engine*>(engine);
    PJ_LEGACY_UNUSED(e);
    g_destroyed.fetch_add(1, std::memory_order_relaxed);
    delete e;
}

PJ_API pj_status PJ_CALL pj_last_error(const pj_engine* engine,
                                       char* buf, int32_t cap, int32_t* needed) {
    const auto* e = reinterpret_cast<const contoso::Engine*>(engine);
#if PJ_HARDENED
    if (e == nullptr) return PJ_ERR_NULL_ARG;
    if (cap < 0) return PJ_ERR_BAD_ARG;
    if (cap > 0 && buf == nullptr) return PJ_ERR_NULL_ARG;

    const std::size_t len = e->last_error.size();
    if (len + 1 > static_cast<std::size_t>(INT32_MAX)) return PJ_ERR_OVERFLOW;
    const int32_t required = static_cast<int32_t>(len) + 1;
    if (needed != nullptr) *needed = required;
    if (cap < required) return PJ_ERR_CAPACITY;

    std::memcpy(buf, e->last_error.c_str(), len);
    buf[len] = '\0';
    return PJ_OK;
#else
    // 2009: the caller was known to pass a 256-byte buffer, so the length was known
    // to fit. strcpy writes the message and its NUL wherever they land.
    if (needed != nullptr) *needed = static_cast<int32_t>(e->last_error.size()) + 1;
    std::strcpy(buf, e->last_error.c_str());
    (void)cap;
    return PJ_OK;
#endif
}

PJ_API pj_status PJ_CALL pj_price_european(pj_engine* engine,
                                           const pj_option* opt,
                                           double* out_price) {
    auto* e = reinterpret_cast<contoso::Engine*>(engine);
    PJ_LEGACY_UNUSED(e);
#if PJ_HARDENED
    if (e == nullptr || opt == nullptr || out_price == nullptr) return PJ_ERR_NULL_ARG;
    *out_price = 0.0;
    if (const char* defect = option_defect(*opt)) {
        set_error(e, defect);
        return PJ_ERR_BAD_ARG;
    }
    if (degenerate_price(*opt, out_price)) {
        return PJ_OK;
    }
#endif
    PJ_TRY
        const double p = contoso::bs_price(*opt);
#if PJ_HARDENED
        // Sane inputs should give a finite price. If they did not, the model has found
        // a corner the validation did not, and saying so is better than returning it.
        if (!std::isfinite(p)) {
            set_error(e, "model produced a non-finite price from validated inputs");
            return PJ_ERR_NOT_CONVERGED;
        }
#endif
        *out_price = p;
        return PJ_OK;
    PJ_CATCH(e)
}

PJ_API pj_status PJ_CALL pj_greeks_european(pj_engine* engine,
                                            const pj_option* opt,
                                            pj_greeks* out) {
    auto* e = reinterpret_cast<contoso::Engine*>(engine);
    PJ_LEGACY_UNUSED(e);
#if PJ_HARDENED
    if (e == nullptr || opt == nullptr || out == nullptr) return PJ_ERR_NULL_ARG;
    std::memset(out, 0, sizeof(*out));
    if (const char* defect = option_defect(*opt)) {
        set_error(e, defect);
        return PJ_ERR_BAD_ARG;
    }
    // Unlike the price, the greeks have no limit to take here: gamma and vega are a
    // delta function and zero respectively at zero volatility, and theta is unbounded
    // at expiry. Returning a number for any of those would be inventing one. The
    // boundary says so instead of letting the NaN check below report it as an internal
    // failure, because this is a property of the mathematics, not a defect.
    if (opt->years <= 0.0 || opt->volatility <= 0.0) {
        set_error(e, "greeks are undefined at zero volatility or at expiry");
        return PJ_ERR_BAD_ARG;
    }
#endif
    PJ_TRY
        const pj_greeks g = contoso::bs_greeks(*opt);
#if PJ_HARDENED
        const double* fields = reinterpret_cast<const double*>(&g);
        for (int i = 0; i < 6; ++i) {
            if (!std::isfinite(fields[i])) {
                set_error(e, "model produced a non-finite greek from validated inputs");
                return PJ_ERR_NOT_CONVERGED;
            }
        }
#endif
        *out = g;
        return PJ_OK;
    PJ_CATCH(e)
}

PJ_API pj_status PJ_CALL pj_price_american(pj_engine* engine,
                                           const pj_option* opt,
                                           int32_t steps,
                                           double* out_price) {
    auto* e = reinterpret_cast<contoso::Engine*>(engine);
    PJ_LEGACY_UNUSED(e);
#if PJ_HARDENED
    if (e == nullptr || opt == nullptr || out_price == nullptr) return PJ_ERR_NULL_ARG;
    *out_price = 0.0;
    if (const char* defect = option_defect(*opt)) {
        set_error(e, defect);
        return PJ_ERR_BAD_ARG;
    }
    if (steps < 1) {
        set_error(e, "steps must be at least 1");
        return PJ_ERR_BAD_ARG;
    }
    if (steps > kMaxSteps) {
        // Not a memory-safety issue: a work-amount issue. steps^2 at 2 billion is a
        // request to occupy a core until the process is killed. Refusing is a
        // capacity answer, not a correctness one.
        set_error(e, "steps exceeds the supported lattice size");
        return PJ_ERR_BAD_ARG;
    }
#endif
    PJ_TRY
        const double p = contoso::crr_american(*opt, steps);
#if PJ_HARDENED
        if (!std::isfinite(p)) {
            set_error(e, "lattice produced a non-finite price");
            return PJ_ERR_NOT_CONVERGED;
        }
#endif
        *out_price = p;
        return PJ_OK;
    PJ_CATCH(e)
}

PJ_API pj_status PJ_CALL pj_price_batch(pj_engine* engine,
                                        const pj_option* opts, int32_t count,
                                        double* out_prices, int32_t out_capacity) {
    PJ_LEGACY_UNUSED(out_capacity);
    auto* e = reinterpret_cast<contoso::Engine*>(engine);
    PJ_LEGACY_UNUSED(e);
#if PJ_HARDENED
    if (e == nullptr) return PJ_ERR_NULL_ARG;
    if (count < 0) {
        set_error(e, "count must not be negative");
        return PJ_ERR_BAD_ARG;
    }
    if (count > kMaxBatch) {
        set_error(e, "count exceeds the supported batch size");
        return PJ_ERR_OVERFLOW;
    }
    if (count == 0) return PJ_OK;          // a legal, meaningful request
    if (opts == nullptr || out_prices == nullptr) return PJ_ERR_NULL_ARG;
    if (out_capacity < count) {
        set_error(e, "output buffer smaller than the batch");
        return PJ_ERR_CAPACITY;
    }
    for (int32_t i = 0; i < count; ++i) {
        if (const char* defect = option_defect(opts[i])) {
            // Fail the whole batch rather than write a NaN into one slot. A partial
            // result the caller cannot distinguish from a complete one is worse than
            // no result: the caller has no way to know which prices to trust.
            char msg[160];
            std::snprintf(msg, sizeof(msg), "option %d of %d rejected: %s",
                          i, count, defect);
            set_error(e, msg);
            return PJ_ERR_BAD_ARG;
        }
    }
#endif
    PJ_TRY
        for (int32_t i = 0; i < count; ++i) {
#if PJ_HARDENED
            if (degenerate_price(opts[i], &out_prices[i])) continue;
#endif
            out_prices[i] = contoso::bs_price(opts[i]);
        }
        return PJ_OK;
    PJ_CATCH(e)
}

PJ_API pj_status PJ_CALL pj_price_monte_carlo(pj_engine* engine,
                                              const pj_option* opt,
                                              int64_t paths,
                                              int64_t report_every,
                                              pj_progress_fn progress,
                                              void* user,
                                              double* out_price) {
    auto* e = reinterpret_cast<contoso::Engine*>(engine);
    PJ_LEGACY_UNUSED(e);
#if PJ_HARDENED
    if (e == nullptr || opt == nullptr || out_price == nullptr) return PJ_ERR_NULL_ARG;
    *out_price = 0.0;
    if (const char* defect = option_defect(*opt)) {
        set_error(e, defect);
        return PJ_ERR_BAD_ARG;
    }
    if (paths < 1 || paths > kMaxPaths) {
        set_error(e, "paths outside the supported range");
        return PJ_ERR_BAD_ARG;
    }
    if (report_every < 0) {
        set_error(e, "report_every must not be negative");
        return PJ_ERR_BAD_ARG;
    }
#endif
    PJ_TRY
        const contoso::McResult r = contoso::monte_carlo(
            *opt, paths, e->seed, report_every, progress, user);
        *out_price = r.price;
        if (r.cancelled) {
            set_error(e, "cancelled by caller progress callback");
            return PJ_ERR_CANCELLED;
        }
        return PJ_OK;
    PJ_CATCH(e)
}

PJ_API void PJ_CALL pj_engine_stats(int64_t* out_created, int64_t* out_destroyed) {
    if (out_created != nullptr) *out_created = g_created.load(std::memory_order_relaxed);
    if (out_destroyed != nullptr) *out_destroyed = g_destroyed.load(std::memory_order_relaxed);
}

PJ_API void PJ_CALL pj_noop(void) {
    // Must not be empty, or the linker may fold it with another empty function and
    // the measurement stops measuring what it claims to.
    static volatile int sink = 0;
    sink = sink + 1;
}

PJ_API double PJ_CALL pj_noop_option(const pj_option* opt) {
    // Reads one field so that the 48 bytes genuinely have to arrive.
    return opt == nullptr ? 0.0 : opt->spot;
}

PJ_API void PJ_CALL pj_burn(int32_t micros) {
    if (micros <= 0) return;
    // A busy loop calibrated at first call. Deliberately does not sleep: the point of
    // this function is to hold a thread inside native code while the runtime tries to
    // do something that needs every thread to reach a safe point.
    volatile double acc = 1.0;
    const long long iterations = static_cast<long long>(micros) * 220LL;
    for (long long i = 0; i < iterations; ++i) {
        acc = acc * 1.0000001 + 1e-9;
        if (acc > 1e18) acc = 1.0;
    }
}

}  // extern "C"
