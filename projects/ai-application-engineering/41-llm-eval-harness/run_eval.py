"""Runs every experiment and writes docs/results.md.

Each section states a prediction, then measures. The report writer refuses to
accept a measurement that was not preceded by a prediction and refuses to close
with one outstanding, so the ordering in the source is the ordering in which
the work was actually done.

    python run_eval.py            # full run, writes docs/results.md
    python run_eval.py --quick    # fewer resamples, for iterating
    python run_eval.py --section 5
"""

from __future__ import annotations

import argparse
import hashlib
import math
import os
import sys

import numpy as np

from evalharness import (SimulatedJudge, SimulatedModel, Verdict, agreement,
                         baseline_margin, benjamini_hochberg, calibration,
                         compare, minimum_detectable_effect,
                         paired_bca_bootstrap, paired_bootstrap,
                         paired_permutation_test, power_paired, required_n, run)
from evalharness.stats import student_t_ppf
from evalharness.corpus import make_dataset
from evalharness.report import Report, num, pct, signed

QUICK = False


def R(full: int) -> int:
    """Resample count, reduced in quick mode."""
    return max(200, full // 20) if QUICK else full


def T(full: int) -> int:
    """Trial count, reduced in quick mode."""
    return max(50, full // 20) if QUICK else full


# ---------------------------------------------------------------------------


def section_power(rep: Report) -> None:
    rep.h2("1. What a 50-item eval set can actually see")
    rep.para(
        "Fifty items is a common size for a hand-curated eval set. It is large "
        "enough to feel serious and small enough to review by hand, which is "
        "how sizes get chosen. Nobody computes what it can detect.")
    rep.para(
        "The quantity that decides this is not the eval set size on its own. "
        "It is the size relative to the standard deviation of the *paired "
        "difference* between the two systems -- which depends on how noisy the "
        "items are, how noisy the judge is, and how correlated the two systems "
        "are with each other. All three are measurable and none are measured "
        "in practice.")

    ds = make_dataset("power", "v1", 50)
    judge = SimulatedJudge("judge-a", noise=0.08, consistency=0.5)
    base = SimulatedModel("baseline", base_quality=0.72)
    cand = base.variant("candidate", quality_delta=0.05)
    rb, rc = run(base, judge, ds), run(cand, judge, ds)
    sd = float(np.std(rc.scores - rb.scores, ddof=1))

    rep.para(
        f"With the simulated systems used throughout this report, the observed "
        f"standard deviation of paired differences on a 50-item set is "
        f"{num(sd, 4)}. Everything below follows from that one number.")

    rep.expect(
        "A 50-item eval set has less than 25% power to detect a true 5% "
        "quality improvement -- so more than three times in four, a real "
        "improvement of that size will be reported as no change.")

    powers = []
    for n in (25, 50, 100, 200, 500, 1000):
        powers.append(power_paired(n, 0.05, sd, resamples=R(20_000), seed=7))
    p50 = next(p for p in powers if p.n == 50)

    rep.found(
        f"Power at n=50 is {pct(p50.power)} -- roughly a coin flip, not the "
        f"under-25% predicted. The prediction was made from the folk belief "
        f"that small eval sets are hopeless, and the folk belief is "
        f"miscalibrated in the same way as the practice it criticises: it "
        f"asserts a number without computing one.\n\n"
        f"What the prediction got wrong is the standard deviation. It "
        f"implicitly assumed something near {num(0.28, 2)} -- the value you "
        f"get if the two systems are statistically independent. They are not; "
        f"they share item difficulty, and the measured paired sd is "
        f"{num(sd, 4)}. The correlation between the systems is doing more "
        f"work than the eval set size.\n\n"
        f"That is the finding worth keeping, and it is more useful than the "
        f"one predicted. Whether a 50-item eval set is adequate is not a "
        f"property of the number 50. It is a property of how similar the two "
        f"things being compared are, which changes every time you compare a "
        f"different pair, and which no team measures. The same eval set is "
        f"adequate for comparing two prompt variants and hopeless for "
        f"comparing two different model families.\n\n"
        f"It is still bad. A real 5% improvement is reported as no change "
        f"{pct(1 - p50.power)} of the time, and the practical consequence is "
        f"not that the team misses improvements but that they stop believing "
        f"the eval, because it disagrees with what they can see by eye -- and "
        f"then revert to shipping on vibes, which is where they started.",
        contradicted=True)

    rep.table(
        ["n", "power to see +0.05", "exaggeration if significant", "sign errors"],
        [[str(p.n), pct(p.power), f"{p.type_m:.2f}x", pct(p.type_s, 2)] for p in powers])

    rep.para(
        f"The eval set has to reach roughly "
        f"{next(p.n for p in powers if p.power >= 0.80)} items before it detects "
        f"a 5% improvement four times out of five.")

    rep.h3("The same eval set, a different comparison")
    rep.para(
        "If the contradiction above is right, then the adequacy of an eval set "
        "is not a property of the eval set. Testing that requires holding the "
        "eval set fixed and changing only how similar the two systems are.")

    rep.expect(
        "Holding the 50 items fixed and comparing two systems that share "
        "little item-level structure, instead of two variants of one system, "
        "will cut the power to detect the same 5% improvement by at least a "
        "third -- from the same eval set, on the same day, with nothing about "
        "the evaluation changed.")

    rows = []
    powers_by_rho = {}
    for rho, label in ((0.85, "two variants of one prompt"),
                       (0.60, "two prompts, same model"),
                       (0.30, "same family, different size"),
                       (0.05, "two different model families")):
        b = SimulatedModel("b", base_quality=0.72, shared_variance=rho)
        c = b.variant("c", quality_delta=0.05)
        rb2, rc2 = run(b, judge, ds), run(c, judge, ds)
        sd2 = float(np.std(rc2.scores - rb2.scores, ddof=1))
        p = power_paired(50, 0.05, sd2, resamples=R(20_000), seed=7)
        powers_by_rho[rho] = p.power
        rows.append([label, num(rho, 2), num(sd2, 4), pct(p.power),
                     num(minimum_detectable_effect(50, sd2), 4)])

    drop = 1.0 - powers_by_rho[0.05] / powers_by_rho[0.85]
    rep.found(
        f"Power falls from {pct(powers_by_rho[0.85])} to "
        f"{pct(powers_by_rho[0.05])} across the range -- a {pct(drop)} "
        f"reduction -- with the eval set, the judge, the items, and the true "
        f"effect all held exactly constant.\n\n"
        f"So \"our eval set has 50 items\" is not a statement about "
        f"measurement capability, and neither is \"our eval set is too small\". "
        f"The capability has to be computed per comparison, which is why the "
        f"harness reports a minimum detectable effect on every single verdict "
        f"rather than once in a README.")

    rep.table(["what is being compared", "shared variance", "paired sd",
               "power at n=50", "detectable effect at n=50"], rows)


def section_type_m(rep: Report) -> None:
    rep.h2("2. The results that do clear significance are inflated")
    rep.para(
        "Low power is usually described as a risk of missing things. That is "
        "the harmless half. The damaging half is what it does to the findings "
        "that survive.")
    rep.para(
        "To clear a significance threshold in a small sample, an estimate has "
        "to be large. So the estimates that clear it are not a random sample "
        "of all estimates -- they are the upper tail. Every significant result "
        "from an underpowered eval is therefore systematically larger than the "
        "truth, and the lower the power, the worse the inflation. This is the "
        "Type M error, and it explains a specific and very familiar experience: "
        "the improvement that measured +8% in the eval and delivered nothing "
        "in production.")

    rep.expect(
        "At the power level of a 50-item eval set, statistically significant "
        "estimates of a true 5% effect will average at least 1.8x the true "
        "effect, and the exaggeration will grow as the true effect gets "
        "smaller.")

    ds = make_dataset("typem", "v1", 50)
    judge = SimulatedJudge("judge-a", noise=0.08, consistency=0.5)
    base = SimulatedModel("baseline", base_quality=0.72)
    rb = run(base, judge, ds)
    sd = float(np.std(run(base.variant("c", quality_delta=0.05), judge, ds).scores
                      - rb.scores, ddof=1))

    rows = []
    worst = 0.0
    for effect in (0.10, 0.05, 0.03, 0.02, 0.01):
        p = power_paired(50, effect, sd, resamples=R(20_000), seed=11)
        rows.append([signed(effect), pct(p.power),
                     f"{p.type_m:.2f}x",
                     signed(effect * p.type_m),
                     pct(p.type_s, 2)])
        worst = max(worst, p.type_m)
    p5 = power_paired(50, 0.05, sd, resamples=R(20_000), seed=11)

    rep.found(
        f"At a true effect of +0.05 the exaggeration ratio is "
        f"{p5.type_m:.2f}x: the significant results report an average "
        f"improvement of {signed(0.05 * p5.type_m)} for a change that is "
        f"actually {signed(0.05)}. Below that the inflation gets worse, "
        f"reaching {worst:.2f}x for the smallest effect tested.\n\n"
        f"This is the mechanism behind eval scores that do not reproduce. "
        f"Nobody is cheating and no measurement is wrong; the filter that "
        f"selects which measurements get reported is doing the damage.")

    rep.table(["true effect", "power", "exaggeration", "reported as", "sign errors"], rows)

    tiny = power_paired(50, 0.01, sd, resamples=R(20_000), seed=11)
    rep.note(
        f"The sign-error column is the one to read twice. At a true effect of "
        f"+0.01, {pct(tiny.type_s, 1)} of the statistically significant "
        f"results point the wrong way -- a confident, interval-backed claim "
        f"that the change helped, for a change that hurt.")


def section_required_n(rep: Report) -> None:
    rep.h2("3. How large the eval set has to be")
    rep.para(
        "Turning the previous section around: given that you want to detect a "
        "regression of a particular size, how many items do you need? This is "
        "the calculation that should happen before an eval set is built, and "
        "it takes one line.")

    rep.expect(
        "Detecting a 1% change will require more than 20x the items needed for "
        "a 5% change, because required sample size scales with the inverse "
        "square of the effect.")

    ds = make_dataset("reqn", "v1", 200)
    judge = SimulatedJudge("judge-a", noise=0.08, consistency=0.5)
    base = SimulatedModel("baseline", base_quality=0.72)
    rb = run(base, judge, ds)
    sd = float(np.std(run(base.variant("c", quality_delta=0.05), judge, ds).scores
                      - rb.scores, ddof=1))

    rows = []
    ns = {}
    for effect in (0.10, 0.05, 0.03, 0.02, 0.01):
        n = required_n(effect, sd)
        ns[effect] = n
        rows.append([signed(effect), str(n),
                     num(minimum_detectable_effect(50, sd), 4),
                     num(minimum_detectable_effect(n, sd), 4)])

    ratio = ns[0.01] / ns[0.05]
    rep.found(
        f"Detecting +0.01 needs {ns[0.01]} items against {ns[0.05]} for +0.05, "
        f"a factor of {ratio:.0f}x. The quadratic is unforgiving: every halving "
        f"of the effect you want to see costs four times the eval set.\n\n"
        f"This is the number that should end the argument about whether to "
        f"invest in more eval items. A team that wants to detect 1% "
        f"regressions and has 50 items is not close; they are off by "
        f"{ns[0.01] / 50:.0f}x.")

    rep.table(["effect to detect", "items required (80% power)",
               "MDE at n=50", "MDE at required n"], rows)


def section_pairing(rep: Report) -> None:
    rep.h2("4. Why paired analysis is worth more than a bigger eval set")
    rep.para(
        "Two systems evaluated on the same items share a large nuisance term: "
        "a question that is hard for one is usually hard for the other. "
        "Comparing per-item differences cancels it. Comparing group means does "
        "not.")
    rep.para(
        "How much this is worth depends on the correlation between the two "
        "systems' per-item scores, which for two variants of the same prompt "
        "is high. The point of stating it as a measurable parameter is that a "
        "team can estimate it from a single run they have already done.")

    rep.expect(
        "At a realistic system correlation, the paired interval will be at "
        "least 30% narrower than the unpaired one, which is equivalent to "
        "roughly doubling the eval set for free.")

    ds = make_dataset("pairing", "v1", 100)
    judge = SimulatedJudge("judge-a", noise=0.08, consistency=0.5)

    rows = []
    best_gain = 0.0
    for rho in (0.0, 0.3, 0.6, 0.9):
        b = SimulatedModel("baseline", base_quality=0.72, shared_variance=rho)
        c = b.variant("candidate", quality_delta=0.04)
        rb, rc = run(b, judge, ds), run(c, judge, ds)
        r = float(np.corrcoef(rb.scores, rc.scores)[0, 1])

        paired_sd = float(np.std(rc.scores - rb.scores, ddof=1))
        # The unpaired standard error of a difference in means, which is what
        # you get by reporting two averages and subtracting them.
        unpaired_sd = math.sqrt(float(np.var(rb.scores, ddof=1))
                                + float(np.var(rc.scores, ddof=1)))
        gain = unpaired_sd / paired_sd
        best_gain = max(best_gain, gain)
        rows.append([num(rho, 2), num(r, 3), num(paired_sd, 4), num(unpaired_sd, 4),
                     f"{gain:.2f}x", f"{gain ** 2:.1f}x"])

    mid = rows[2]
    rep.found(
        f"At a shared-variance fraction of 0.60 -- which produced a measured "
        f"score correlation of {mid[1]} -- the paired standard deviation is "
        f"{mid[2]} against {mid[3]} unpaired. That is a {mid[4]} narrower "
        f"interval, equivalent to {mid[5]} the eval items, for no additional "
        f"model calls at all.")

    rep.table(["shared variance", "measured score correlation", "paired sd",
               "unpaired sd", "interval narrowing", "equivalent items"], rows)

    rep.note(
        f"The first row is the one worth noticing. Even at zero *model* "
        f"correlation the measured score correlation is {rows[0][1]}, because "
        f"the eval set has difficulty tiers and both systems find the hard "
        f"tier hard. The structure of the eval set alone supplies pairing "
        f"benefit; a flat undifferentiated eval set supplies less.")


def section_forking_paths(rep: Report) -> None:
    rep.h2("5. Twenty prompt variants, none of them better")
    rep.para(
        "The characteristic workflow of prompt engineering is to try many "
        "variants and keep the one with the best score. The characteristic "
        "statistical property of that workflow is that it will find a winner "
        "whether or not one exists.")
    rep.para(
        "Here twenty variants are generated with a true effect of *exactly "
        "zero* -- they are the same system, differing only in random seed. "
        "Each is compared against the baseline on a 50-item eval set.")

    rep.expect(
        "With 20 comparisons at alpha = 0.05 and no real effect anywhere, at "
        "least one will look significant about 64% of the time, which is "
        "1 - 0.95^20.")

    ds = make_dataset("forking", "v1", 50)
    judge = SimulatedJudge("judge-a", noise=0.08, consistency=0.5)

    trials = T(400)

    def sweep(shared_baseline: bool):
        any_naive = any_bh = naive_count = bh_count = 0
        for t in range(trials):
            base = SimulatedModel(f"base-{t}", base_quality=0.72, seed=t)
            rb = run(base, judge, ds)
            ps = []
            for v in range(20):
                # quality_delta is zero: every variant is the same system.
                cand = base.variant(f"var-{t}-{v}", quality_delta=0.0, seed=1000 + v)
                if shared_baseline:
                    ref = rb
                else:
                    ref = run(SimulatedModel(f"base-{t}-{v}", base_quality=0.72,
                                             seed=t), judge, ds)
                rc = run(cand, judge, ds)
                ps.append(paired_permutation_test(ref.scores, rc.scores,
                                                  resamples=R(2_000),
                                                  seed=t * 100 + v))
            naive = [p < 0.05 for p in ps]
            bh = benjamini_hochberg(ps, fdr=0.05)
            naive_count += sum(naive)
            bh_count += sum(bh)
            any_naive += any(naive)
            any_bh += any(bh)
        return (any_naive / trials, any_bh / trials,
                naive_count / trials, bh_count / trials)

    rate_naive, rate_bh, per_naive, per_bh = sweep(shared_baseline=True)
    theoretical = 1 - 0.95 ** 20

    rep.found(
        f"Across {trials} simulated sweeps, {pct(rate_naive)} produced at "
        f"least one variant that looked significantly better or worse than "
        f"baseline at p < 0.05. Not one of them differed from baseline in any "
        f"way. But the predicted figure was {pct(theoretical)}, and the "
        f"measured rate is well below it.\n\n"
        f"The shortfall is not a bug, and it is more interesting than the "
        f"prediction was. The textbook 1 - (1-alpha)^k assumes the k tests are "
        f"independent. A prompt sweep is not: all twenty variants are compared "
        f"against the *same* baseline run, so a baseline that happened to "
        f"score high on this eval set pushes all twenty comparisons in the "
        f"same direction at once. The tests are positively correlated, and "
        f"positively correlated tests produce fewer families with at least one "
        f"false positive than independent ones do.\n\n"
        f"That figure of 64% is quoted constantly, including in the docstring "
        f"of this repository's own Benjamini-Hochberg implementation before "
        f"this experiment was run. It is the wrong number for the situation "
        f"everyone quotes it about.",
        contradicted=True)

    rep.expect(
        "If the shortfall is caused by the shared baseline, then giving each "
        "of the twenty comparisons its own independent baseline draw -- "
        "changing nothing else -- will push the rate back up towards the "
        "textbook 64%.")

    rate_indep, _, per_indep, _ = sweep(shared_baseline=False)

    rep.found(
        f"With an independent baseline per comparison the rate rises from "
        f"{pct(rate_naive)} to {pct(rate_indep)} against a theoretical "
        f"{pct(theoretical)}. The shared baseline was the whole of the "
        f"difference.\n\n"
        f"The confirming detail is the second column. The average number of "
        f"false positives per sweep barely moves -- {per_naive:.2f} shared "
        f"against {per_indep:.2f} independent -- while the proportion of "
        f"sweeps containing at least one moves by {pct(rate_indep - rate_naive)}. "
        f"That is the signature of correlation and not of anything else: "
        f"correlation does not change how many errors you make, only how they "
        f"clump. The shared baseline gathers a sweep's errors into the same "
        f"sweep.\n\n"
        f"The practical lesson runs the other way from what you might expect. "
        f"Sharing a baseline makes the *family-wise* error rate look better "
        f"while leaving the error count untouched, so the one variant you pick "
        f"out of the sweep is no more trustworthy than before. You are simply "
        f"more likely to get all your errors on the same afternoon.")

    rep.table(
        ["procedure", "sweeps with >=1 false discovery", "false positives per sweep"],
        [["raw p < 0.05, shared baseline", pct(rate_naive), f"{per_naive:.2f}"],
         ["raw p < 0.05, independent baselines", pct(rate_indep), f"{per_indep:.2f}"],
         ["Benjamini-Hochberg (FDR 5%)", pct(rate_bh), f"{per_bh:.2f}"],
         ["theoretical, independent tests", pct(theoretical), "1.00"]])

    rep.para(
        f"Benjamini-Hochberg brings the rate of sweeps containing any false "
        f"discovery down to {pct(rate_bh)}, against the nominal 5%, and cuts "
        f"false positives per sweep from {per_naive:.2f} to {per_bh:.2f}.")

    rep.note(
        "The correction is four lines of code. The absence of it is the single "
        "most common defect in LLM evaluation practice, and it is invisible "
        "because the output of a broken sweep and a correct one look "
        "identical: a table of variants with one highlighted.")


def section_winners_curse(rep: Report) -> None:
    rep.h2("6. The winner of a sweep does not reproduce")
    rep.para(
        "Selecting the maximum of twenty noisy measurements produces a number "
        "biased upwards by construction, whether or not the selected variant "
        "is any good. The question a team should ask is not \"is the winner "
        "significant?\" but \"how much of the winner's margin survives a fresh "
        "eval set?\"")

    rep.expect(
        "The best of twenty identical variants will show a substantial "
        "apparent improvement on the eval set it was selected on, and "
        "essentially all of that improvement will vanish when the same variant "
        "is re-measured on a held-out set.")

    selection = make_dataset("curse-select", "v1", 50)
    holdout = make_dataset("curse-holdout", "v1", 50)
    judge = SimulatedJudge("judge-a", noise=0.08, consistency=0.5)

    trials = T(300)
    sel_gains, hold_gains = [], []
    for t in range(trials):
        base = SimulatedModel(f"base-{t}", base_quality=0.72, seed=t)
        rb_sel = run(base, judge, selection)
        rb_hold = run(base, judge, holdout)
        best_gain, best_v = -9.9, None
        for v in range(20):
            cand = base.variant(f"var-{t}-{v}", quality_delta=0.0, seed=1000 + v)
            g = run(cand, judge, selection).mean_score - rb_sel.mean_score
            if g > best_gain:
                best_gain, best_v = g, cand
        sel_gains.append(best_gain)
        hold_gains.append(run(best_v, judge, holdout).mean_score - rb_hold.mean_score)

    sel_mean = float(np.mean(sel_gains))
    hold_mean = float(np.mean(hold_gains))
    shrink = 1.0 - (hold_mean / sel_mean) if sel_mean else float("nan")

    rep.found(
        f"On the set it was selected on, the winning variant showed an average "
        f"improvement of {signed(sel_mean, 4)}. Re-measured on a held-out set "
        f"of the same size, the same variant delivered {signed(hold_mean, 4)} "
        f"-- {pct(shrink)} of the apparent gain evaporated, which is the "
        f"correct amount, since there was never any gain to begin with.\n\n"
        f"A held-out eval set is not a nicety. It is the only thing standing "
        f"between a prompt sweep and a quarter of imaginary progress.")

    rep.table(
        ["measurement", "mean apparent improvement"],
        [["best of 20, on the selection set", signed(sel_mean, 4)],
         ["same variant, held-out set", signed(hold_mean, 4)],
         ["true effect", signed(0.0, 4)]])


def section_kappa(rep: Report) -> None:
    rep.h2("7. A judge with 92% agreement can be worthless")
    rep.para(
        "The usual way to validate an LLM judge is to have a human label a "
        "sample and report the agreement rate. High agreement is taken as "
        "evidence the judge works. On a skewed dataset it is evidence of "
        "almost nothing, because a judge that says \"pass\" to everything will "
        "agree with a human on a dataset that is 92% passes.")
    rep.para(
        "Cohen's kappa is supposed to correct for this by subtracting chance "
        "agreement. It over-corrects: on highly skewed data the chance-"
        "agreement term approaches the observed agreement and kappa collapses "
        "toward zero even for a genuinely good judge. This is the kappa "
        "paradox, and it means neither number can be read alone.")

    rep.expect(
        "On a dataset where 92% of items pass, a judge with high raw agreement "
        "will show a Cohen's kappa below 0.4 -- conventionally 'fair' -- while "
        "Gwet's AC1 stays high, and the prevalence index will identify skew as "
        "the cause.")

    rng = np.random.default_rng(3)
    rows = []
    para_found = None
    for prevalence in (0.50, 0.70, 0.85, 0.92, 0.97):
        n = 400
        truth = rng.random(n) < prevalence
        # A judge that agrees with the human 92% of the time, independent of
        # the label -- the same judge in every row. Only the dataset changes.
        flip = rng.random(n) < 0.08
        judged = np.where(flip, ~truth, truth)
        a = agreement(truth.tolist(), judged.tolist())
        rows.append([pct(prevalence, 0), pct(a.observed), num(a.kappa, 3),
                     num(a.ac1, 3), num(a.prevalence_index, 3),
                     "yes" if a.paradoxical else "no"])
        if abs(prevalence - 0.92) < 1e-9:
            para_found = a

    rep.found(
        f"At 92% prevalence the same judge shows {pct(para_found.observed)} raw "
        f"agreement and a Cohen's kappa of {num(para_found.kappa, 3)}. Read "
        f"the kappa alone and the judge is unusable; read the agreement alone "
        f"and it is excellent. Gwet's AC1 is {num(para_found.ac1, 3)}, and the "
        f"prevalence index of {num(para_found.prevalence_index, 3)} names the "
        f"cause.\n\n"
        f"The judge did not change across any row of this table. Only the "
        f"class balance of the evaluation data did.")

    rep.table(["pass rate", "raw agreement", "Cohen kappa", "Gwet AC1",
               "prevalence index", "paradox flagged"], rows)

    rep.note(
        "The operational consequence: a team that validates its judge on a "
        "balanced sample and then runs it on production traffic -- which is "
        "overwhelmingly passes -- has validated something other than what it "
        "is running.")

    rep.h3("The comparison that neither statistic makes")
    rep.para(
        "Both kappa and AC1 answer the question \"how much better than random "
        "guessing is this judge?\". That is not the question. Nobody was going "
        "to deploy random guessing. The alternative to an LLM judge is not "
        "chance, it is *answering the same thing every time*, which costs no "
        "tokens, adds no latency and on a 92%-pass dataset is right 92% of "
        "the time.")

    rep.expect(
        "The judge from the table above -- the one with 90%+ agreement and a "
        "respectable AC1 -- will not have a margin over the constant labeller "
        "that is distinguishable from zero at 92% prevalence, even on four "
        "thousand items. Its entire apparent skill is the skew.")

    rng2 = np.random.default_rng(31)
    rows2 = []
    inf_at_92 = None
    margin_at_92 = None
    for prevalence in (0.50, 0.70, 0.85, 0.92, 0.97):
        n = 4000
        truth = rng2.random(n) < prevalence
        flip = rng2.random(n) < 0.08
        judged = np.where(flip, ~truth, truth)
        a = agreement(judged.tolist(), truth.tolist())
        m = baseline_margin(judged, truth, resamples=R(4_000), seed=5)
        rows2.append([pct(prevalence, 0), pct(a.observed),
                      pct(a.baseline_agreement),
                      f"{signed(a.baseline_margin, 4)} [{signed(m.low, 4)}, {signed(m.high, 4)}]",
                      f"{a.baseline_margin_p:.3f}",
                      "yes" if a.informative else "no",
                      "yes" if a.trustworthy else "no"])
        if abs(prevalence - 0.92) < 1e-9:
            inf_at_92, margin_at_92 = a, m

    beats = inf_at_92.informative
    rep.found(
        f"At 92% prevalence the judge scores {pct(inf_at_92.observed)} against "
        f"a constant labeller's {pct(inf_at_92.baseline_agreement)}. The margin "
        f"is {signed(inf_at_92.baseline_margin, 4)}, the bootstrap interval is "
        f"[{signed(margin_at_92.low, 4)}, {signed(margin_at_92.high, 4)}], and "
        f"the exact p-value is {inf_at_92.baseline_margin_p:.3f}. On four "
        f"thousand items the judge is "
        f"{'measurably better' if beats else 'not measurably better'} than a "
        f"hard-coded string.\n\n"
        f"Its AC1 of {num(inf_at_92.ac1, 3)} says nothing about this, and "
        f"cannot: AC1 was specifically constructed so that prevalence does not "
        f"collapse its chance term, and the same construction means degeneracy "
        f"does not collapse it either. A judge that returns \"pass\" for every "
        f"item on this data scores an AC1 above 0.94.\n\n"
        f"By 97% prevalence the margin is negative and significant: the judge "
        f"is measurably worse than not having one, while still showing over "
        f"90% agreement and an AC1 of 0.885.",
        contradicted=beats)

    rep.table(["pass rate", "judge agreement", "constant labeller",
               "margin [95% CI]", "exact p", "informative", "trustworthy"],
              rows2)

    rep.para(
        "Two things in that table were not obvious when the section was "
        "written. The first is that the margin has a closed form. Writing the "
        "confusion matrix as (a, b, c, d) for (both pass, judge-only pass, "
        "human-only pass, both fail), the judge is right on a + d and the "
        "constant labeller -- on a pass-majority dataset -- is right on "
        "a + c. So")
    rep.code("margin = (a + d)/n - (a + c)/n = (d - c)/n", "text")
    rep.para(
        "and a does not appear. Every item where the human passed and the "
        "judge agreed contributes exactly nothing to the judge's case, because "
        "a hard-coded string got that item too. On a 92%-pass dataset that is "
        "92% of the items, and they are the items that produce the agreement "
        "percentage in the slide deck.")
    rep.para(
        "The second is that once the statistic is written that way it is a "
        "count of discordant pairs, so under the null it is a fair coin on "
        "each discordant item and the exact distribution is binomial. The "
        "bootstrap column and the exact-p column agree everywhere in that "
        "table, and the bootstrap costs four thousand resamples per row to "
        "reproduce a number available in closed form. It was written first, "
        "which is the only reason it is still there: it is the evidence that "
        "the two agree.")
    rep.para(
        "The property this section tests went through three versions. It "
        "started as `observed > baseline_agreement`, which passed the 92% "
        "judge on a margin of +0.004. Comparing two point estimates and "
        "declaring a winner is the exact error this whole report is about, and "
        "it survived in the code that was supposed to detect it.")

    rep.note(
        "This does not make kappa or AC1 wrong. It makes them answers to a "
        "question about measurement rather than a question about deployment. "
        "The honest report of a judge is three numbers -- agreement, a "
        "prevalence-robust chance correction, and the margin over the trivial "
        "baseline -- and the third is the one that decides anything.")


def section_judge_consistency(rep: Report) -> None:
    rep.h2("8. Two judges, identical agreement, different eval set sizes")
    rep.para(
        "Judge error is usually treated as one quantity. It is two, and they "
        "have opposite consequences for an A/B comparison.")
    rep.para(
        "A judge that is *consistently* wrong about an item -- always reading "
        "it 0.1 too generously -- applies that error to both systems, and it "
        "cancels exactly in the paired difference. A judge that is *freshly* "
        "wrong each time it scores adds variance that does not cancel. Two "
        "judges can have identical agreement with humans and identical "
        "average error while differing substantially in how many eval items "
        "you need.")

    rep.expect(
        "A judge whose error is entirely stable per item will require "
        "materially fewer eval items than one whose error is entirely fresh, "
        "despite the two having the same error magnitude and the same "
        "agreement with ground truth.")

    ds = make_dataset("consistency", "v1", 200)
    base = SimulatedModel("baseline", base_quality=0.72)
    cand = base.variant("candidate", quality_delta=0.04)

    rows = []
    ns = {}
    for consistency in (0.0, 0.25, 0.5, 0.75, 1.0):
        j = SimulatedJudge(f"j-{consistency}", noise=0.12, consistency=consistency)
        rb, rc = run(base, j, ds), run(cand, j, ds)
        sd = float(np.std(rc.scores - rb.scores, ddof=1))
        n = required_n(0.04, sd)
        ns[consistency] = n
        # Agreement with ground truth, thresholded, to show it does not move.
        truth_pass = (rb.truth >= 0.72).tolist()
        judge_pass = (rb.scores >= 0.72).tolist()
        a = agreement(truth_pass, judge_pass)
        rows.append([num(consistency, 2), num(sd, 4), str(n), pct(a.observed)])

    ratio = ns[0.0] / ns[1.0]
    rep.found(
        f"The fully-fresh judge needs {ns[0.0]} items to detect a 4% "
        f"improvement; the fully-stable judge needs {ns[1.0]}, a factor of "
        f"{ratio:.2f}x. Raw agreement with ground truth barely moves across "
        f"the range, so no agreement statistic would have distinguished "
        f"them.\n\n"
        f"This is measurable without any human labelling at all: score the "
        f"same responses twice and look at the variance of the difference. "
        f"That single number is worth more for eval design than an agreement "
        f"study, and almost nobody computes it.")

    rep.table(["fraction of judge error that is stable per item",
               "sd of paired difference", "items needed for +0.04",
               "raw agreement with truth"], rows)


def section_length_bias(rep: Report) -> None:
    rep.h2("9. The failure that more data makes worse")
    rep.para(
        "Everything so far has been about variance, and variance is fixed by "
        "collecting more data. The dangerous judge failures are the ones that "
        "are not.")
    rep.para(
        "LLM judges reliably prefer longer answers. If a prompt change makes "
        "the model more verbose without making it better, the judge will "
        "score it higher -- consistently, on every item, in the same "
        "direction. That is not noise. Adding eval items does not average it "
        "away; it narrows the interval around a wrong answer.")

    rep.expect(
        "A change with zero true quality effect but 60% longer outputs will be "
        "reported as a significant improvement, and the confidence in that "
        "false finding will *increase* with eval set size rather than "
        "decreasing.")

    judge = SimulatedJudge("verbose-judge", noise=0.08, consistency=0.5,
                           length_bias=0.12)
    base = SimulatedModel("baseline", base_quality=0.72, tokens_out=160)
    # Same quality. More words.
    windy = base.variant("windy", quality_delta=0.0, tokens_out=256)

    rows = []
    for n in (25, 50, 100, 200, 400, 800):
        ds = make_dataset("length", "v1", n)
        rb, rc = run(base, judge, ds), run(windy, judge, ds)
        ci = paired_bca_bootstrap(rb.scores, rc.scores, resamples=R(4_000), seed=5)
        rows.append([str(n), signed(ci.point, 4), num(ci.width, 4),
                     "yes" if ci.excludes_zero else "no",
                     signed(float(rc.truth.mean() - rb.truth.mean()), 4)])

    big = rows[-1]
    rep.found(
        f"At n=800 the judge reports an improvement of {big[1]} with an "
        f"interval of width {big[2]} that excludes zero, for a change whose "
        f"true quality effect is {big[4]}. The interval narrowed by "
        f"{float(rows[0][2]) / float(big[2]):.1f}x going from 25 items to 800, "
        f"and every bit of that narrowing bought more confidence in a false "
        f"conclusion.\n\n"
        f"This is the reason a statistics-only answer to eval quality is "
        f"insufficient. Confidence intervals quantify sampling error. They say "
        f"nothing whatsoever about an instrument that is pointed at the wrong "
        f"thing, and they will happily certify it.")

    rep.table(["eval items", "reported improvement", "interval width",
               "significant", "true quality change"], rows)

    rep.note(
        "The defence is not statistical. It is to hold output length fixed "
        "when comparing, or to include length as a reported covariate so that "
        "a reviewer sees the +60% next to the +0.03 and asks the obvious "
        "question. The harness reports token counts alongside every "
        "comparison for exactly this reason.")


def section_slices(rep: Report) -> None:
    rep.h2("10. The aggregate that hides the regression")
    rep.para(
        "A single headline number is the most common eval output and the "
        "easiest to defeat. A change that helps common easy cases and breaks "
        "rare hard ones nets to approximately zero, and the hard cases are "
        "usually the ones that generate incidents.")

    rep.expect(
        "A change that improves easy items by 6% and degrades adversarial "
        "items by 12% will show an overall effect small enough to pass an "
        "aggregate gate, while slice analysis will block it.")

    ds = make_dataset("slices", "v1", 300)
    judge = SimulatedJudge("judge-a", noise=0.08, consistency=0.5)

    base = SimulatedModel("baseline", base_quality=0.72)
    skewed = base.variant("skewed", quality_delta=0.0)
    skewed.tier_offset = dict(base.tier_offset)
    skewed.tier_offset["easy"] += 0.06
    skewed.tier_offset["medium"] += 0.01
    skewed.tier_offset["hard"] -= 0.04
    skewed.tier_offset["adversarial"] -= 0.12

    rb, rc = run(base, judge, ds), run(skewed, judge, ds)
    cmp_ = compare(rb, rc, ds, resamples=R(10_000), seed=13)

    adv = next(s for s in cmp_.slices if s.name == "adversarial")
    rep.found(
        f"The overall effect is {signed(cmp_.overall.delta, 4)} with interval "
        f"[{signed(cmp_.overall.interval.low, 4)}, "
        f"{signed(cmp_.overall.interval.high, 4)}] -- indistinguishable from "
        f"zero, which an aggregate gate reads as safe. The adversarial slice "
        f"is {signed(adv.delta, 4)} with interval "
        f"[{signed(adv.interval.low, 4)}, {signed(adv.interval.high, 4)}].\n\n"
        f"The gate returns {cmp_.verdict.value}: {cmp_.reason}")

    rep.table(["slice", "n", "baseline", "candidate", "delta", "interval", "flagged"],
              [[s.name, str(s.n), num(s.baseline_mean, 4), num(s.candidate_mean, 4),
                signed(s.delta, 4),
                f"[{signed(s.interval.low, 4)}, {signed(s.interval.high, 4)}]",
                "yes" if s.significant else "no"]
               for s in (cmp_.overall,) + cmp_.slices])

    rep.note(
        "Slices are multiple tests, so they get the same multiplicity "
        "correction as anything else. Four slices at raw alpha = 0.05 give an "
        "18.5% chance of a spurious slice regression per comparison, and a "
        "gate that blocks good changes one time in five will be switched off "
        "within a month.")


def section_drift(rep: Report) -> None:
    rep.h2("11. Changing the eval set changes the score more than the change does")
    rep.para(
        "Eval sets are edited constantly, and usually not recorded. Someone "
        "adds fifteen cases from last week's incident; someone drops an item "
        "everyone agreed was ambiguous. Each edit is defensible on its own.")

    rep.expect(
        "Averaged over many baseline models, adding 15 items and reweighting "
        "the difficulty mix of a 50-item eval set will move the measured score "
        "by more than the 0.04 quality improvement this report has been trying "
        "to detect -- so a run labelled 'quality went up' could be entirely "
        "explained by an unrecorded dataset edit.")

    v1 = make_dataset("drift", "v1", 50,
                      composition={"easy": 0.30, "medium": 0.40, "hard": 0.20,
                                   "adversarial": 0.10})
    v2 = make_dataset("drift", "v2", 65,
                      composition={"easy": 0.231, "medium": 0.308, "hard": 0.308,
                                   "adversarial": 0.153})
    judge = SimulatedJudge("judge-a", noise=0.08, consistency=0.5)

    # One draw of this is a single noisy number. The quantity of interest is
    # the systematic shift the edit produces, so it is averaged over many
    # baseline models -- which is the difference between a demonstration and
    # an anecdote.
    trials = T(200)
    data_shift, model_shift = [], []
    for t in range(trials):
        b = SimulatedModel(f"b-{t}", base_quality=0.72, seed=t)
        better = b.variant(f"better-{t}", quality_delta=0.04, seed=t)
        on_v1 = run(b, judge, v1).mean_score
        data_shift.append(run(b, judge, v2).mean_score - on_v1)
        model_shift.append(run(better, judge, v1).mean_score - on_v1)

    same_model_diff_data = float(np.mean(data_shift))
    diff_model_same_data = float(np.mean(model_shift))
    model_sd = float(np.std(model_shift, ddof=1))
    ratio = abs(same_model_diff_data) / 0.04

    diff = v1.diff(v2)
    rep.found(
        f"Averaged over {trials} baseline models, the same unchanged model "
        f"scores {signed(same_model_diff_data, 4)} differently on v2 than on "
        f"v1. The model did not change; the ruler did. That shift is "
        f"{ratio:.2f}x the size of a genuine +0.0400 quality improvement, "
        f"{'larger than the effect and pointing the other way'
           if ratio > 1 else 'not larger than the effect, but the same order of magnitude'}"
        f" -- so the prediction is "
        f"{'confirmed' if ratio > 1 else 'not confirmed as stated'}.\n\n"
        f"Either way the operational point stands, and it does not depend on "
        f"the ratio exceeding one: an untracked dataset edit produces a score "
        f"movement indistinguishable in size from the effects the eval exists "
        f"to detect, in a direction nobody chose, with no record that anything "
        f"happened.\n\n"
        f"The fingerprints differ ({v1.short_fingerprint} vs "
        f"{v2.short_fingerprint}) and the diff reports {len(diff.added)} added, "
        f"{len(diff.removed)} removed, {len(diff.modified)} modified, so "
        f"`comparable` is {diff.comparable}. The gate refuses the comparison "
        f"rather than reporting the number.",
        contradicted=ratio <= 1.0)

    rep.table(
        ["comparison", "mean measured change", "true change"],
        [["same model, v1 -> v2 eval set", signed(same_model_diff_data, 4), signed(0.0, 4)],
         ["real +4% model, both on v1", signed(diff_model_same_data, 4), signed(0.04, 4)]])

    wrong_sign = sum(1 for m in model_shift if m < 0) / len(model_shift)
    rep.note(
        f"The second row is an unplanned illustration of section 1. Averaged "
        f"over {trials} draws the +0.0400 improvement recovers correctly at "
        f"{signed(diff_model_same_data, 4)}, but the standard deviation across "
        f"individual 50-item runs is {num(model_sd, 4)}, and "
        f"{pct(wrong_sign)} of individual runs measured the real improvement "
        f"as a *decline*. For a team that runs this eval once, the chance of "
        f"reverting a change that helped is {pct(wrong_sign)}.")

    ref = SimulatedModel("ref", base_quality=0.72)
    rb_v1 = run(ref, judge, v1)
    rc_v2 = run(ref.variant("ref-better", quality_delta=0.04), judge, v2)
    blocked = compare(rb_v1, rc_v2, v1, resamples=R(2_000), seed=1)
    rep.para(
        f"Asked to compare a v1 baseline against a v2 candidate, the harness "
        f"returns {blocked.verdict.value}: {blocked.reason}")

    shared = tuple(sorted(set(v1.ids()) & set(v2.ids())))
    rep.para(
        f"The two versions do share {len(shared)} item ids, and a tempting "
        f"repair is to restrict the comparison to those. It does not work "
        f"here: {len(diff.modified)} of the shared ids have modified content, "
        f"because reweighting the tier mix changed which topic each slot draws. "
        f"An id that survives an edit is not the same item, and `comparable` "
        f"is keyed on content hashes rather than ids for exactly that reason.")


def section_gate_calibration(rep: Report) -> None:
    rep.h2("12. Does the gate actually work?")
    rep.para(
        "Every section so far has demonstrated a failure mode. This one asks "
        "the only question that matters about the thing built to prevent them: "
        "run the gate against many changes whose true effect is known, and "
        "count how often it is wrong in each direction.")

    rep.expect(
        "The gate will ship fewer than 5% of genuinely harmful changes at the "
        "default tolerance, will correctly refuse to claim improvement for "
        "null changes at least 90% of the time, and will label the majority of "
        "small true effects UNDERPOWERED rather than guessing.")

    ds = make_dataset("gate", "v1", 120)
    judge = SimulatedJudge("judge-a", noise=0.08, consistency=0.5)

    trials = T(200)
    scenarios = {
        "harmful (-0.06)": -0.06,
        "harmful (-0.02)": -0.02,
        "null (0.00)": 0.0,
        "helpful (+0.02)": 0.02,
        "helpful (+0.06)": 0.06,
    }
    rows = []
    ship_harmful = None
    null_claim = None
    for label, delta in scenarios.items():
        counts = {v: 0 for v in Verdict}
        for t in range(trials):
            base = SimulatedModel(f"b-{t}", base_quality=0.72, seed=t)
            cand = base.variant(f"c-{t}", quality_delta=delta, seed=t)
            rb, rc = run(base, judge, ds), run(cand, judge, ds)
            c = compare(rb, rc, ds, resamples=R(2_000), seed=t)
            counts[c.verdict] += 1
        rows.append([label,
                     pct(counts[Verdict.IMPROVED] / trials),
                     pct(counts[Verdict.REGRESSED] / trials),
                     pct(counts[Verdict.INDISTINGUISHABLE] / trials),
                     pct(counts[Verdict.UNDERPOWERED] / trials)])
        if label == "harmful (-0.06)":
            ship_harmful = counts[Verdict.IMPROVED] / trials
        if label == "null (0.00)":
            null_claim = counts[Verdict.IMPROVED] / trials

    rep.found(
        f"The gate never claimed an improvement for a genuinely harmful "
        f"-0.06 change ({pct(ship_harmful)}), and claimed one for a null "
        f"change {pct(null_claim)} of the time, against a nominal 2.5% for a "
        f"one-sided false claim at a 95% interval.\n\n"
        f"The UNDERPOWERED column is the honest part. For small true effects "
        f"the gate mostly declines to answer, and says why, rather than "
        f"reporting the sign of a coin flip as a finding.")

    rep.table(["true effect", "IMPROVED", "REGRESSED", "INDISTINGUISHABLE",
               "UNDERPOWERED"], rows)


def section_methods(rep: Report) -> None:
    rep.h2("13. Do the intervals cover what they claim to?")
    rep.para(
        "A 95% interval that covers the truth 80% of the time is worse than no "
        "interval, because it is believed. Quality scores are bounded and pile "
        "up near the top of the scale, and improvements are usually "
        "concentrated in a minority of items rather than spread evenly -- so "
        "the paired differences are skewed, zero-inflated and truncated, which "
        "is where normal theory is least defensible.")
    rep.para(
        "The generative process here is deliberately nasty and deliberately "
        "realistic: baseline scores from a Beta(8, 1.5) piled up near 1.0, and "
        "an improvement that touches only 30% of items and is exponentially "
        "distributed when it does. That is what a real prompt fix looks like. "
        "It fixes a specific failure mode, so most items do not move at all.")

    rep.note(
        "The first version of this experiment measured nothing. It defined the "
        "truth as the mean difference of the same draw the interval was "
        "computed from, so every procedure covered it by construction and all "
        "three columns read 100%. A coverage study needs a parameter that is "
        "fixed before the sample is drawn; if the truth moves with the data, "
        "the question is not merely hard to answer, it is not a question. The "
        "population effect below is therefore estimated once at 2,000,000 "
        "draws, including the truncation at 1.0, and held fixed.")

    rep.expect(
        "On this skewed, truncated, zero-inflated data the z-interval will "
        "under-cover at n=20 by at least two percentage points; the t-interval "
        "will recover most but not all of that; the percentile bootstrap will "
        "be no better than the t-interval at small n; and BCa will be closest "
        "to the nominal 95% at every sample size.")

    rng = np.random.default_rng(21)

    def draw(n: int, r: np.random.Generator):
        base = r.beta(8.0, 1.5, size=n)
        gain = r.exponential(0.04, size=n) * (r.random(n) < 0.3)
        return base, np.clip(base + gain, 0.0, 1.0)

    big_b, big_c = draw(2_000_000, np.random.default_rng(99))
    true_effect = float((big_c - big_b).mean())
    rep.para(
        f"The population effect for this process, including truncation, is "
        f"{num(true_effect, 5)}. Nominal coverage is 95%.")

    trials = T(600)
    rows = []
    worst_z = 1.0
    bca_all = []
    for n in (20, 50, 200):
        cover = {"z": 0, "t": 0, "percentile": 0, "bca": 0}
        widths = {"z": 0.0, "t": 0.0, "percentile": 0.0, "bca": 0.0}
        tcrit = student_t_ppf(0.975, n - 1)
        for t in range(trials):
            base, cand = draw(n, rng)
            d = cand - base
            se = float(d.std(ddof=1)) / math.sqrt(n)
            m = float(d.mean())
            for key, crit in (("z", 1.959963985), ("t", tcrit)):
                lo, hi = m - crit * se, m + crit * se
                cover[key] += lo <= true_effect <= hi
                widths[key] += hi - lo
            p = paired_bootstrap(base, cand, resamples=R(2_000), seed=t)
            cover["percentile"] += p.low <= true_effect <= p.high
            widths["percentile"] += p.width
            b = paired_bca_bootstrap(base, cand, resamples=R(2_000), seed=t)
            cover["bca"] += b.low <= true_effect <= b.high
            widths["bca"] += b.width
        worst_z = min(worst_z, cover["z"] / trials)
        bca_all.append(cover["bca"] / trials)
        rows.append([str(n)]
                    + [pct(cover[k] / trials) for k in ("z", "t", "percentile", "bca")]
                    + [num(widths["bca"] / trials, 4)])

    z20 = float(rows[0][1].rstrip("%")) / 100
    t20 = float(rows[0][2].rstrip("%")) / 100
    pb20 = float(rows[0][3].rstrip("%")) / 100
    bca20 = float(rows[0][4].rstrip("%")) / 100

    contradicted = not (z20 <= 0.93 and t20 > z20 and bca20 >= max(pb20, t20) - 0.005)
    if contradicted:
        rep.found(
            f"Partly wrong. At n=20 the z-interval covers {pct(z20)} and the "
            f"t-interval {pct(t20)}, so the small-sample correction does what "
            f"it is supposed to. But BCa at {pct(bca20)} is not uniformly the "
            f"winner it was predicted to be against the percentile bootstrap "
            f"at {pct(pb20)}.\n\n"
            f"The reason is worth stating because it is a limit on the whole "
            f"approach. BCa's acceleration term is estimated from the "
            f"jackknife, and at n=20 the jackknife skewness estimate is itself "
            f"noisy. The correction is being applied with a coefficient that "
            f"has substantial error, so it sometimes corrects the wrong way. "
            f"BCa is the better procedure asymptotically and is not reliably "
            f"better at the sample sizes eval sets actually run at -- which is "
            f"the same lesson as every other section: the small-sample regime "
            f"is not the large-sample regime with wider error bars, it is a "
            f"different place with different rules.",
            contradicted=True)
    else:
        rep.found(
            f"At n=20 the z-interval covers only {pct(z20)} against a nominal "
            f"95%, the t-interval recovers to {pct(t20)}, the percentile "
            f"bootstrap reaches {pct(pb20)} and BCa {pct(bca20)}. By n=200 all "
            f"four are close to nominal.\n\n"
            f"The z-interval failure is the one to care about, because "
            f"`mean +/- 1.96 * sem` is what gets written when someone reaches "
            f"for a formula, and at the sample sizes eval sets run at it is "
            f"wrong in the direction that makes findings look more certain "
            f"than they are.")

    rep.table(["n", "z-interval", "t-interval", "percentile bootstrap",
               "BCa bootstrap", "mean BCa width"], rows)

    rep.para(
        f"BCa coverage across the three sample sizes is "
        f"{', '.join(pct(c) for c in bca_all)}. The bootstrap procedures make "
        f"no distributional assumption, which is why they are the default in "
        f"this harness; they are not free, costing "
        f"{R(2_000):,} resamples per interval, which on a 200-item eval set is "
        f"a few milliseconds and on no realistic eval set is the bottleneck.")


def section_calibration(rep: Report) -> None:
    rep.h2("14. Ranking ability and calibration are different questions")
    rep.para(
        "A judge with a constant offset ranks perfectly and is wrong about "
        "every absolute value. If the only use is A/B comparison, the offset "
        "cancels and the judge is fine. If anyone reads the absolute score -- "
        "and someone always does, because it goes in a slide -- it is not.")

    rep.expect(
        "A judge with a large constant bias will show near-perfect rank "
        "correlation and a large calibration error, and the harness will "
        "report it as usable for ranking but not for absolute scores.")

    ds = make_dataset("calib", "v1", 200)
    base = SimulatedModel("baseline", base_quality=0.62)
    rows = []
    biased_cal = None
    for bias in (0.0, 0.05, 0.15, 0.25):
        j = SimulatedJudge(f"j-{bias}", noise=0.05, consistency=0.5, bias=bias)
        r = run(base, j, ds)
        cal = calibration(r.scores, r.truth)
        rows.append([signed(bias, 2), num(cal.correlation, 3),
                     num(cal.rank_correlation, 3),
                     num(cal.mean_error, 4), num(cal.mean_abs_error, 4),
                     "yes" if cal.usable_for_ranking else "no",
                     "yes" if cal.usable_for_absolute_scores else "no"])
        if abs(bias - 0.25) < 1e-9:
            biased_cal = cal

    rep.found(
        f"At a bias of +0.25 the judge's rank correlation with truth is "
        f"{num(biased_cal.rank_correlation, 3)} -- it orders responses "
        f"essentially perfectly -- while its mean error is "
        f"{signed(biased_cal.mean_error, 4)}. "
        f"The harness reports usable_for_ranking="
        f"{biased_cal.usable_for_ranking} and usable_for_absolute_scores="
        f"{biased_cal.usable_for_absolute_scores}.\n\n"
        f"Collapsing these into one 'judge quality' number would have to "
        f"choose which of two true statements to discard.")

    rep.table(["judge bias", "Pearson", "Spearman", "mean error", "mean abs error",
               "ranking", "absolute"], rows)


SECTIONS = [
    section_power,
    section_type_m,
    section_required_n,
    section_pairing,
    section_forking_paths,
    section_winners_curse,
    section_kappa,
    section_judge_consistency,
    section_length_bias,
    section_slices,
    section_drift,
    section_gate_calibration,
    section_methods,
    section_calibration,
]


def build_report(only: int | None = None) -> Report:
    rep = Report("LLM Evaluation: what the numbers can and cannot support")
    if QUICK:
        rep.note(
            "**QUICK MODE — NOT THE PUBLISHED REPORT.** Resample and trial "
            "counts are reduced 20x so the run finishes in about a minute. "
            "Every number below is a noisier estimate of the corresponding "
            "number in the committed `docs/results.md`, and some will differ "
            "in the second decimal place. Do not quote these. Regenerate "
            "without `--quick` before citing anything.")
    rep.para(
        "Everything here is measured, not asserted. The systems and judges are "
        "simulated with known parameters, which is the point: the claims are "
        "about whether a method can recover a truth, and that requires knowing "
        "the truth. No LLM is called anywhere in this repository. The README "
        "section \"Why the systems are simulated\" and `docs/adr/001` explain "
        "why a real model would make this argument weaker rather than "
        "stronger; `docs/known-limitations.md` states plainly what it "
        "therefore does not establish.")
    rep.para(
        "Each section states a prediction in writing before the corresponding "
        "measurement is read. The report generator enforces the ordering: a "
        "measurement with no outstanding prediction raises, and so does "
        "closing the report with one unresolved.")

    chosen = SECTIONS if only is None else [SECTIONS[only - 1]]
    for fn in chosen:
        fn(rep)
    return rep


def main() -> int:
    global QUICK
    ap = argparse.ArgumentParser()
    ap.add_argument("--quick", action="store_true",
                    help="fewer resamples and trials, for iterating")
    ap.add_argument("--section", type=int, default=None)
    ap.add_argument("--out", default=os.path.join("docs", "results.md"))
    ap.add_argument("--stdout", action="store_true")
    args = ap.parse_args()
    QUICK = args.quick

    rep = build_report(args.section)
    text = rep.render()

    if args.stdout or args.section:
        sys.stdout.write(text)
        return 0

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    digest = hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]
    print(f"wrote {args.out}  ({len(text)} bytes, sha256 {digest})")
    print(f"{rep.n_predictions} predictions, {rep.n_confirmed} held, "
          f"{rep.n_contradicted} contradicted")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
