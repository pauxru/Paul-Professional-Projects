/*
 * pricing_abi.h -- the narrow, stable C ABI of the Contoso pricing engine.
 *
 * This header is the ONLY thing the .NET host is allowed to know about. The C++
 * behind it is 2009 code that nobody is authorised to rewrite; the rules below are
 * what make it safe to call from a garbage-collected, memory-safe runtime.
 *
 * ABI rules (these are the contract, not documentation):
 *
 *   1. Every exported function is `noexcept` in practice: no C++ exception may cross
 *      this boundary. Failures are reported as pj_status codes. See abi.cpp.
 *   2. Every function that can fail returns pj_status. Results travel through out
 *      parameters. There is no "returns -1 on error" overloading of a value channel.
 *   3. Ownership never transitions implicitly. The only heap object the caller owns is
 *      pj_engine, created by pj_engine_create and released by pj_engine_destroy.
 *      Every buffer is caller-allocated; the engine never returns a pointer the caller
 *      must free.
 *   4. All structs here are C-layout, fixed-width, and contain no pointers, no bool,
 *      and no enums whose width is implementation-defined. They are blittable by
 *      construction so that .NET can pass them without marshalling.
 *   5. No struct contains padding that the caller must guess: each is explicitly sized
 *      and asserted with static_assert in abi.cpp.
 *   6. The ABI is versioned. pj_abi_version() must be checked before anything else.
 */
#ifndef CONTOSO_PRICING_ABI_H
#define CONTOSO_PRICING_ABI_H

#include <stdint.h>

#if defined(_WIN32)
#  if defined(PJ_BUILDING_DLL)
#    define PJ_API __declspec(dllexport)
#  else
#    define PJ_API __declspec(dllimport)
#  endif
#  define PJ_CALL __cdecl
#else
#  define PJ_API __attribute__((visibility("default")))
#  define PJ_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* ---------------------------------------------------------------- status codes */

typedef int32_t pj_status;

#define PJ_OK                 0
#define PJ_ERR_NULL_ARG       1  /* a required pointer was null                     */
#define PJ_ERR_BAD_ARG        2  /* a value was outside its documented domain       */
#define PJ_ERR_CAPACITY       3  /* caller buffer too small; *needed says how much  */
#define PJ_ERR_OVERFLOW       4  /* a size computation would not fit                */
#define PJ_ERR_CANCELLED      5  /* the caller's progress callback asked to stop    */
#define PJ_ERR_NOT_CONVERGED  6  /* the numerical method did not converge           */
#define PJ_ERR_INTERNAL       7  /* a C++ exception was caught at the boundary      */

/* --------------------------------------------------------------------- version */

/* Encoded as major*10000 + minor*100 + patch. The host refuses to load on mismatch
 * of the major component. */
PJ_API int32_t PJ_CALL pj_abi_version(void);

/* ------------------------------------------------------------------ value types */

/* Option kind. int32_t, not an enum: enum width is implementation-defined in C and
 * this struct must be blittable against a .NET struct with a known layout. */
#define PJ_CALL_OPTION 0
#define PJ_PUT_OPTION  1

/* Exactly 56 bytes, 8-byte aligned, no padding holes. static_assert'd in abi.cpp. */
typedef struct pj_option {
    double  spot;       /* S  > 0                          */
    double  strike;     /* K  > 0                          */
    double  rate;       /* r, continuously compounded      */
    double  dividend;   /* q, continuous yield             */
    double  volatility; /* sigma > 0                       */
    double  years;      /* T  > 0                          */
    int32_t kind;       /* PJ_CALL_OPTION | PJ_PUT_OPTION  */
    int32_t reserved;   /* must be 0; keeps the struct 8-byte aligned with no hole */
} pj_option;

/* Exactly 48 bytes. Greeks are returned alongside the price because computing them
 * separately would mean crossing the boundary five more times -- the shape of the ABI
 * is itself a performance decision. */
typedef struct pj_greeks {
    double price;
    double delta;
    double gamma;
    double vega;
    double theta;
    double rho;
} pj_greeks;

/* ---------------------------------------------------------------------- engine */

/* Opaque. The caller never learns the size or layout, so the C++ side is free to
 * change both without breaking the host. */
typedef struct pj_engine pj_engine;

/* Creates an engine. *out receives the handle on PJ_OK and is set to NULL otherwise.
 * `seed` seeds the Monte Carlo generator; equal seeds give bit-identical paths. */
PJ_API pj_status PJ_CALL pj_engine_create(uint64_t seed, pj_engine** out);

/* Releases an engine. Passing NULL is a no-op, deliberately: the .NET SafeHandle
 * releases from a critical finalizer where throwing is not an option. */
PJ_API void PJ_CALL pj_engine_destroy(pj_engine* engine);

/* Copies the last error message for `engine` into `buf` as NUL-terminated ASCII.
 * If cap is too small, returns PJ_ERR_CAPACITY and writes the required size
 * (including the NUL) to *needed. buf may be NULL if cap is 0 -- that is the
 * documented way to ask "how big a buffer do I need?". */
PJ_API pj_status PJ_CALL pj_last_error(const pj_engine* engine,
                                       char* buf, int32_t cap, int32_t* needed);

/* ---------------------------------------------------------------------- pricing */

/* Closed-form Black-Scholes-Merton. Cheap: hundreds of nanoseconds. This is the
 * function that makes boundary overhead visible, because there is barely any work
 * behind it. */
PJ_API pj_status PJ_CALL pj_price_european(pj_engine* engine,
                                           const pj_option* opt,
                                           double* out_price);

/* Closed form plus all five first- and second-order sensitivities, in one crossing. */
PJ_API pj_status PJ_CALL pj_greeks_european(pj_engine* engine,
                                            const pj_option* opt,
                                            pj_greeks* out);

/* Cox-Ross-Rubinstein binomial lattice with early exercise. `steps` in [1, 20000].
 * Cost is O(steps^2): at 512 steps this is ~100us of real work, which is what makes
 * it the control case for "when does the boundary stop mattering". */
PJ_API pj_status PJ_CALL pj_price_american(pj_engine* engine,
                                           const pj_option* opt,
                                           int32_t steps,
                                           double* out_price);

/* Prices `count` options into a caller-allocated array of `count` doubles.
 * One crossing for the whole portfolio. */
PJ_API pj_status PJ_CALL pj_price_batch(pj_engine* engine,
                                        const pj_option* opts, int32_t count,
                                        double* out_prices, int32_t out_capacity);

/* -------------------------------------------------------- monte carlo, callbacks */

/* Return 0 to cancel, non-zero to continue. Called from the engine's own thread with
 * the caller's `user` pointer. Crossing back into managed code is expensive; the
 * engine calls this at most `report_every` path-blocks, not per path. */
typedef int32_t (PJ_CALL *pj_progress_fn)(int64_t done, int64_t total, void* user);

/* Antithetic-variate Monte Carlo European price. Deterministic for a given
 * (seed, paths): the generator is seeded per call from the engine seed, so repeated
 * calls on the same engine return bit-identical results. */
PJ_API pj_status PJ_CALL pj_price_monte_carlo(pj_engine* engine,
                                              const pj_option* opt,
                                              int64_t paths,
                                              int64_t report_every,
                                              pj_progress_fn progress,
                                              void* user,
                                              double* out_price);

/* ----------------------------------------------------------------- diagnostics  */

/* Returns, through out params, counters the host uses to prove that ownership rules
 * are being honoured: how many engines were created and destroyed process-wide.
 * A leak test is only meaningful if the native side is willing to be counted. */
PJ_API void PJ_CALL pj_engine_stats(int64_t* out_created, int64_t* out_destroyed);

/* Deliberately does nothing. Exists so the host can measure the cost of the boundary
 * itself, with zero work behind it. */
PJ_API void PJ_CALL pj_noop(void);

/* Same, but takes an option by pointer, so the host can measure the marginal cost of
 * passing a 48-byte struct across. Reads one field so the call cannot be elided. */
PJ_API double PJ_CALL pj_noop_option(const pj_option* opt);

/* Burns approximately `micros` microseconds inside native code without touching
 * managed memory. Used to demonstrate what SuppressGCTransition does to a runtime
 * that is trying to collect. */
PJ_API void PJ_CALL pj_burn(int32_t micros);

#ifdef __cplusplus
}
#endif

#endif /* CONTOSO_PRICING_ABI_H */

