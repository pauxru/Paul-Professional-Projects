"""Statistics for paired comparisons on a shared query set.

Three things are done here that a typical retrieval comparison omits, and each
of them changes the conclusion.

**Pairing.** Every configuration is run on the identical queries, so the
correct unit of analysis is the per-query difference. Comparing two independent
means throws away that structure and inflates the variance by roughly the
between-query variance, which in retrieval is far larger than the between-system
variance. Query difficulty ranges from trivial to impossible; system differences
are a couple of points.

**Family-wise error.** A grid of C configurations yields C(C-1)/2 comparisons.
At 5% each, a grid of 35 configurations produces about thirty spurious
"significant" results by construction. Holm's step-down procedure controls the
probability of *any* false claim across the family, and unlike Bonferroni it
costs almost nothing in power.

**Power.** The number a comparison cannot produce is the one worth knowing
first: given this many queries and this much variance, how large must a true
difference be before it can be seen at all? Everything below that threshold is
unmeasurable on this corpus, and reporting it as a result -- in either
direction -- is unsupported.

**Resolution.** A randomisation test cannot report a p-value below
1/(B+1), because the observed assignment is one of the B+1 outcomes being
counted. That floor is a property of the resampling budget, not of the data,
and it interacts badly with a family-wise correction: Holm's strictest
threshold is alpha/m, so once m > alpha*(B+1) no comparison in the family can
be significant no matter how large its effect. The failure is silent -- the
table reports zeros that look like a finding. `permutation_resolution` and
`iterations_for_holm` make the arithmetic explicit, and a parametric paired
test is provided for families large enough to hit the floor.

No scipy in this environment, so the normal quantile function is implemented
here (Acklam's rational approximation, refined by one Halley step), along with
the regularised incomplete beta function behind Student's t; both are pinned
by tests against known values.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np

_A = (-3.969683028665376e01, 2.209460984245205e02, -2.759285104469687e02,
      1.383577518672690e02, -3.066479806614716e01, 2.506628277459239e00)
_B = (-5.447609879822406e01, 1.615858368580409e02, -1.556989798598866e02,
      6.680131188771972e01, -1.328068155288572e01)
_C = (-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e00,
      -2.549732539343734e00, 4.374664141464968e00, 2.938163982698783e00)
_D = (7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e00,
      3.754408661907416e00)


def norm_cdf(x: float) -> float:
    return 0.5 * math.erfc(-x / math.sqrt(2.0))


def probit(p: float) -> float:
    """Inverse standard normal CDF.

    Acklam's approximation is good to about 1.15e-9 relative; the Halley
    refinement below takes it to machine precision, which matters only because
    the same function is used to report required sample sizes and a visibly
    wrong digit there undermines the whole section.
    """
    if not 0.0 < p < 1.0:
        raise ValueError(f"probit domain is (0,1), got {p}")
    plow, phigh = 0.02425, 1 - 0.02425
    if p < plow:
        q = math.sqrt(-2 * math.log(p))
        x = (((((_C[0] * q + _C[1]) * q + _C[2]) * q + _C[3]) * q + _C[4]) * q + _C[5]) / (
            (((_D[0] * q + _D[1]) * q + _D[2]) * q + _D[3]) * q + 1
        )
    elif p > phigh:
        q = math.sqrt(-2 * math.log(1 - p))
        x = -(((((_C[0] * q + _C[1]) * q + _C[2]) * q + _C[3]) * q + _C[4]) * q + _C[5]) / (
            (((_D[0] * q + _D[1]) * q + _D[2]) * q + _D[3]) * q + 1
        )
    else:
        q = p - 0.5
        r = q * q
        x = (((((_A[0] * r + _A[1]) * r + _A[2]) * r + _A[3]) * r + _A[4]) * r + _A[5]) * q / (
            ((((_B[0] * r + _B[1]) * r + _B[2]) * r + _B[3]) * r + _B[4]) * r + 1
        )
    e = norm_cdf(x) - p
    u = e * math.sqrt(2 * math.pi) * math.exp(x * x / 2)
    return x - u / (1 + x * u / 2)


@dataclass(frozen=True)
class PairedResult:
    mean_diff: float
    ci_low: float
    ci_high: float
    p_value: float
    n: int
    sd_diff: float

    @property
    def significant_uncorrected(self) -> bool:
        return self.p_value < 0.05

    @property
    def ci_excludes_zero(self) -> bool:
        return self.ci_low > 0.0 or self.ci_high < 0.0


def paired_bootstrap(
    a: np.ndarray, b: np.ndarray, *, iterations: int = 10000, seed: int = 12345,
    alpha: float = 0.05,
) -> tuple[float, float, float]:
    """Percentile confidence interval for the mean paired difference a - b."""
    d = np.asarray(a, dtype=np.float64) - np.asarray(b, dtype=np.float64)
    n = len(d)
    if n == 0:
        return 0.0, 0.0, 0.0
    rng = np.random.default_rng(seed)
    idx = rng.integers(0, n, size=(iterations, n))
    means = d[idx].mean(axis=1)
    lo = float(np.quantile(means, alpha / 2))
    hi = float(np.quantile(means, 1 - alpha / 2))
    return float(d.mean()), lo, hi


def paired_permutation(
    a: np.ndarray, b: np.ndarray, *, iterations: int = 10000, seed: int = 12345
) -> float:
    """Two-sided p-value by randomisation of the sign of each difference.

    The exact test under the null that the two systems are exchangeable on
    every query. Makes no distributional assumption, which matters because
    per-query nDCG differences are mostly exactly zero with a heavy tail --
    about as far from normal as a bounded variable gets.
    """
    d = np.asarray(a, dtype=np.float64) - np.asarray(b, dtype=np.float64)
    n = len(d)
    if n == 0:
        return 1.0
    observed = abs(float(d.mean()))
    if observed == 0.0:
        return 1.0
    rng = np.random.default_rng(seed)
    signs = rng.choice(np.array([-1.0, 1.0]), size=(iterations, n))
    means = np.abs((d * signs).mean(axis=1))
    # +1 in numerator and denominator: the observed assignment is itself one of
    # the permutations, and omitting it can report p = 0, which is never true.
    return float((np.sum(means >= observed - 1e-12) + 1) / (iterations + 1))


def compare(
    a: np.ndarray, b: np.ndarray, *, iterations: int = 10000, seed: int = 12345
) -> PairedResult:
    mean, lo, hi = paired_bootstrap(a, b, iterations=iterations, seed=seed)
    p = paired_permutation(a, b, iterations=iterations, seed=seed + 1)
    d = np.asarray(a, dtype=np.float64) - np.asarray(b, dtype=np.float64)
    sd = float(d.std(ddof=1)) if len(d) > 1 else 0.0
    return PairedResult(mean, lo, hi, p, len(d), sd)


def _betacf(a: float, b: float, x: float, itmax: int = 400, eps: float = 3e-16) -> float:
    """Continued-fraction expansion for the incomplete beta (Lentz's method)."""
    tiny = 1e-300
    qab, qap, qam = a + b, a + 1.0, a - 1.0
    c = 1.0
    d = 1.0 - qab * x / qap
    if abs(d) < tiny:
        d = tiny
    d = 1.0 / d
    h = d
    for m in range(1, itmax + 1):
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
        if abs(delta - 1.0) < eps:
            return h
    raise RuntimeError("incomplete beta did not converge")


def betai(a: float, b: float, x: float) -> float:
    """Regularised incomplete beta function I_x(a, b)."""
    if x <= 0.0:
        return 0.0
    if x >= 1.0:
        return 1.0
    front = math.exp(
        math.lgamma(a + b) - math.lgamma(a) - math.lgamma(b)
        + a * math.log(x) + b * math.log1p(-x)
    )
    if x < (a + 1.0) / (a + b + 2.0):
        return front * _betacf(a, b, x) / a
    return 1.0 - front * _betacf(b, a, 1.0 - x) / b


def student_t_two_sided(t: float, df: int) -> float:
    """Two-sided tail probability of Student's t with df degrees of freedom."""
    if df <= 0:
        return 1.0
    if math.isinf(t):
        return 0.0
    return betai(df / 2.0, 0.5, df / (df + t * t))


def paired_t(a: np.ndarray, b: np.ndarray) -> float:
    """Two-sided p-value from Student's t on the per-query differences.

    Weaker than the permutation test -- it assumes the mean difference is
    normally distributed, which for a bounded, mostly-zero variable is only
    true by appeal to the central limit theorem -- but its p-values are
    continuous. That is the reason it is here: a randomisation test cannot
    resolve below 1/(B+1), and a family-wise correction over hundreds of
    comparisons demands thresholds far below any affordable B.
    """
    d = np.asarray(a, dtype=np.float64) - np.asarray(b, dtype=np.float64)
    n = len(d)
    if n < 2:
        return 1.0
    mean = float(d.mean())
    sd = float(d.std(ddof=1))
    if sd == 0.0:
        return 1.0 if mean == 0.0 else 0.0
    t = mean / (sd / math.sqrt(n))
    return student_t_two_sided(t, n - 1)


def permutation_resolution(iterations: int) -> float:
    """Smallest p-value a sign-flip permutation test with this budget can report."""
    return 1.0 / (iterations + 1)


def iterations_for_holm(m: int, alpha: float = 0.05) -> int:
    """Resamples needed before Holm significance is arithmetically possible.

    Holm's strictest threshold over a family of m comparisons is alpha/m. A
    randomisation test can only clear it if 1/(B+1) < alpha/m.
    """
    if m <= 0:
        return 0
    return int(math.ceil(m / alpha))


def holm(p_values: list[float]) -> list[float]:
    """Holm step-down adjusted p-values, in the input order.

    Adjusted values are enforced monotone: without that, a comparison can end
    up with a smaller adjusted p-value than one with a smaller raw p-value,
    which is indefensible when the table is sorted.
    """
    m = len(p_values)
    if m == 0:
        return []
    order = sorted(range(m), key=lambda i: p_values[i])
    adjusted = [0.0] * m
    running = 0.0
    for rank, i in enumerate(order):
        val = (m - rank) * p_values[i]
        running = max(running, val)
        adjusted[i] = min(1.0, running)
    return adjusted


def unpaired_naive(a: np.ndarray, b: np.ndarray) -> float:
    """Welch's t-test, ignoring the pairing. Included to be argued against.

    This is what a comparison of two reported averages amounts to. The report
    runs the identical grid through both this and the paired test so the cost
    of discarding the pairing is a measured number rather than an assertion.
    """
    a = np.asarray(a, dtype=np.float64)
    b = np.asarray(b, dtype=np.float64)
    na, nb = len(a), len(b)
    if na < 2 or nb < 2:
        return 1.0
    va, vb = a.var(ddof=1), b.var(ddof=1)
    se = math.sqrt(va / na + vb / nb)
    if se == 0.0:
        return 1.0
    t = (a.mean() - b.mean()) / se
    # Normal approximation to the t distribution. At n > 100 per group the
    # difference in the tail is in the fourth decimal and is irrelevant to the
    # point being made, which is about the pairing, not the reference
    # distribution.
    return 2.0 * (1.0 - norm_cdf(abs(t)))


def minimum_detectable_effect(
    sd_diff: float, n: int, *, alpha: float = 0.05, power: float = 0.8
) -> float:
    """Smallest true paired difference detectable at the given power.

    delta = (z_{1-alpha/2} + z_{power}) * sd / sqrt(n)

    Anything smaller than this is not a result that this corpus can produce,
    whichever way the observed difference happens to point.
    """
    if n <= 0 or sd_diff <= 0.0:
        return 0.0
    return (probit(1 - alpha / 2) + probit(power)) * sd_diff / math.sqrt(n)


def required_queries(
    delta: float, sd_diff: float, *, alpha: float = 0.05, power: float = 0.8
) -> int:
    if delta <= 0.0 or sd_diff <= 0.0:
        return 0
    z = probit(1 - alpha / 2) + probit(power)
    return int(math.ceil((z * sd_diff / delta) ** 2))


def mean_ci(
    x: np.ndarray, *, iterations: int = 10000, seed: int = 999, alpha: float = 0.05
) -> tuple[float, float, float]:
    x = np.asarray(x, dtype=np.float64)
    n = len(x)
    if n == 0:
        return 0.0, 0.0, 0.0
    rng = np.random.default_rng(seed)
    idx = rng.integers(0, n, size=(iterations, n))
    means = x[idx].mean(axis=1)
    return (
        float(x.mean()),
        float(np.quantile(means, alpha / 2)),
        float(np.quantile(means, 1 - alpha / 2)),
    )
