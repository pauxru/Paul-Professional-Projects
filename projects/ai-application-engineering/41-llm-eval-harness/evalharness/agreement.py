"""Measuring whether an LLM judge can be trusted.

The standard pattern is: label a few hundred examples by hand, check that the
judge agrees with the humans "most of the time", declare the judge validated,
and never look again. Two things go wrong with that.

The first is that raw percent agreement is meaningless without knowing what
chance agreement would have been. If 95% of your eval items are "good", a judge
that says "good" unconditionally agrees with the humans 95% of the time while
carrying no information whatsoever.

The second is subtler and catches people who already know the first: Cohen's
kappa, the standard correction for chance agreement, has a well-documented
pathology. On a skewed dataset it can report near-zero for a judge with 90%+
raw agreement, because its chance-agreement term is estimated from the observed
marginals and those marginals are themselves extreme. This is the "kappa
paradox" (Feinstein & Cicchetti, 1990), and a team that hits it will usually
conclude their judge is broken and rebuild it, when the real problem is the
statistic.

This module reports kappa, the two indices that diagnose when kappa is
misleading, and Gwet's AC1, which is designed to be stable under exactly the
prevalence conditions that break kappa.
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from .stats import Interval, paired_bca_bootstrap, sign_test


@dataclass(frozen=True)
class Agreement:
    """Judge-human agreement, with enough detail to know which number to trust."""

    n: int
    observed: float
    """Raw proportion of items where judge and human gave the same label."""

    kappa: float
    """Cohen's kappa: agreement corrected for chance, from observed marginals."""

    ac1: float
    """Gwet's AC1: agreement corrected for chance, from a prevalence-robust estimate."""

    prevalence_index: float
    """|P(both positive) - P(both negative)|. High values destabilise kappa."""

    bias_index: float
    """|P(judge positive) - P(human positive)|. The judge's systematic lean."""

    judge_positive_rate: float
    human_positive_rate: float

    counts: tuple[int, int, int, int]
    """(a, b, c, d) = (both pass, judge-only pass, human-only pass, both fail)."""

    @property
    def paradoxical(self) -> bool:
        """Whether kappa is being suppressed by skew rather than by disagreement.

        Defined as the two chance-corrections disagreeing substantially while
        the data is skewed, rather than as kappa falling below a fixed
        Landis-Koch bucket. The bucket version is what the literature uses and
        it is arbitrary: a kappa of 0.41 and a kappa of 0.39 are the same
        situation, and only one of them trips a threshold. The gap between
        kappa and AC1 *is* the phenomenon -- they differ only in how they model
        chance agreement, so a large gap alongside a large prevalence index is
        a direct measurement of prevalence driving the difference.
        """
        return (self.observed >= 0.80
                and self.prevalence_index >= 0.40
                and (self.ac1 - self.kappa) >= 0.20)

    @property
    def baseline_agreement(self) -> float:
        """Agreement achieved by the best constant labeller.

        Always saying the majority label requires no judge, no API call and no
        prompt. On a dataset that is 92% passes it is right 92% of the time.
        """
        return max(self.human_positive_rate, 1.0 - self.human_positive_rate)

    @property
    def baseline_margin(self) -> float:
        """How much the judge beats the best constant labeller, in accuracy.

        This has an exact closed form that is worth seeing. Write the confusion
        matrix as (a, b, c, d) = (both pass, judge-only pass, human-only pass,
        both fail). The judge is right on a + d. If the majority human label is
        "pass", the constant labeller is right on a + c. The difference is

            margin = (d - c) / n

        which involves neither a nor b. Every item the human passed and the
        judge also passed contributes nothing, because the constant labeller
        got it too. The judge's entire value lives in the minority class.

        On a dataset that is 92% passes, 92% of the items cannot help the judge
        no matter how well it does on them, and it is those items that dominate
        the agreement percentage everyone reports.
        """
        a, b, c, d = self.counts
        if self.human_positive_rate >= 0.5:
            return (d - c) / self.n
        return (a - b) / self.n

    @property
    def baseline_margin_p(self) -> float:
        """Exact two-sided p-value for the margin, from a sign test.

        Because the margin reduces to a difference of two discordant counts,
        the items that contribute are exactly those two cells and each one is a
        single Bernoulli trial for "did the judge win or lose this item". Under
        the null that the judge is no better than the constant, each discordant
        item is a fair coin, so the exact distribution is binomial and no
        resampling is required.

        The bootstrap gets the same answer. It is just unnecessary, and it took
        writing the bootstrap first to see that.
        """
        a, b, c, d = self.counts
        wins, losses = (d, c) if self.human_positive_rate >= 0.5 else (a, b)
        return sign_test(wins, wins + losses)

    @property
    def informative(self) -> bool:
        """Whether the judge measurably beats saying the same thing every time.

        This is the check neither kappa nor AC1 performs, and the one that
        catches the failure that actually happens. Kappa collapses under
        prevalence, which is a known problem, and AC1 was designed to not
        collapse under prevalence -- so AC1 also does not collapse under
        *degeneracy*, and rates a judge that says yes to every single item at
        0.95 on skewed data. Both statistics are answering "how much better
        than random guessing is this?" when the question that decides whether
        to deploy a judge is "how much better than not having one?".

        A judge that fails this is not merely poorly measured. It is worse than
        useless: it costs money and latency to reproduce a constant.

        The significance requirement is not decoration. The first version of
        this property compared the two point estimates, and on a 92%-pass
        dataset it passed a judge whose margin was +0.004 with a 95% interval
        of [-0.008, +0.014] over four thousand items. Replacing one
        unreliable number with a second unreliable number is not a fix.
        """
        return self.baseline_margin > 0 and self.baseline_margin_p < 0.05

    @property
    def trustworthy(self) -> bool:
        """Whether the judge is good enough to gate a release on.

        Deliberately strict, and deliberately not just a kappa threshold. A
        judge with strong agreement but a large bias index is systematically
        generous or harsh; it will still rank two systems correctly, but every
        absolute score it produces is wrong, and absolute scores are what end
        up in the release notes.
        """
        if not self.informative:
            return False
        effective = self.ac1 if self.paradoxical else self.kappa
        return effective >= 0.60 and self.bias_index <= 0.15

    def verdict(self) -> str:
        if not self.informative:
            return (f"uninformative: {self.observed:.1%} agreement beats the "
                    f"{self.baseline_agreement:.1%} from always answering "
                    f"'{'pass' if self.human_positive_rate >= 0.5 else 'fail'}' "
                    f"by {self.baseline_margin:+.4f} (p={self.baseline_margin_p:.3f})")
        if self.trustworthy:
            base = "trustworthy"
        elif self.bias_index > 0.15:
            base = f"biased (judge {self.judge_positive_rate:.1%} vs human {self.human_positive_rate:.1%})"
        else:
            base = "not trustworthy"
        if self.paradoxical:
            base += f"; kappa {self.kappa:.3f} suppressed by prevalence, read AC1 {self.ac1:.3f}"
        return base

    def __str__(self) -> str:
        return (f"n={self.n} observed={self.observed:.3f} kappa={self.kappa:.3f} "
                f"AC1={self.ac1:.3f} PI={self.prevalence_index:.3f} "
                f"BI={self.bias_index:.3f} -- {self.verdict()}")


def agreement(judge: np.ndarray, human: np.ndarray) -> Agreement:
    """Compare a judge's binary labels against human labels on the same items.

    Both arrays are 0/1 over the same items in the same order.
    """
    judge = np.asarray(judge)
    human = np.asarray(human)
    if judge.shape != human.shape:
        raise ValueError(f"shape mismatch: {judge.shape} vs {human.shape}")
    if judge.ndim != 1:
        raise ValueError("expected 1-D label vectors")
    n = judge.size
    if n == 0:
        raise ValueError("no items to compare")
    if not set(np.unique(judge)) <= {0, 1} or not set(np.unique(human)) <= {0, 1}:
        raise ValueError("labels must be binary 0/1")

    a = int(np.sum((judge == 1) & (human == 1)))  # both positive
    b = int(np.sum((judge == 1) & (human == 0)))  # judge generous
    c = int(np.sum((judge == 0) & (human == 1)))  # judge harsh
    d = int(np.sum((judge == 0) & (human == 0)))  # both negative

    po = (a + d) / n

    # Cohen's kappa: chance agreement from the product of the marginals.
    pj, ph = (a + b) / n, (a + c) / n
    pe_cohen = pj * ph + (1 - pj) * (1 - ph)
    kappa = 0.0 if pe_cohen >= 1.0 else (po - pe_cohen) / (1 - pe_cohen)

    # Gwet's AC1: chance agreement from the average marginal prevalence,
    # which does not collapse when one class dominates. The 2*pi*(1-pi) form
    # is the binary case of the general AC1 definition.
    pi = (pj + ph) / 2
    pe_gwet = 2 * pi * (1 - pi)
    ac1 = 0.0 if pe_gwet >= 1.0 else (po - pe_gwet) / (1 - pe_gwet)

    return Agreement(
        n=n,
        observed=po,
        kappa=kappa,
        ac1=ac1,
        prevalence_index=abs(a - d) / n,
        bias_index=abs(b - c) / n,
        judge_positive_rate=pj,
        human_positive_rate=ph,
        counts=(a, b, c, d),
    )


@dataclass(frozen=True)
class Calibration:
    """How a judge's continuous scores map onto human judgement."""

    n: int
    mean_error: float
    """Signed. Positive means the judge scores higher than humans on average."""

    mean_abs_error: float
    correlation: float
    """Pearson correlation between judge and human scores."""

    rank_correlation: float
    """Spearman. The one that matters for A/B comparison."""

    @property
    def usable_for_ranking(self) -> bool:
        """Whether the judge can order two systems even if its absolute scores are off.

        This is the distinction that makes a slightly-wrong judge still useful.
        A judge that adds a constant 0.1 to every score is useless for
        reporting quality and perfectly fine for deciding which of two prompts
        is better, because the constant cancels in the difference. Ranking
        ability and calibration are separate properties and a single "judge
        accuracy" number conflates them.
        """
        return self.rank_correlation >= 0.70

    @property
    def usable_for_absolute_scores(self) -> bool:
        return self.usable_for_ranking and self.mean_abs_error <= 0.10

    def __str__(self) -> str:
        return (f"n={self.n} bias={self.mean_error:+.3f} MAE={self.mean_abs_error:.3f} "
                f"r={self.correlation:.3f} rho={self.rank_correlation:.3f}")


def baseline_margin(
    judge: np.ndarray,
    human: np.ndarray,
    *,
    level: float = 0.95,
    resamples: int = 10_000,
    seed: int = 0,
) -> Interval:
    """Confidence interval for how much the judge beats a constant labeller.

    Judge validation is a measurement, and its result is an estimate with
    uncertainty, and it is universally reported as a bare percentage. "Our
    judge agrees with humans 92% of the time" is a point estimate from a
    sample, usually a small one, of a quantity whose relevant comparison is
    not zero but the constant-labeller baseline.

    Pairing is on items -- the judge and the constant labeller are scored on
    the same items -- so the same paired machinery used for comparing two
    systems applies unchanged. That is not a coincidence. Validating a judge
    against a baseline and comparing two prompts against each other are the
    same statistical problem, and treating the first as a spreadsheet exercise
    while treating the second carefully is a common and expensive
    inconsistency.
    """
    judge = np.asarray(judge)
    human = np.asarray(human)
    if judge.shape != human.shape:
        raise ValueError(f"shape mismatch: {judge.shape} vs {human.shape}")
    if judge.ndim != 1:
        raise ValueError("expected 1-D label vectors")
    if judge.size < 2:
        raise ValueError("need at least 2 items")

    judge_correct = (judge.astype(int) == human.astype(int)).astype(float)
    constant = 1 if human.astype(int).mean() >= 0.5 else 0
    baseline_correct = (human.astype(int) == constant).astype(float)
    return paired_bca_bootstrap(baseline_correct, judge_correct,
                                level=level, resamples=resamples, seed=seed)


def calibration(judge_scores: np.ndarray, human_scores: np.ndarray) -> Calibration:
    judge_scores = np.asarray(judge_scores, dtype=float)
    human_scores = np.asarray(human_scores, dtype=float)
    if judge_scores.shape != human_scores.shape:
        raise ValueError("shape mismatch")
    if judge_scores.size < 2:
        raise ValueError("need at least 2 items")
    err = judge_scores - human_scores
    return Calibration(
        n=judge_scores.size,
        mean_error=float(err.mean()),
        mean_abs_error=float(np.abs(err).mean()),
        correlation=_pearson(judge_scores, human_scores),
        rank_correlation=_pearson(_rankdata(judge_scores), _rankdata(human_scores)),
    )


def _pearson(x: np.ndarray, y: np.ndarray) -> float:
    xc, yc = x - x.mean(), y - y.mean()
    denom = float(np.sqrt(np.sum(xc ** 2) * np.sum(yc ** 2)))
    # A constant vector has no correlation with anything, and reporting 0 is
    # more honest than propagating a NaN that a downstream comparison will
    # silently treat as False.
    return 0.0 if denom == 0 else float(np.sum(xc * yc) / denom)


def _rankdata(x: np.ndarray) -> np.ndarray:
    """Ranks with ties averaged.

    Ties are not an edge case here. LLM judges emit scores on coarse scales --
    1 to 5, or multiples of 0.1 -- so a hundred-item eval routinely has twenty
    items sharing a score. Assigning them arbitrary distinct ranks makes the
    rank correlation depend on the input ordering, which is how a "stable"
    metric turns out to change when someone sorts the dataset.
    """
    order = np.argsort(x, kind="stable")
    ranks = np.empty(x.size, dtype=float)
    i = 0
    while i < x.size:
        j = i
        while j + 1 < x.size and x[order[j + 1]] == x[order[i]]:
            j += 1
        avg = (i + j) / 2.0 + 1.0
        for k in range(i, j + 1):
            ranks[order[k]] = avg
        i = j + 1
    return ranks
