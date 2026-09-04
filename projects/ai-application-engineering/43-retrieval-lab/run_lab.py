"""Generates docs/results.md.

Run: python run_lab.py [--out docs/results.md]

The output is byte-reproducible. test.ps1 generates it twice and compares
hashes, which is how a non-deterministic tie-break would be caught -- a check
that a within-process determinism test cannot perform.
"""

from __future__ import annotations

import argparse
import pathlib
import sys

import numpy as np

from rqlab import stats
from rqlab.chunking import CHUNKERS
from rqlab.corpus import build_corpus
from rqlab.distractors import build_distractors
from rqlab.experiment import K, POOL_DEPTH, Config, LabResults, run_grid
from rqlab.facts import FACTS, FAMILIES
from rqlab.indexes import RETRIEVERS
from rqlab.queries import QUERIES, QUERY_CLASSES, by_class
from rqlab.rerank import FEATURES, HAND_WEIGHTS, RERANKERS
from rqlab.report import Report, num, pct, signed
from rqlab.text import analysed

NDCG = f"ndcg@{K}"
BOOT = 4000
BOOT_HEADLINE = 10000


def best_retriever(r: LabResults, chunker: str, reranker: str = "none") -> str:
    return max(
        RETRIEVERS, key=lambda rt: (r.by_config(chunker, rt, reranker).mean(NDCG), rt)
    )


def section_corpus(rep: Report, r: LabResults) -> None:
    rep.h2("0. The corpus, and why it is generated rather than collected")
    corpus = build_corpus()
    distractors = build_distractors()
    docs = list(corpus.documents) + list(distractors)
    tokens = sum(len(analysed(d.text)) for d in docs)
    classes = by_class()

    rep.para(
        "Every retrieval benchmark inherits the same defect from the way it was "
        "built: an assessor read the documents and marked which ones answer each "
        "query. Passages the assessor missed are not merely unlabelled, they are "
        "labelled *irrelevant*, and every metric computed on top of them is "
        "biased by an amount nobody can measure."
    )
    rep.para(
        "This corpus is built in the opposite direction. Facts are declared "
        "first, as structured data. Documents are rendered from them in six "
        "layouts. The set of passages that answers a query is therefore known "
        "exactly, and -- more importantly -- so is the set that does not."
    )
    rep.table(
        ["quantity", "value"],
        [
            ["fact families", str(len(FAMILIES))],
            ["facts (family x qualifier)", str(len(FACTS))],
            ["documents holding answers", str(len(corpus.documents))],
            ["near-miss documents", str(len(distractors))],
            ["analysed tokens", f"{tokens:,}"],
            ["labelled placements", str(len(corpus.placements))],
            ["queries", str(len(QUERIES))],
            *[[f"  {c}", str(len(classes[c]))] for c in QUERY_CLASSES],
        ],
    )
    rep.para(
        "The structure that makes the experiment work is that a value is "
        "rendered *without* the qualifier that scopes it. The sentence "
        "\"Retained for 400 days before automatic deletion.\" answers nothing on "
        "its own; it answers a question only in combination with the heading "
        "that says which plan it applies to. A chunk holding the sentence and "
        "not the heading has kept the answer and thrown away its meaning, and "
        "the labels can tell the difference."
    )
    rep.para(
        "Three invariants are enforced in code and by tests, because each of "
        "them, if violated, would silently invalidate every number below:"
    )
    rep.bullets([
        "no near-miss document may contain a governed value, or it would be an "
        "unlabelled correct answer;",
        "no query may quote the value it asks for, or it becomes retrievable by "
        "exact match and measures nothing;",
        "every governed value must be a distinctive string, so the first check "
        "is meaningful. The fact table originally contained the value \"4\", "
        "which makes substring search useless and would have let a generated "
        "sentence reading \"the default fan-out is 4\" act as an unlabelled "
        "answer.",
    ])


def section_ceilings(rep: Report, r: LabResults) -> None:
    rep.h2("1. The cascade of ceilings")
    rep.para(
        "A pipeline can lose an answer in three places, and for a given "
        "(query, target) pair exactly one of them is responsible. Testing in "
        "pipeline order makes the assignment exclusive and exhaustive:"
    )
    rep.bullets([
        "**chunker** -- no chunk anywhere in the index covers the target;",
        f"**retriever** -- a covering chunk exists but none reached the "
        f"candidate pool (depth {POOL_DEPTH});",
        f"**ranker** -- a covering chunk reached the pool but not the top {K};",
        "**served** -- a covering chunk is in the top k.",
    ])
    rep.para(
        "The four sum to one by construction, so the decomposition has no "
        "residual to argue about. A defect in it does not produce a plausible "
        "split, it produces a row that does not sum to one, and a test asserts "
        "that none does."
    )

    rep.expect(
        "Chunkers that cut small will have the highest reachability -- a "
        "smaller unit is more likely to fall entirely inside one chunk -- and "
        "the reachability ranking will therefore be roughly the inverse of "
        "chunk size. Reachability is an upper bound and not an outcome, so it "
        "should not predict the final score."
    )
    rows = []
    for cname in CHUNKERS:
        rt = best_retriever(r, cname)
        run = r.by_config(cname, rt, "none")
        share = run.attribution.share()
        lad = run.ladder
        rows.append([
            cname,
            str(run.cost.n_chunks),
            rt,
            num(lad.reachable),
            num(lad.pooled),
            num(lad.served),
            num(share["chunker"]),
            num(share["retriever"]),
            num(share["ranker"]),
            lad.binding_stage(),
        ])
    rows.sort(key=lambda x: -float(x[3]))
    rep.table(
        ["chunker", "chunks", "best retriever", "reach", "pooled", "served",
         "lost:chunker", "lost:retriever", "lost:ranker", "binding"],
        rows,
    )

    sw = r.by_config("sentence_window_1", best_retriever(r, "sentence_window_1"), "none")
    rc = r.by_config("recursive_240", best_retriever(r, "recursive_240"), "none")
    rep.found(
        f"The prediction is wrong, and it is wrong in the one place where only "
        f"size varies. `fixed_120` cuts half as long as `fixed_240` and reaches "
        f"{num(r.by_config('fixed_120', best_retriever(r, 'fixed_120'), 'none').ladder.reachable)} "
        f"against "
        f"{num(r.by_config('fixed_240', best_retriever(r, 'fixed_240'), 'none').ladder.reachable)} "
        f"-- smaller units are *worse*. The reason is that a target is a "
        f"conjunction. Halving the window makes each individual span more "
        f"likely to fit and the pair less likely to co-occur, and it is the "
        f"pair that carries the meaning. `sentence_window_1` reaches "
        f"{num(sw.ladder.reachable)} not because its unit is small but because "
        f"the window re-attaches the neighbouring sentence, which is a "
        f"different mechanism from cutting small.\n\n"
        f"The second half of the prediction holds, and the consequence is the "
        f"opposite of the intuition that motivates small chunks: "
        f"`sentence_window_1` reaches {num(sw.ladder.reachable)} of targets and "
        f"serves {num(sw.ladder.served)}, the worst in the grid. It did not "
        f"remove the loss, it moved it: chunker loss "
        f"{num(sw.attribution.share()['chunker'])} against retriever loss "
        f"{num(sw.attribution.share()['retriever'])}. Splitting into "
        f"{sw.cost.n_chunks:,} units gives every answer a home and gives the "
        f"retriever {sw.cost.n_chunks // rc.cost.n_chunks}x more places to look, "
        f"each too short to carry enough signal to be found.",
        contradicted=True,
    )
    rep.para(
        "This is the shape of the whole problem. The stage you measure is the "
        "stage you fix, and fixing it in isolation relocates the failure to a "
        "stage you were not measuring, where it is invisible. The binding "
        "column names the stage that is actually costing the most for each "
        "configuration; for five of seven chunkers it is the retriever, which "
        "is not where the chunking literature suggests looking."
    )

    _severance(rep, r)


def _severance(rep: Report, r: LabResults) -> None:
    """Placement-level survival: the measurement the fact-level ceiling hides."""
    rep.h3("1a. Reachability saturates; severance does not")
    rep.para(
        "The reachability column above is computed per *fact*, and a fact "
        "appears in up to three layouts. A chunker can destroy an answer in "
        "one layout and still score 1.000 because redundancy rescued it "
        "elsewhere. That is a property of the corpus, not of the chunker, and "
        "it is why five of seven rows tie at the top. Measuring per "
        "*placement* -- each individual rendering of a fact in a document -- "
        "removes the rescue and separates the two ways a chunking can fail:"
    )
    rep.bullets([
        "**lost** -- no chunk contains the sentence stating the value at all;",
        "**severed** -- some chunk contains the sentence, but no chunk "
        "contains it together with the heading that says which plan or region "
        "it applies to.",
    ])
    rep.expect(
        "The two failures call for opposite fixes -- lost answers want smaller "
        "units, severed answers want larger ones -- so a useful chunker "
        "comparison has to report them separately. Loss should dominate at "
        "small chunk sizes and severance at large ones."
    )

    rows = []
    for cname in CHUNKERS:
        surv = r.bundles[cname].survival
        full = sum(v[0] for v in surv.values())
        sev = sum(v[1] for v in surv.values())
        tot = sum(v[2] for v in surv.values())
        pm = surv.get("plan_matrix", (0, 0, 0))
        rows.append([
            cname,
            str(r.bundles[cname].coverage.n_chunks),
            f"{full}/{tot}",
            str(sev),
            str(tot - full - sev),
            f"{pm[0]}/{pm[2]}",
        ])
    rep.table(
        ["chunker", "chunks", "placements intact", "severed", "lost",
         "plan_matrix intact"],
        rows,
    )

    tot_lost = sum(
        sum(v[2] - v[0] - v[1] for v in r.bundles[c].survival.values())
        for c in CHUNKERS
    )
    tot_sev = sum(
        sum(v[1] for v in r.bundles[c].survival.values()) for c in CHUNKERS
    )
    s240 = r.bundles["structural_240"].survival
    sp240 = r.bundles["structural_prefixed_240"].survival
    rep.found(
        f"Severance is not one failure mode among two, it is essentially the "
        f"only one. Across all seven chunkers and 160 placements each, "
        f"{tot_sev} placements were severed and {tot_lost} were lost outright. "
        f"The prediction that loss dominates at small sizes is contradicted: "
        f"`sentence_window_1`, the smallest unit in the grid, loses nothing "
        f"and severs {sum(v[1] for v in r.bundles['sentence_window_1'].survival.values())}, "
        f"the most in the grid.\n\n"
        f"The controlled comparison is the last two structural rows. "
        f"`structural_240` and `structural_prefixed_240` produce the *identical* "
        f"{r.bundles['structural_240'].coverage.n_chunks} chunk boundaries; the "
        f"only difference is that the second repeats the heading path into the "
        f"body of each chunk. Severance goes from "
        f"{sum(v[1] for v in s240.values())} to {sum(v[1] for v in sp240.values())}, "
        f"and `plan_matrix` -- the layout where every value is scoped by a "
        f"heading -- goes from {s240['plan_matrix'][0]}/{s240['plan_matrix'][2]} "
        f"to {sp240['plan_matrix'][0]}/{sp240['plan_matrix'][2]}. Both chunkers "
        f"report a fact-level reachability of 1.000.",
        contradicted=True,
    )
    rep.note(
        "This is the single most useful number in the report and it costs one "
        "pass over the index with no retrieval, no queries and no model. If a "
        "chunking severs answers from the headings that scope them, every "
        "downstream measurement is bounded by that and no amount of retriever "
        "tuning will recover it."
    )


def section_labels(rep: Report, r: LabResults) -> None:
    rep.h2("2. Document-level labels cannot measure chunking")
    rep.para(
        "Public retrieval benchmarks label relevance at document level: an "
        "assessor marks a document as answering a query, and every chunk of "
        "that document inherits the label. Applying that judging rule to the "
        "identical rankings isolates one variable -- label granularity -- and "
        "prices it."
    )
    rep.expect(
        "Under document-level labels a chunk is relevant whenever it comes "
        "from the right document, so a chunker that severs an answer from its "
        "qualifier is scored as though it had not. The spread between the best "
        "and worst chunker should collapse towards zero, and the ordering "
        "should change."
    )
    rows = []
    span_scores: dict[str, float] = {}
    doc_scores: dict[str, float] = {}
    for cname in CHUNKERS:
        rt = best_retriever(r, cname)
        run = r.by_config(cname, rt, "none")
        span_scores[cname] = run.mean(NDCG)
        doc_scores[cname] = float(run.ndcg_doclevel.mean())
    span_rank = sorted(span_scores, key=lambda c: -span_scores[c])
    doc_rank = sorted(doc_scores, key=lambda c: -doc_scores[c])
    for cname in span_rank:
        rows.append([
            cname,
            num(span_scores[cname]),
            str(span_rank.index(cname) + 1),
            num(doc_scores[cname]),
            str(doc_rank.index(cname) + 1),
        ])
    rep.table(
        ["chunker", f"nDCG@{K} (span labels)", "rank", f"nDCG@{K} (doc labels)", "rank"],
        rows,
    )
    span_spread = max(span_scores.values()) - min(span_scores.values())
    doc_spread = max(doc_scores.values()) - min(doc_scores.values())
    moved = sum(1 for c in CHUNKERS if span_rank.index(c) != doc_rank.index(c))
    rep.found(
        f"Span-level labels separate the chunkers by {num(span_spread)} nDCG. "
        f"Document-level labels separate them by {num(doc_spread)}, and "
        f"{moved} of {len(CHUNKERS)} chunkers change rank. The direction is as "
        f"predicted; the size is the part worth carrying away. An evaluation "
        f"with document-level labels is not a weak instrument for comparing "
        f"chunkers -- it is not an instrument for comparing chunkers, and it "
        f"will report a confident ordering anyway."
    )
    rep.note(
        "Both columns are computed from the same rankings produced by the same "
        "runs. Nothing about the systems differs between them. The only "
        "difference is what the judge was allowed to see."
    )

    lead = Config("recursive_240", "bm25", "none")
    span_q = r.runs[lead.name].metrics[NDCG]
    doc_q = r.runs[lead.name].ndcg_doclevel
    up = int(np.sum(doc_q > span_q + 1e-12))
    down = int(np.sum(doc_q < span_q - 1e-12))
    same = len(span_q) - up - down
    corr = float(np.corrcoef(span_q, doc_q)[0, 1])
    rep.para(
        "One more property of the doc-level column is worth stating, because "
        "the obvious reading of it is wrong. Document-level labels are a "
        "superset of span-level ones, so the natural expectation is that they "
        "inflate every score. They do not, because nDCG normalises by the "
        "ideal and the ideal grows with the relevant set: a ranking that "
        "filled every slot it could under span labels no longer fills every "
        "slot the larger ideal assumes."
    )
    rep.expect(
        "If the effect were pure inflation, per-query scores would move in one "
        "direction only. If instead the growing normaliser matters, they will "
        "move in both, and the correlation between the two judging rules will "
        "be well below one."
    )
    rep.table(
        ["queries scored higher", "scored lower", "unchanged", "correlation"],
        [[str(up), str(down), str(same), num(corr)]],
    )
    rep.found(
        f"On the leading configuration {up} queries score higher under "
        f"document-level labels, {down} score lower and {same} are unchanged; "
        f"the two judging rules correlate at {num(corr)}. Document-level "
        f"labels do not inflate the measurement, they decorrelate it. That is "
        f"the stronger objection: a biased instrument can be corrected for, a "
        f"decorrelated one cannot."
    )


def _family(r: LabResults, reranker: str = "none") -> list[Config]:
    return [
        Config(c, rt, reranker) for c in CHUNKERS for rt in RETRIEVERS
    ]


def section_significance(rep: Report, r: LabResults) -> None:
    rep.h2("3. Most of the grid is noise")
    configs = _family(r)
    n = len(configs)
    pairs = [(i, j) for i in range(n) for j in range(i + 1, n)]
    rep.para(
        f"The {n} chunker x retriever combinations yield {len(pairs)} pairwise "
        f"comparisons. Every configuration ran on the identical "
        f"{len(QUERIES)} queries, so the unit of analysis is the per-query "
        f"difference and the correct test is paired."
    )
    rep.expect(
        "Two effects push in opposite directions and both are large. Pairing "
        "removes between-query variance, which in retrieval dwarfs "
        "between-system variance, so the paired test will find far more real "
        "differences than the unpaired one. Correcting for "
        f"{len(pairs)} simultaneous comparisons will remove many of them again. "
        "The naive analysis -- unpaired and uncorrected -- will report the most "
        "significant results of all four combinations, and its extra findings "
        "are false."
    )

    arrays = {c.name: r.runs[c.name].metrics[NDCG] for c in configs}
    raw_p: list[float] = []
    t_p: list[float] = []
    unpaired_p: list[float] = []
    diffs: list[float] = []
    sds: list[float] = []
    for i, j in pairs:
        a = arrays[configs[i].name]
        b = arrays[configs[j].name]
        res = stats.compare(a, b, iterations=BOOT, seed=1000 + i * 97 + j)
        raw_p.append(res.p_value)
        t_p.append(stats.paired_t(a, b))
        sds.append(res.sd_diff)
        diffs.append(res.mean_diff)
        unpaired_p.append(stats.unpaired_naive(a, b))
    adj_p = stats.holm(t_p)
    adj_unpaired = stats.holm(unpaired_p)
    adj_perm = stats.holm(raw_p)

    rep.table(
        ["analysis", "significant at 0.05", "share of comparisons"],
        [
            ["unpaired, uncorrected",
             str(sum(1 for p in unpaired_p if p < 0.05)),
             pct(sum(1 for p in unpaired_p if p < 0.05) / len(pairs))],
            ["unpaired, Holm-corrected",
             str(sum(1 for p in adj_unpaired if p < 0.05)),
             pct(sum(1 for p in adj_unpaired if p < 0.05) / len(pairs))],
            ["paired, uncorrected",
             str(sum(1 for p in raw_p if p < 0.05)),
             pct(sum(1 for p in raw_p if p < 0.05) / len(pairs))],
            ["paired, Holm-corrected",
             str(sum(1 for p in adj_p if p < 0.05)),
             pct(sum(1 for p in adj_p if p < 0.05) / len(pairs))],
        ],
    )
    n_paired = sum(1 for p in adj_p if p < 0.05)
    n_naive = sum(1 for p in unpaired_p if p < 0.05)
    contradicted = n_paired > n_naive
    rep.found(
        f"The paired, corrected analysis -- the only defensible one -- finds "
        f"{n_paired} real differences out of {len(pairs)}, "
        f"{pct(n_paired / len(pairs))}. The naive analysis finds {n_naive}. "
        + (
            "The prediction was wrong in direction: pairing gains more than "
            f"correction loses, so the defensible analysis finds {n_paired - n_naive} "
            "*more* differences than the naive one. Discarding the pairing "
            "does not merely inflate significance, it destroys the power to "
            "see the effects that are actually there -- and the two errors do "
            "not cancel, because they act on different comparisons."
            if contradicted else
            "The prediction was that pairing would dominate and the naive "
            "analysis would over-report; it does over-report, "
            f"by {n_naive - n_paired} comparisons."
        ),
        contradicted=contradicted,
    )
    rep.note(
        "The comparisons the two analyses disagree about are not "
        "interchangeable. Naive significance is concentrated in pairs with "
        "large mean differences; paired significance is concentrated in pairs "
        "with small, consistent ones. A method that only sees the former will "
        "systematically miss the improvements that are worth shipping -- small "
        "and reliable -- and systematically endorse the ones that are not."
    )
    _resolution(rep, len(pairs), raw_p, adj_perm, t_p, adj_p)
    return None


def _resolution(
    rep: Report,
    m: int,
    perm_p: list[float],
    adj_perm: list[float],
    t_p: list[float],
    adj_t: list[float],
) -> None:
    """The bug this section originally shipped, kept because it is instructive."""
    rep.h3("3a. The correction the resampling budget cannot satisfy")
    floor = stats.permutation_resolution(BOOT)
    need = stats.iterations_for_holm(m)
    rep.para(
        f"The paired row above is computed with a t-test, and the reason is a "
        f"defect this report shipped in an earlier revision. The permutation "
        f"test is the better instrument -- it assumes nothing about the "
        f"distribution of per-query differences, which are mostly exactly zero "
        f"with a heavy tail -- but it is a *counting* procedure, and it cannot "
        f"report a p-value below 1/(B+1). At B = {BOOT:,} that floor is "
        f"{floor:.2e}. Holm's strictest threshold over {m} comparisons is "
        f"0.05/{m} = {0.05 / m:.2e}."
    )
    rep.expect(
        "The floor is above the threshold, so no comparison in this family can "
        "clear it regardless of how large its effect is. The permutation "
        "column will report exactly zero significant differences, and it will "
        "look like a finding about retrieval rather than a fact about the "
        "resampling budget."
    )
    rep.table(
        ["test", "raw p < 0.05", "Holm-corrected p < 0.05", "smallest raw p"],
        [
            ["paired permutation (B = {:,})".format(BOOT),
             str(sum(1 for p in perm_p if p < 0.05)),
             str(sum(1 for p in adj_perm if p < 0.05)),
             f"{min(perm_p):.2e}"],
            ["paired t",
             str(sum(1 for p in t_p if p < 0.05)),
             str(sum(1 for p in adj_t if p < 0.05)),
             f"{min(t_p):.2e}"],
        ],
    )
    rep.found(
        f"The permutation test finds {sum(1 for p in perm_p if p < 0.05)} raw "
        f"differences and {sum(1 for p in adj_perm if p < 0.05)} after "
        f"correction; its smallest attainable p-value is {min(perm_p):.2e}, "
        f"which is the floor itself. The t-test, on the identical differences, "
        f"finds {sum(1 for p in adj_t if p < 0.05)} after correction with a "
        f"smallest raw p-value of {min(t_p):.2e}. The zero was an artefact of "
        f"arithmetic, not a property of the systems. Recovering it with a "
        f"permutation test would need B > {need:,} -- "
        f"{need / BOOT:.1f}x the current budget on every one of {m} "
        f"comparisons."
    )
    rep.note(
        "The general rule: a randomisation test and a family-wise correction "
        "constrain each other. Before running one inside the other, check that "
        "1/(B+1) < alpha/m. If it is not, the table will fill with zeros and "
        "read as a result. The defect is invisible precisely because 'nothing "
        "was significant after correction' is exactly what an honest, "
        "well-powered-but-null experiment also looks like."
    )


def section_power(rep: Report, r: LabResults) -> None:
    rep.h2("4. What this corpus cannot measure")
    configs = _family(r)
    arrays = {c.name: r.runs[c.name].metrics[NDCG] for c in configs}
    names = [c.name for c in configs]
    sds = []
    for i in range(len(names)):
        for j in range(i + 1, len(names)):
            d = arrays[names[i]] - arrays[names[j]]
            if len(d) > 1:
                sds.append(float(d.std(ddof=1)))
    sds.sort()
    median_sd = sds[len(sds) // 2]

    rep.para(
        "Before asking which configuration wins, ask what size of difference "
        "this corpus is capable of resolving at all. For a paired test the "
        "answer follows from the standard deviation of the per-query "
        "differences and the number of queries."
    )
    rep.expect(
        "Per-query nDCG differences are mostly exactly zero with a heavy tail, "
        "so their standard deviation will be a substantial fraction of the "
        "nDCG scale. With a few hundred queries the smallest detectable "
        "difference will be on the order of a few nDCG points -- comparable to "
        "the differences typically reported as improvements."
    )
    rows = []
    for n_q in (50, 100, len(QUERIES), 500, 1000, 5000):
        mde = stats.minimum_detectable_effect(median_sd, n_q)
        rows.append([
            str(n_q) + (" (this corpus)" if n_q == len(QUERIES) else ""),
            num(mde),
        ])
    rep.table(["queries", f"minimum detectable nDCG@{K} difference"], rows)

    mde_here = stats.minimum_detectable_effect(median_sd, len(QUERIES))
    need_1pt = stats.required_queries(0.01, median_sd)
    need_2pt = stats.required_queries(0.02, median_sd)
    ordered = sorted(configs, key=lambda c: -r.runs[c.name].mean(NDCG))
    top = r.runs[ordered[0].name].mean(NDCG)
    within = sum(1 for c in configs if top - r.runs[c.name].mean(NDCG) < mde_here)
    rep.found(
        f"The median standard deviation of paired differences is "
        f"{num(median_sd)}. With {len(QUERIES)} queries the minimum detectable "
        f"difference at 80% power is {num(mde_here)} nDCG. To resolve a "
        f"one-point difference would take {need_1pt:,} queries; two points, "
        f"{need_2pt:,}. {within} of the {len(configs)} configurations sit "
        f"within {num(mde_here)} of the leader, which means this corpus cannot "
        f"order them, and neither can any report built on it -- including this "
        f"one."
    )
    rep.note(
        "This is the number that should appear first in every retrieval "
        "comparison and appears in almost none. A fifty-query evaluation -- "
        f"common in practice -- cannot resolve anything smaller than "
        f"{num(stats.minimum_detectable_effect(median_sd, 50))} nDCG, which is "
        "larger than the difference between most of the configurations anyone "
        "argues about."
    )


def section_optimism(rep: Report, r: LabResults) -> None:
    rep.h2("5. What tuning on the evaluation set is worth")
    rep.para(
        "The `fitted` reranker learns seven feature weights by coordinate "
        "ascent on nDCG. It is fitted on half the queries and reported on the "
        "other half. To price the shortcut that is taken instead, the same "
        "fitting is also run *on the reporting half itself*, and both numbers "
        "are shown."
    )
    rep.expect(
        "Seven weights on a few hundred queries is not a high-capacity model, "
        "so the optimism should be visible but modest -- larger than the "
        "differences between neighbouring configurations, smaller than the "
        "spread of the grid."
    )
    keys = sorted(r.refit_on_report)
    gaps = [r.refit_on_report[k] - r.honest_on_report[k] for k in keys]
    rows = []
    for k in sorted(keys, key=lambda k: -(r.refit_on_report[k] - r.honest_on_report[k]))[:8]:
        rows.append([
            k,
            num(r.honest_on_report[k]),
            num(r.refit_on_report[k]),
            signed(r.refit_on_report[k] - r.honest_on_report[k]),
        ])
    rep.table(
        ["chunker/retriever", "held out (honest)", "fitted on the reported half",
         "optimism"],
        rows,
    )
    mean_gap = float(np.mean(gaps))
    max_gap = float(np.max(gaps))
    configs = _family(r)
    arrays = {c.name: r.runs[c.name].metrics[NDCG] for c in configs}
    sds = []
    names = [c.name for c in configs]
    for i in range(len(names)):
        for j in range(i + 1, len(names)):
            sds.append(float((arrays[names[i]] - arrays[names[j]]).std(ddof=1)))
    sds.sort()
    mde = stats.minimum_detectable_effect(sds[len(sds) // 2], len(QUERIES))
    rep.found(
        f"Mean optimism across the {len(keys)} chunker x retriever combinations "
        f"is {signed(mean_gap)} nDCG; the worst is {signed(max_gap)}. Against a "
        f"minimum detectable effect of {num(mde)}, an optimism of "
        f"{num(mean_gap)} is "
        + ("large enough to manufacture a publishable improvement out of nothing."
           if mean_gap > mde * 0.5 else
           "smaller than the noise floor, so on this corpus the shortcut would "
           "not by itself fabricate a result -- which is a statement about this "
           "corpus and this seven-parameter model, not about the practice.")
    )
    rep.para(
        "The seven features and the weights the honest fit selected, for the "
        "leading configuration:"
    )
    lead = max(keys, key=lambda k: r.honest_on_report[k])
    w = r.fitted_weights[lead]
    rep.table(
        ["feature", "hand-chosen weight", f"fitted weight ({lead})"],
        [[f, num(float(HAND_WEIGHTS[i]), 2), num(float(w[i]), 2)]
         for i, f in enumerate(FEATURES)],
    )


def section_classes(rep: Report, r: LabResults) -> None:
    rep.h2("6. The aggregate winner loses on the class that matters")
    rep.para(
        "Queries fall into five classes that fail differently. `implicit` is "
        "the interesting one: the qualifier is never named, only implied "
        "(\"for our largest accounts\" rather than \"Enterprise\"), so no term "
        "weighting can reach it -- the query and the heading share no token."
    )
    configs = _family(r)
    rep.expect(
        "The lexical retrievers will win overall, because most queries share "
        "vocabulary with the documents. On `implicit` they should collapse and "
        "the distributional retriever should win, since that class is exactly "
        "the case term matching cannot serve."
    )
    overall_best = max(configs, key=lambda c: (r.runs[c.name].mean(NDCG), c.name))
    rows = []
    per_class_best: dict[str, Config] = {}
    for cls in QUERY_CLASSES:
        best = max(configs, key=lambda c: (r.runs[c.name].class_ndcg.get(cls, 0.0), c.name))
        per_class_best[cls] = best
        rows.append([
            cls,
            str(len(by_class()[cls])),
            num(r.runs[overall_best.name].class_ndcg.get(cls, 0.0)),
            best.name,
            num(r.runs[best.name].class_ndcg.get(cls, 0.0)),
            signed(r.runs[best.name].class_ndcg.get(cls, 0.0)
                   - r.runs[overall_best.name].class_ndcg.get(cls, 0.0)),
        ])
    rep.table(
        ["class", "queries", f"aggregate winner ({overall_best.name})",
         "best for this class", "its score", "gain"],
        rows,
    )

    sizes = {c: len(by_class()[c]) for c in QUERY_CLASSES}
    total = sum(sizes.values())
    oracle = sum(
        r.runs[per_class_best[c].name].class_ndcg.get(c, 0.0) * sizes[c]
        for c in QUERY_CLASSES
    ) / total
    single = r.runs[overall_best.name].mean(NDCG)
    distinct = len({per_class_best[c].name for c in QUERY_CLASSES})
    lex_imp = r.runs[Config("recursive_240", "bm25", "none").name].class_ndcg.get("implicit", 0.0)
    sem_imp = r.runs[Config("recursive_240", "lsa", "none").name].class_ndcg.get("implicit", 0.0)
    rep.found(
        f"An oracle that routed each class to its best configuration would "
        f"score {num(oracle)} against {num(single)} for the best single "
        f"configuration, a gain of {signed(oracle - single)} using "
        f"{distinct} distinct configurations. On `implicit` specifically, "
        f"BM25 scores {num(lex_imp)} and the distributional index scores "
        f"{num(sem_imp)} on the identical chunking"
        + (" -- the predicted inversion." if sem_imp > lex_imp else
           " -- the predicted inversion does not appear. Latent semantic "
           "indexing over this corpus does not learn that \"largest accounts\" "
           "means Enterprise, because the two never co-occur: the corpus was "
           "written by a generator that had no reason to put them in the same "
           "chunk. The class remains unserved by everything in the grid, "
           "which is a more useful finding than a win would have been."),
        contradicted=sem_imp <= lex_imp,
    )
    rep.para(
        f"Whether {signed(oracle - single)} justifies building a router is a "
        f"cost question, and the honest answer here is no: the gain is "
        f"comparable to the minimum detectable effect from section 4. The "
        f"value of the table is not the routing gain, it is that the "
        f"aggregate hides a class the whole grid fails at."
    )


def section_rerank(rep: Report, r: LabResults) -> None:
    rep.h2("7. Reranking amplifies retrieval, it does not repair it")
    rep.para(
        "A reranker reorders the candidate pool. It cannot add to it. Its "
        "ceiling is therefore the fraction of targets whose covering chunk "
        "reached the pool, and that quantity is already in the ladder from "
        "section 1."
    )
    rep.expect(
        "Rerank gain should be near zero where pool recall is low -- there is "
        "nothing to promote -- and larger where pool recall is high but the "
        "ordering within the pool is poor. Plotted against pool recall the "
        "relationship should be positive."
    )
    rows = []
    xs, ys = [], []
    for cname in CHUNKERS:
        for rt in RETRIEVERS:
            base = r.by_config(cname, rt, "none")
            best_rr = max(
                RERANKERS, key=lambda rr: (r.by_config(cname, rt, rr).mean(NDCG), rr)
            )
            gain = r.by_config(cname, rt, best_rr).mean(NDCG) - base.mean(NDCG)
            xs.append(base.ladder.pooled)
            ys.append(gain)
            rows.append([f"{cname}/{rt}", num(base.ladder.pooled),
                         num(base.mean(NDCG)), best_rr, signed(gain)])
    rows.sort(key=lambda x: float(x[1]))
    rep.table(
        ["chunker/retriever", f"pool recall (depth {POOL_DEPTH})",
         f"nDCG@{K} unranked", "best reranker", "gain"],
        [rows[i] for i in list(range(4)) + list(range(len(rows) - 4, len(rows)))],
    )
    xa, ya = np.array(xs), np.array(ys)
    corr = float(np.corrcoef(xa, ya)[0, 1]) if len(xa) > 2 else 0.0
    lo = ya[xa < np.median(xa)].mean()
    hi = ya[xa >= np.median(xa)].mean()
    rep.found(
        f"Across all {len(xs)} chunker x retriever combinations the "
        f"correlation between pool recall and the best available rerank gain "
        f"is {num(corr)}. Configurations in the lower half by pool recall gain "
        f"{signed(float(lo))} on average; the upper half gain "
        f"{signed(float(hi))}. "
        + ("The relationship is positive as predicted."
           if corr > 0 else
           "The relationship is negative, contradicting the prediction. The "
           "reason is visible in the ladder: where pool recall is already high "
           "the pool ordering is also already good, so there is little for a "
           "reranker to fix, while the low-recall configurations have badly "
           "ordered pools with some signal left in them. The ceiling argument "
           "is still correct -- a reranker cannot exceed pool recall -- but it "
           "does not imply that high pool recall leaves room to gain."),
        contradicted=corr <= 0,
    )
    rep.note(
        "Either way the practical conclusion is the same and it is the one "
        "worth taking: rerank gains measured on one pipeline do not transfer "
        "to another, because the gain is a property of how badly the pool was "
        "ordered, not of the reranker."
    )


def section_harm(rep: Report, r: LabResults) -> None:
    rep.h2("8. The failure nobody counts")
    rep.para(
        "nDCG, MRR and recall all answer \"did the right thing appear?\". None "
        "answers \"did the wrong thing appear instead?\". In this corpus the "
        "distinction is sharp, because a chunk can hold the correct value "
        "under the wrong plan, or the correct value under no plan at all. Both "
        "produce a fluent, cited, wrong answer."
    )
    rep.para(
        "Two counters make it visible. `misleading@1` is the rate at which the "
        "top result is such a chunk. `unanswered@10` is the rate at which "
        "nothing relevant *and* nothing misleading is returned -- the good "
        "failure, where the system visibly has nothing and a grounded "
        "generator declines."
    )
    rep.expect(
        "Chunkers that separate a value from its qualifier will produce more "
        "misleading top results. `structural_prefixed_240` repeats the heading "
        "path into every chunk, so by construction it can never produce a "
        "chunk holding a value without its qualifier, and its misleading rate "
        "should come only from sibling confusion."
    )
    rows = []
    for cname in CHUNKERS:
        rt = best_retriever(r, cname)
        run = r.by_config(cname, rt, "none")
        rows.append([
            cname, rt, num(run.mean(NDCG)),
            num(run.mean("misleading@1")),
            num(run.mean(f"misleading@{K}")),
            num(run.mean(f"unanswered@{K}")),
        ])
    rows.sort(key=lambda x: -float(x[3]))
    rep.table(
        ["chunker", "retriever", f"nDCG@{K}", "misleading@1",
         f"misleading@{K}", f"unanswered@{K}"],
        rows,
    )
    worst = rows[0]
    prefixed = next(x for x in rows if x[0] == "structural_prefixed_240")
    plain = next(x for x in rows if x[0] == "structural_240")
    rep.found(
        f"The worst configuration puts a misleading chunk first on "
        f"{pct(float(worst[3]))} of queries. `structural_prefixed_240` reads "
        f"{num(float(prefixed[3]))} against {num(float(plain[3]))} for the "
        f"same strategy without heading propagation -- repeating the heading "
        f"path costs nothing and removes an entire failure mode. Note that "
        f"the two are not distinguishable on nDCG "
        f"({num(float(prefixed[2]))} against {num(float(plain[2]))}), which is "
        f"the point: the metric everyone reports cannot see the difference "
        f"between them."
    )


def section_pareto(rep: Report, r: LabResults) -> None:
    rep.h2("9. The frontier, and what is dominated")
    rep.para(
        "Cost here is counted, not timed. Wall-clock on a shared machine "
        "measures the machine, and a cost axis that moves between runs cannot "
        "appear on a frontier that is supposed to be reproducible. The count "
        "is multiply-accumulate operations charged by a query: postings "
        "visited for BM25, matrix rows times dimensions for the dense indexes, "
        "and the sum of both for the hybrids."
    )
    rep.expect(
        "Dense retrieval over many small chunks will be the most expensive and "
        "among the worst, so the frontier should be short: a handful of "
        "configurations, most of the grid dominated."
    )
    pts = []
    for cname in CHUNKERS:
        for rt in RETRIEVERS:
            for rr in RERANKERS:
                run = r.by_config(cname, rt, rr)
                pts.append((run.cost.query_ops, run.mean(NDCG), run.config.name,
                            run.cost.index_bytes))
    frontier = []
    for ops, q, name, b in sorted(pts):
        if all(not (o <= ops and s >= q and n != name) for o, s, n, _ in pts):
            frontier.append((ops, q, name, b))
    seen_q = -1.0
    trimmed = []
    for ops, q, name, b in sorted(frontier):
        if q > seen_q:
            trimmed.append((ops, q, name, b))
            seen_q = q
    rep.table(
        ["query ops", f"nDCG@{K}", "configuration", "index bytes"],
        [[f"{int(o):,}", num(q), n, f"{b:,}"] for o, q, n, b in trimmed],
    )
    cheapest = trimmed[0]
    best = max(trimmed, key=lambda x: x[1])
    rep.found(
        f"{len(trimmed)} of {len(pts)} configurations are on the frontier; "
        f"{pct(1 - len(trimmed) / len(pts))} of the grid is strictly "
        f"dominated. The cheapest frontier point scores {num(cheapest[1])} at "
        f"{int(cheapest[0]):,} ops; the best scores {num(best[1])} at "
        f"{int(best[0]):,} ops. Buying the last "
        f"{num(best[1] - cheapest[1])} nDCG costs "
        f"{best[0] / max(cheapest[0], 1):.2f}x the query work."
    )


def section_guide(rep: Report, r: LabResults) -> None:
    rep.h2("10. What to actually do")
    configs = _family(r)
    arrays = {c.name: r.runs[c.name].metrics[NDCG] for c in configs}
    best = max(configs, key=lambda c: (r.runs[c.name].mean(NDCG), c.name))
    ba = arrays[best.name]
    tied = []
    for c in configs:
        if c.name == best.name:
            continue
        res = stats.compare(ba, arrays[c.name], iterations=BOOT_HEADLINE,
                            seed=77 + len(c.name))
        if res.p_value >= 0.05:
            tied.append((c.name, res.mean_diff, res.p_value))
    rep.para(
        f"The leading configuration is `{best.name}` at "
        f"nDCG@{K} {num(r.runs[best.name].mean(NDCG))}. "
        f"{len(tied)} other configurations are statistically indistinguishable "
        f"from it on this corpus. Choosing between those on score is choosing "
        f"on noise; choose on cost, on `misleading@1`, or on operational "
        f"preference."
    )
    rep.bullets([
        "**Measure the chunker before tuning the retriever.** The reachability "
        "column is computable from the chunking and the labels alone, with no "
        "retrieval, and it bounds everything downstream. If it is low, nothing "
        "else matters.",
        "**Do not chase reachability.** The chunker with perfect reachability "
        "is the worst configuration in the grid. Reachability is a ceiling, "
        "not an objective.",
        "**Label at span level or do not claim to compare chunkers.** "
        "Document-level labels reduced the chunker spread to "
        f"{num(_doc_spread(r))} nDCG and reordered them.",
        "**Report the minimum detectable effect first.** Most of this grid is "
        "inside it.",
        "**Count harm separately.** Two configurations indistinguishable on "
        "nDCG differ by an entire failure mode on `misleading@1`.",
        "**Repeat the heading path into every chunk.** It is a few lines of "
        "code, it costs nothing measurable, and it eliminates chunks that hold "
        "a value without the qualifier that scopes it.",
    ])


def _doc_spread(r: LabResults) -> float:
    doc_scores = {}
    for cname in CHUNKERS:
        rt = best_retriever(r, cname)
        doc_scores[cname] = float(r.by_config(cname, rt, "none").ndcg_doclevel.mean())
    return max(doc_scores.values()) - min(doc_scores.values())


def build_report(r: LabResults) -> str:
    rep = Report("Retrieval Quality Lab: where the answer went")
    rep.para(
        "\"Our RAG isn't working\" is not one problem. It is at least three, "
        "they are separable, and the separation is computable in advance. This "
        "report separates them on a corpus whose relevance labels are exact by "
        "construction, and then examines whether the differences it finds are "
        "real."
    )
    rep.para(
        f"Grid: {len(CHUNKERS)} chunkers x {len(RETRIEVERS)} retrievers x "
        f"{len(RERANKERS)} rerankers = {len(r.runs)} configurations, each run "
        f"on the same {len(QUERIES)} queries, cut at k={K} from a candidate "
        f"pool of {POOL_DEPTH}. Every configuration is deterministic and this "
        f"document is byte-reproducible."
    )
    section_corpus(rep, r)
    section_ceilings(rep, r)
    section_labels(rep, r)
    section_significance(rep, r)
    section_power(rep, r)
    section_optimism(rep, r)
    section_classes(rep, r)
    section_rerank(rep, r)
    section_harm(rep, r)
    section_pareto(rep, r)
    section_guide(rep, r)
    return rep.render()


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="docs/results.md")
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args(argv)

    r = run_grid(verbose=not args.quiet)
    text = build_report(r)
    out = pathlib.Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(text, encoding="utf-8", newline="\n")
    if not args.quiet:
        print(f"wrote {out} ({len(text.splitlines())} lines)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
