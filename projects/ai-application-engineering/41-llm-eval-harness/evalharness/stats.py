"""Statistical machinery for comparing two systems on the same eval set.

Everything here is implemented from primitives rather than delegated to SciPy,
for two reasons. The obvious one is that SciPy is not available in the build
environment. The better one is that the entire argument of this project is that
teams reach for a statistic without knowing what it assumes, and a module that
just forwards to `scipy.stats.ttest_rel` would be making the same mistake it is
complaining about.

The functions here divide into three groups:

* **Estimation** -- bootstrap and BCa intervals for the difference between two
  systems, always *paired*, because eval runs share items and pairing is worth
  a large factor in sample size.
* **Testing** -- an exact-ish permutation test, plus Benjamini-Hochberg for the
  case everybody hits and nobody corrects for: sweeping twenty prompt variants
  and reporting the best one.
* **Design** -- power, minimum detectable effect, and the Type M / Type S error
  rates that say what a *significant* result from an underpowered eval is
  actually worth. This last group is the one that changes behaviour.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np

# ---------------------------------------------------------------------------
# Normal distribution helpers
# ---------------------------------------------------------------------------


def norm_cdf(z: float) -> float:
    """Standard normal CDF, via the error function in the standard library."""
    return 0.5 * (1.0 + math.erf(z / math.sqrt(2.0)))


# Coefficients from Peter Acklam's rational approximation to the inverse normal
# CDF, refined by one step of Halley's method. Accurate to about 1e-15, which
# is far more than anything here needs, but the refinement is three lines and
# removes any doubt about the tails -- and the tails are where the interesting
# quantiles live.
_A = [-3.969683028665376e01, 2.209460984245205e02, -2.759285104469687e02,
      1.383577518672690e02, -3.066479806614716e01, 2.506628277459239e00]
_B = [-5.447609879822406e01, 1.615858368580409e02, -1.556989798598866e02,
      6.680131188771972e01, -1.328068155288572e01]
_C = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e00,
      -2.549732539343734e00, 4.374664141464968e00, 2.938163982698783e00]
_D = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e00,
      3.754408661907416e00]


def norm_ppf(p: float) -> float:
    """Inverse standard normal CDF."""
    if not 0.0 < p < 1.0:
        raise ValueError(f"norm_ppf requires 0 < p < 1, got {p}")
    plow, phigh = 0.02425, 1 - 0.02425
    if p < plow:
        q = math.sqrt(-2 * math.log(p))
        x = (((((_C[0] * q + _C[1]) * q + _C[2]) * q + _C[3]) * q + _C[4]) * q + _C[5]) / \
            ((((_D[0] * q + _D[1]) * q + _D[2]) * q + _D[3]) * q + 1)
    elif p > phigh:
        q = math.sqrt(-2 * math.log(1 - p))
        x = -(((((_C[0] * q + _C[1]) * q + _C[2]) * q + _C[3]) * q + _C[4]) * q + _C[5]) / \
            ((((_D[0] * q + _D[1]) * q + _D[2]) * q + _D[3]) * q + 1)
    else:
        q = p - 0.5
        r = q * q
        x = (((((_A[0] * r + _A[1]) * r + _A[2]) * r + _A[3]) * r + _A[4]) * r + _A[5]) * q / \
            (((((_B[0] * r + _B[1]) * r + _B[2]) * r + _B[3]) * r + _B[4]) * r + 1)
    # One Halley step.
    e = norm_cdf(x) - p
    u = e * math.sqrt(2 * math.pi) * math.exp(x * x / 2)
    return x - u / (1 + x * u / 2)


# ---------------------------------------------------------------------------
# Student's t
# ---------------------------------------------------------------------------


def _betacf(a: float, b: float, x: float) -> float:
    """Continued fraction for the incomplete beta function (Lentz's method)."""
    tiny = 1e-300
    qab, qap, qam = a + b, a + 1.0, a - 1.0
    c = 1.0
    d = 1.0 - qab * x / qap
    if abs(d) < tiny:
        d = tiny
    d = 1.0 / d
    h = d
    for m in range(1, 300):
        m2 = 2 * m
        aa = m * (b - m) * x / ((qam + m2) * (a + m2))
        d = 1.0 + aa * d
        if abs(d) < tiny:
            d = tiny
        c = 1.0 + aa / c
        if abs(c) < tiny:
            c = tiny
        d = 1.0 / d
        h *= d * c
        aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2))
        d = 1.0 + aa * d
        if abs(d) < tiny:
            d = tiny
        c = 1.0 + aa / c
        if abs(c) < tiny:
            c = tiny
        d = 1.0 / d
        delta = d * c
        h *= delta
        if abs(delta - 1.0) < 3e-16:
            break
    return h


def betainc(a: float, b: float, x: float) -> float:
    """Regularised incomplete beta function I_x(a, b)."""
    if x <= 0.0:
        return 0.0
    if x >= 1.0:
        return 1.0
    front = math.exp(math.lgamma(a + b) - math.lgamma(a) - math.lgamma(b)
                     + a * math.log(x) + b * math.log1p(-x))
    if x < (a + 1.0) / (a + b + 2.0):
        return front * _betacf(a, b, x) / a
    return 1.0 - math.exp(math.lgamma(a + b) - math.lgamma(a) - math.lgamma(b)
                          + b * math.log1p(-x) + a * math.log(x)) * _betacf(b, a, 1.0 - x) / b


def student_t_cdf(t: float, df: float) -> float:
    """CDF of Student's t with `df` degrees of freedom."""
    if df <= 0:
        raise ValueError("df must be positive")
    x = df / (df + t * t)
    p = 0.5 * betainc(df / 2.0, 0.5, x)
    return 1.0 - p if t > 0 else p


def student_t_ppf(p: float, df: float) -> float:
    """Inverse CDF of Student's t, by bisection on the CDF.

    Present because a fair comparison of interval procedures has to include
    the t-interval, not just the z-interval. Using z at n=20 and calling the
    resulting under-coverage a failure of "normal theory" would be attacking a
    straw man: the failure would be the analyst's, not the method's. The
    distinction matters for the claim in section 13 of the report, which is
    about the *shape* of the score distribution rather than about small-sample
    corrections that have been understood since 1908.
    """
    if not 0.0 < p < 1.0:
        raise ValueError(f"student_t_ppf requires 0 < p < 1, got {p}")
    if df > 1e7:
        return norm_ppf(p)
    lo, hi = -1e3, 1e3
    for _ in range(200):
        mid = 0.5 * (lo + hi)
        if student_t_cdf(mid, df) < p:
            lo = mid
        else:
            hi = mid
    return 0.5 * (lo + hi)


# ---------------------------------------------------------------------------
# Paired comparison
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Interval:
    """A point estimate with a confidence interval and the method that made it."""

    point: float
    low: float
    high: float
    level: float
    method: str

    @property
    def excludes_zero(self) -> bool:
        """Whether the interval supports a directional claim.

        This is the only question most eval reports need to ask, and the one
        they usually skip in favour of comparing two point estimates.
        """
        return self.low > 0.0 or self.high < 0.0

    @property
    def width(self) -> float:
        return self.high - self.low

    def __str__(self) -> str:
        return (f"{self.point:+.4f} [{self.low:+.4f}, {self.high:+.4f}] "
                f"({self.level:.0%} {self.method})")


def sign_test(successes: int, trials: int) -> float:
    """Exact two-sided p-value for a fair-coin sign test.

    Computed in log space via lgamma rather than with math.comb, because the
    binomial coefficients here reach magnitudes around 2**4000 and the
    intermediate integers, while exact, are slow and pointless -- every one of
    them is immediately divided by a number of the same size.
    """
    if trials < 0:
        raise ValueError("trials must be non-negative")
    if not 0 <= successes <= trials:
        raise ValueError(f"successes {successes} out of range for {trials} trials")
    if trials == 0:
        return 1.0

    k = min(successes, trials - successes)
    log_half_n = -trials * math.log(2.0)
    tail = 0.0
    for i in range(k + 1):
        log_c = (math.lgamma(trials + 1)
                 - math.lgamma(i + 1)
                 - math.lgamma(trials - i + 1))
        tail += math.exp(log_c + log_half_n)
    return min(1.0, 2.0 * tail)


def paired_bootstrap(
    baseline: np.ndarray,
    candidate: np.ndarray,
    *,
    level: float = 0.95,
    resamples: int = 10_000,
    seed: int = 0,
) -> Interval:
    """Percentile bootstrap CI for the mean paired difference.

    Resampling *items*, not scores, is the whole point. Both systems are
    evaluated on the same eval set, so item difficulty is a shared nuisance
    term: a question that is hard for the baseline is usually hard for the
    candidate too. Resampling the paired differences cancels it. Treating the
    two score vectors as independent samples throws that away and inflates the
    interval by a factor that depends on how correlated the systems are --
    which, for two variants of the same prompt, is a great deal.
    """
    _check_paired(baseline, candidate)
    diff = candidate - baseline
    n = diff.size
    rng = np.random.default_rng(seed)
    idx = rng.integers(0, n, size=(resamples, n))
    stats = diff[idx].mean(axis=1)
    alpha = 1.0 - level
    low, high = np.quantile(stats, [alpha / 2, 1 - alpha / 2])
    return Interval(float(diff.mean()), float(low), float(high), level, "percentile bootstrap")


def paired_bca_bootstrap(
    baseline: np.ndarray,
    candidate: np.ndarray,
    *,
    level: float = 0.95,
    resamples: int = 10_000,
    seed: int = 0,
) -> Interval:
    """Bias-corrected and accelerated bootstrap CI for the mean paired difference.

    The percentile bootstrap assumes the bootstrap distribution is centred and
    symmetric about the estimate. For a mean over a bounded, skewed score
    distribution -- which is exactly what a 0-to-1 quality score is, especially
    when most items score 1.0 -- it is neither, and the percentile interval is
    shifted.

    BCa corrects for median bias (z0) and for how the estimate's variance
    changes with the underlying value (the acceleration term, from jackknife
    skewness). It matters most in the tail, which is where a gate decision
    sits.
    """
    _check_paired(baseline, candidate)
    diff = candidate - baseline
    n = diff.size
    theta = float(diff.mean())

    rng = np.random.default_rng(seed)
    idx = rng.integers(0, n, size=(resamples, n))
    boot = diff[idx].mean(axis=1)

    # Degeneracy check. The obvious version of this -- "did every resample land
    # on one side of the estimate?" -- is almost unreachable with real float
    # data: `b - a` for a constant offset is constant only to within rounding,
    # so a bootstrap distribution with a spread of 1e-17 still splits either
    # side of the mean and the guard never fires. What follows then is z0
    # estimated from noise in the last bits, an acceleration term whose
    # denominator is the cube of that noise, and an `adj` that can be
    # arbitrarily large. It happens to produce the right answer here only
    # because every quantile of a degenerate distribution is the same number.
    #
    # The guard is therefore on the *scale* of the spread rather than on a
    # strict inequality.
    scale = max(float(np.abs(diff).max()), 1.0)
    if float(boot.std()) <= 1e-12 * scale:
        return paired_bootstrap(baseline, candidate, level=level,
                                resamples=resamples, seed=seed)

    # Bias correction: where the point estimate falls in the bootstrap
    # distribution. Symmetric and unbiased puts it at the median, z0 == 0.
    prop = float(np.mean(boot < theta))
    if prop <= 0.0 or prop >= 1.0:
        return paired_bootstrap(baseline, candidate, level=level,
                                resamples=resamples, seed=seed)
    z0 = norm_ppf(prop)

    # Acceleration from the jackknife.
    total = diff.sum()
    jack = (total - diff) / (n - 1)
    jack_mean = jack.mean()
    dev = jack_mean - jack
    denom = 6.0 * float(np.sum(dev ** 2) ** 1.5)
    a = 0.0 if denom == 0 else float(np.sum(dev ** 3) / denom)

    alpha = 1.0 - level
    out = []
    for q in (alpha / 2, 1 - alpha / 2):
        z = norm_ppf(q)
        # The BCa adjustment has a pole at a*(z0+z) == 1. It is not reachable
        # for well-behaved data and is very reachable for data that is nearly
        # constant, where `a` is estimated from numerical noise. Falling back
        # is the only honest option: there is no acceleration estimate to use.
        shift = 1 - a * (z0 + z)
        if abs(shift) < 1e-9:
            return paired_bootstrap(baseline, candidate, level=level,
                                    resamples=resamples, seed=seed)
        adj = z0 + (z0 + z) / shift
        out.append(float(np.quantile(boot, norm_cdf(adj))))
    return Interval(theta, out[0], out[1], level, "BCa bootstrap")


def paired_permutation_test(
    baseline: np.ndarray,
    candidate: np.ndarray,
    *,
    resamples: int = 10_000,
    seed: int = 0,
) -> float:
    """Two-sided p-value from a paired sign-flip permutation test.

    The null is that the two systems are exchangeable *within each item*, so
    the permutation flips the sign of each paired difference independently.
    This makes no distributional assumption at all, which matters because
    quality scores are bounded, discrete, and usually piled up at 1.0 -- the
    conditions under which a t-test's normality assumption is least defensible
    and most commonly ignored.

    The +1 in numerator and denominator is not a rounding convenience. Without
    it the minimum achievable p-value is 0, which claims more certainty than a
    finite number of resamples can support.
    """
    _check_paired(baseline, candidate)
    diff = candidate - baseline
    n = diff.size
    observed = abs(float(diff.mean()))
    rng = np.random.default_rng(seed)
    signs = rng.choice(np.array([-1.0, 1.0]), size=(resamples, n))
    stats = np.abs((signs * diff).mean(axis=1))
    return float((np.sum(stats >= observed) + 1) / (resamples + 1))


def _check_paired(a: np.ndarray, b: np.ndarray) -> None:
    if a.shape != b.shape:
        raise ValueError(f"paired comparison needs matching shapes, got {a.shape} and {b.shape}")
    if a.ndim != 1:
        raise ValueError(f"expected 1-D score vectors, got {a.ndim}-D")
    if a.size < 2:
        raise ValueError("need at least 2 items to compare")


# ---------------------------------------------------------------------------
# Multiple comparisons
# ---------------------------------------------------------------------------


def benjamini_hochberg(pvalues: list[float], *, fdr: float = 0.05) -> list[bool]:
    """Benjamini-Hochberg step-up procedure. Returns per-hypothesis rejections.

    The situation this exists for: a team sweeps twenty prompt variants against
    one baseline, finds that variant 14 is 'significantly' better at p = 0.04,
    and ships it. The finding is more likely to be noise than signal, and
    nothing in the report says so.

    The figure usually quoted for this is 1 - 0.95^20 = 64%. That figure is
    wrong for this situation, and measurably so -- section 5 of docs/results.md
    finds 41.8%, because all twenty variants are compared against the *same*
    baseline run and the tests are therefore positively correlated rather than
    independent. The correct conclusion is unchanged and the arithmetic behind
    it is not, which is worth knowing before quoting it in a design review.

    BH controls the expected *proportion* of rejections that are false, which
    is the right error rate for a screening sweep -- Bonferroni controls the
    probability of *any* false positive, which is the right rate when a single
    false claim is unacceptable, and is far too strict for choosing a prompt.

    Positive correlation between the tests is also the condition under which
    BH is known to remain valid without the Benjamini-Yekutieli penalty, so
    the dependence that broke the 64% figure happens to be the benign kind
    here. Under arbitrary dependence BH needs the log-harmonic correction.

    Returned in input order, because a caller who has to re-sort to interpret
    the result will eventually misalign it with the variant names.
    """
    if not pvalues:
        return []
    m = len(pvalues)
    order = sorted(range(m), key=lambda i: pvalues[i])
    rejected = [False] * m
    # Step up: find the largest rank whose p-value clears its threshold, then
    # reject everything at or below that rank. Rejecting only those that clear
    # their own threshold individually is the classic misimplementation, and it
    # is under-powered rather than wrong, so it is hard to notice.
    kmax = -1
    for rank, i in enumerate(order, start=1):
        if pvalues[i] <= fdr * rank / m:
            kmax = rank
    for rank, i in enumerate(order, start=1):
        if rank <= kmax:
            rejected[i] = True
    return rejected


# ---------------------------------------------------------------------------
# Design: how big does the eval set have to be?
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class PowerResult:
    """What an eval set of a given size can and cannot see.

    `type_m` and `type_s` are the two numbers that make an underpowered eval
    actively harmful rather than merely uninformative, and neither appears in
    any eval report the author has seen.
    """

    n: int
    effect: float
    sd: float
    alpha: float
    power: float
    type_m: float
    type_s: float

    def __str__(self) -> str:
        return (f"n={self.n} effect={self.effect:+.3f} power={self.power:.1%} "
                f"exaggeration={self.type_m:.2f}x sign-error={self.type_s:.2%}")


def power_paired(
    n: int,
    effect: float,
    sd: float,
    *,
    alpha: float = 0.05,
    resamples: int = 20_000,
    seed: int = 0,
) -> PowerResult:
    """Power, exaggeration ratio and sign-error rate for a paired comparison.

    Power is the familiar quantity: given a true effect of this size, how often
    would an eval of this size call it significant?

    The other two are the ones that change behaviour, and they are properties
    of the results that *did* reach significance:

    * **Type M (magnitude), the exaggeration ratio.** To clear the threshold in
      a small sample, an estimate has to be large. So the significant estimates
      are a biased subsample of all estimates -- systematically larger than the
      truth. At 20% power the exaggeration is roughly 2x, which means a team
      that correctly detects a real 3% improvement will report it as 6% and
      then fail to reproduce it.

    * **Type S (sign).** The proportion of significant results pointing the
      wrong way. At very low power this is not negligible, and it is the worst
      possible failure: a confident, statistically significant claim that the
      change helped, when it hurt.

    Both are computed by simulation rather than from a formula, because the
    formula versions require assuming normality and the simulation does not
    need to.
    """
    if n < 2:
        raise ValueError("need at least 2 items")
    if sd <= 0:
        raise ValueError("sd must be positive")
    rng = np.random.default_rng(seed)
    samples = rng.normal(effect, sd, size=(resamples, n))
    means = samples.mean(axis=1)
    ses = samples.std(axis=1, ddof=1) / math.sqrt(n)
    with np.errstate(divide="ignore", invalid="ignore"):
        z = np.abs(means) / ses
    crit = norm_ppf(1 - alpha / 2)
    sig = z >= crit

    power = float(sig.mean())
    if not sig.any():
        return PowerResult(n, effect, sd, alpha, 0.0, float("nan"), float("nan"))

    sig_means = means[sig]
    type_m = float(np.mean(np.abs(sig_means)) / abs(effect)) if effect != 0 else float("nan")
    type_s = float(np.mean(np.sign(sig_means) != np.sign(effect))) if effect != 0 else float("nan")
    return PowerResult(n, effect, sd, alpha, power, type_m, type_s)


def minimum_detectable_effect(
    n: int, sd: float, *, alpha: float = 0.05, power: float = 0.80
) -> float:
    """The smallest true effect an eval of size n can reliably detect.

    This is the number to put at the top of an eval report, because it bounds
    every claim the report can make. An eval set that cannot detect a 5%
    regression is not a safety net against 5% regressions, however many times
    it has run green.
    """
    if n < 2:
        raise ValueError("need at least 2 items")
    z_a = norm_ppf(1 - alpha / 2)
    z_b = norm_ppf(power)
    return (z_a + z_b) * sd / math.sqrt(n)


def required_n(
    effect: float, sd: float, *, alpha: float = 0.05, power: float = 0.80
) -> int:
    """How many eval items are needed to detect a given effect."""
    if effect == 0:
        raise ValueError("cannot size a study for a zero effect")
    z_a = norm_ppf(1 - alpha / 2)
    z_b = norm_ppf(power)
    return int(math.ceil(((z_a + z_b) * sd / abs(effect)) ** 2))
