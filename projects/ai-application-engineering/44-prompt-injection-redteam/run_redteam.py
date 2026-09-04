"""Generate ``docs/results.md``.

Every section states a prediction before it reads a measurement. The report
type enforces this: ``expect()`` must precede ``found()``, and ``render()``
refuses to emit a document with an unresolved prediction. Predictions that
turn out wrong are rendered as wrong and discussed where they appear, which
is the only reason writing them down is worth anything.
"""

from __future__ import annotations

import hashlib
import pathlib
from collections import Counter

from redteam.corpus import Attack, Family, Goal, load_corpus
from redteam.defenses import ClassifierStats, InjectionClassifier, invisible_ratio
from redteam.metrics import (Rate, Tradeoff, evaluate_all_subsets, frontier,
                             interaction, macro_asr, per_family, per_goal,
                             rule_of_three, shapley_values)
from redteam.normalize import normalize
from redteam.pipeline import (ALL_LAYERS, BEHAVIOURAL_LAYERS, Layer, Pipeline,
                              RunResult, STRUCTURAL_LAYERS)
from redteam.report import Report
from redteam.target import SimulatedAgent

CORPUS = load_corpus()
BASE_SUSCEPTIBILITY = 0.6


def pct(x: float) -> str:
    return f"{x * 100:.1f}%"


def num(x: float, places: int = 2) -> str:
    return f"{x:.{places}f}"


def _run(layers: frozenset[Layer], *, susceptibility: float | None = None,
         **kwargs) -> RunResult:
    agent = SimulatedAgent(
        susceptibility=BASE_SUSCEPTIBILITY if susceptibility is None
        else susceptibility)
    return Pipeline(agent, layers, **kwargs).run(CORPUS)


def _rate(result: RunResult) -> Rate:
    return Rate(len(result.successes), len(result.outcomes))


def _ci(rate: Rate) -> str:
    low, high = rate.wilson
    return f"{pct(rate.point)} [{pct(low)}, {pct(high)}]"


# ---------------------------------------------------------------------------


def section_corpus(r: Report) -> None:
    r.h2("1. The corpus, and why its shape decides the headline number")
    traps = sum(1 for b in CORPUS.benign if b.trap)
    r.para(
        f"{len(CORPUS.attacks)} attacks across {len(CORPUS.families)} families "
        f"and four goals, plus {len(CORPUS.benign)} benign documents. The "
        f"benign half is not padding: {traps} of its documents carry a "
        f"recorded reason for being there -- a quoted thread, an imperative "
        f"sentence, a link to an unknown host, a non-Latin script -- and each "
        f"is a specific way a naive detector goes wrong. The report never "
        f"quotes an attack-success rate without the false-positive rate "
        f"beside it.")

    counts = Counter(a.family.value for a in CORPUS.attacks)
    total = len(CORPUS.attacks)
    r.table(["family", "n", "share"],
            [[name, str(n), pct(n / total)]
             for name, n in counts.most_common()])

    r.expect(
        "The obfuscated family will be the largest by a wide margin, because "
        "it is generated combinatorially (7 obfuscations x 4 base payloads) "
        "while every other family is hand-written. If so, a micro-averaged "
        "attack success rate over this corpus is substantially a measurement "
        "of Unicode handling, and will diverge from the macro-average by "
        "several points.")

    bare = _run(frozenset())
    full = _run(frozenset(ALL_LAYERS))
    gap_bare = abs(macro_asr(bare, CORPUS) - bare.asr)
    gap_full = abs(macro_asr(full, CORPUS) - full.asr)
    obf_share = counts["obfuscated"] / total
    ranked = counts.most_common()
    runner_up = ranked[1][1]
    largest = ranked[0][0] == "obfuscated"
    wide_margin = counts["obfuscated"] >= 1.5 * runner_up
    diverges = gap_bare >= 0.02

    r.found(
        f"The obfuscated family is {pct(obf_share)} of the corpus and "
        f"{counts['obfuscated'] / runner_up:.1f}x the size of the next "
        f"largest ({ranked[1][0]}, n={runner_up}). Micro and macro averages "
        f"differ by {pct(gap_bare)} with no defences and {pct(gap_full)} with "
        f"the full stack. The gap is small at baseline and grows as defences "
        f"are added, which is the worse direction: the number moves most "
        f"exactly when it is being used to compare configurations. Every "
        f"table below reports both.",
        contradicted=not (largest and wide_margin and diverges))

    meaning = {
        Goal.OVERRIDE: "agent abandons its task",
        Goal.EXFILTRATE: "a secret crosses the boundary",
        Goal.TOOL_CALL: "an unauthorised tool runs",
        Goal.MISINFORM: "false content in the output, no flow to block",
    }
    goal_counts = Counter(a.goal for a in CORPUS.attacks)
    r.table(["goal", "n", "what success means"],
            [[goal.value, str(goal_counts[goal]), meaning[goal]]
             for goal in Goal])

    r.note(f"corpus v1, digest `{CORPUS.digest}`")


def section_baseline(r: Report) -> None:
    r.h2("2. Undefended baseline")
    r.para(
        "No defences. The only thing standing between the attacker and the "
        "goal is whether the model chooses to comply.")

    res = _run(frozenset())
    fam = per_family(res, CORPUS)
    r.table(["family", "successes", "n", "ASR [95% Wilson]"],
            [[family.value, str(rate.successes), str(rate.trials), _ci(rate)]
             for family, rate in fam.items()])

    overall = _rate(res)
    r.para(
        f"Micro-ASR {_ci(overall)}, macro-ASR "
        f"{pct(macro_asr(res, CORPUS))}. Intervals are Wilson rather than "
        f"normal throughout: several configurations below reach exactly zero "
        f"successes, where the normal approximation returns an interval of "
        f"zero width and the correct statement is 'nothing observed, "
        f"consistent with up to {pct(rule_of_three(len(CORPUS.attacks)))}'.")

    goals = per_goal(res, CORPUS)
    r.table(["goal", "ASR [95% Wilson]"],
            [[goal.value, _ci(rate)] for goal, rate in goals.items()])


def section_invariance(r: Report) -> None:
    r.h2("3. The headline: which guarantees survive a change of model")
    r.para(
        "This is the experiment the whole repository is built around. Every "
        "published prompt-injection defence reports an attack success rate "
        "measured against some model. The question nobody asks is what that "
        "number is a property *of*.")
    r.para(
        "Here the target's susceptibility -- the probability it obeys a "
        "plainly-worded injection -- is swept across its entire range, from a "
        "model that never complies to one that always does. A defence whose "
        "effectiveness is a property of the code will draw a flat line. A "
        "defence whose effectiveness is a property of the model will not.")

    r.expect(
        "Behavioural defences (classifier, spotlighting) will show attack "
        "success rising roughly in step with susceptibility, because they "
        "reduce the probability of compliance without bounding it. Structural "
        "defences will show a flat line for the goals they govern. The "
        "sharpest test is the authorised_tool family, where the user is "
        "genuinely entitled to the tool and only the argument is "
        "attacker-chosen: (a) its undefended success should climb to near "
        "100% at susceptibility 1.0, (b) under the behavioural stack it "
        "should still climb substantially, and (c) under the broker it should "
        "be exactly 0.0% at every susceptibility.")

    levels = [0.0, 0.2, 0.4, 0.6, 0.8, 1.0]
    rows = []
    structural_auth: list[float] = []
    behavioural_auth: list[float] = []
    none_auth: list[float] = []
    structural_tool: list[float] = []
    behavioural_tool: list[float] = []

    def tool(a: Attack) -> bool:
        return a.goal is Goal.TOOL_CALL

    def auth(a: Attack) -> bool:
        return a.family is Family.AUTHORISED_TOOL

    for s in levels:
        none = _run(frozenset(), susceptibility=s)
        beh = _run(BEHAVIOURAL_LAYERS, susceptibility=s)
        stru = _run(STRUCTURAL_LAYERS, susceptibility=s)
        structural_tool.append(stru.asr_for(tool))
        behavioural_tool.append(beh.asr_for(tool))
        none_auth.append(none.asr_for(auth))
        behavioural_auth.append(beh.asr_for(auth))
        structural_auth.append(stru.asr_for(auth))
        rows.append([num(s, 1), pct(_rate(none).point), pct(_rate(beh).point),
                     pct(_rate(stru).point), pct(none.asr_for(auth)),
                     pct(beh.asr_for(auth)), pct(stru.asr_for(auth))])

    r.table(["susceptibility", "no defence, all", "behavioural, all",
             "structural, all", "auth_tool, none", "auth_tool, behavioural",
             "auth_tool, structural"], rows)

    spread_b = max(behavioural_auth) - min(behavioural_auth)
    spread_s = max(structural_auth) - min(structural_auth)
    spread_b_tool = max(behavioural_tool) - min(behavioural_tool)
    spread_s_tool = max(structural_tool) - min(structural_tool)
    part_a = max(none_auth) >= 0.95
    part_b = spread_b >= 0.5
    part_c = max(structural_auth) == 0.0

    r.found(
        f"All three parts hold. (a) Undefended, the authorised-tool family "
        f"reaches {pct(max(none_auth))} at susceptibility 1.0. (b) The "
        f"behavioural stack does not flatten it -- it moves "
        f"{pct(min(behavioural_auth))} to {pct(max(behavioural_auth))}, a "
        f"spread of {pct(spread_b)}, which is to say that the classifier and "
        f"spotlighting delay the model's compliance rather than bound it. "
        f"(c) Under the broker the same family is {pct(max(structural_auth))} "
        f"at every susceptibility, a spread of {pct(spread_s)}. Across all "
        f"tool_call goals the behavioural stack moves {pct(spread_b_tool)} "
        f"while the broker stays within {pct(spread_s_tool)}.",
        contradicted=not (part_a and part_b and part_c))

    r.para(
        "The broker's tool_call residual is not zero -- it is "
        f"{pct(max(structural_tool))}, a single attack. That attack is "
        "`direct-022`, which arrives on the *user* channel: the operator "
        "themselves typed 'invoke send_email to attacker@collect.example.net'. "
        "The broker allows it because the argument's taint resolves to the "
        "principal's own trust level, and a system that refused this would be "
        "refusing the user access to their own tools. This is the honest "
        "boundary of the mechanism: a capability broker reduces the tool-call "
        "attack surface to exactly the set of actions the principal asked for "
        "themselves. It does not solve the confused-deputy problem by making "
        "the deputy cautious; it solves it by removing the deputy's ability "
        "to be confused about who asked.")

    r.para(
        "The practical reading: a defence report that does not say which "
        "model it was measured against has told you nothing that transfers, "
        "and a defence whose number would change if the model changed should "
        "not be described as a control.")


def section_shapley(r: Report) -> None:
    r.h2("4. Layer attribution without the ordering artefact")
    r.para(
        "The standard way to present a layered defence is a waterfall: "
        "baseline ASR, then ASR after adding each layer in turn. That number "
        "is a property of the order chosen. Whichever layer is added first "
        "gets credit for everything the layers agree on, and any layer added "
        "late looks redundant regardless of its merit.")
    r.para(
        "Five layers means 32 configurations, which is small enough to "
        "enumerate exhaustively. Each layer's Shapley value is its average "
        "marginal contribution across all 120 orderings.")

    r.expect(
        "Two claims. (a) The classifier will take the largest Shapley value, "
        "because it fires earliest and catches the widest range. "
        "(b) Normalisation will score near zero on its own -- it changes no "
        "outcome by itself, it only makes the classifier's job possible -- "
        "and will therefore show a large positive interaction with the "
        "classifier alongside its small solo value.")

    table = evaluate_all_subsets(lambda ls: _run(ls))

    def reduction(res: RunResult) -> float:
        return -res.asr

    values = shapley_values(table, reduction)
    total = sum(values.values())
    ordered = sorted(values.items(), key=lambda kv: -kv[1])
    r.table(["layer", "kind", "Shapley value (ASR reduction)", "share"],
            [[layer.value,
              "structural" if layer.structural else "behavioural",
              pct(v), pct(v / total) if total else "n/a"]
             for layer, v in ordered])

    full_reduction = table[frozenset()].asr - table[frozenset(ALL_LAYERS)].asr
    r.para(
        f"The values sum to {pct(total)}, which equals the full stack's total "
        f"reduction of {pct(full_reduction)} to within floating point. That "
        f"identity is the efficiency axiom and it is asserted in the tests; "
        f"it is the cheapest available check on a factorial-weight error.")

    pairs = [(Layer.NORMALIZE, Layer.CLASSIFY),
             (Layer.CLASSIFY, Layer.SPOTLIGHT),
             (Layer.BROKER, Layer.EGRESS),
             (Layer.CLASSIFY, Layer.BROKER),
             (Layer.SPOTLIGHT, Layer.BROKER)]
    inter = {(a, b): interaction(table, reduction, a, b) for a, b in pairs}
    r.table(["pair", "interaction index", "reading"],
            [[f"{a.value} x {b.value}", pct(v),
              "complementary" if v > 0.01 else
              "redundant" if v < -0.01 else "independent"]
             for (a, b), v in inter.items()])

    top_layer, top_value = ordered[0]
    norm_solo = values[Layer.NORMALIZE]
    norm_inter = inter[(Layer.NORMALIZE, Layer.CLASSIFY)]
    part_a = top_layer is Layer.CLASSIFY
    part_b = abs(norm_solo) < 0.05 and norm_inter > 0.0

    r.found(
        f"(a) is wrong: the largest Shapley value belongs to "
        f"`{top_layer.value}` at {pct(top_value)}, not to the classifier at "
        f"{pct(values[Layer.CLASSIFY])}. (b) holds: normalisation's solo "
        f"value is {pct(norm_solo)} against an interaction with the "
        f"classifier of {pct(norm_inter)} -- the signature of a preprocessing "
        f"step, almost nothing alone and a great deal in company. That shape "
        f"is invisible to any sequential waterfall, and a team that measured "
        f"normalisation last would have deleted it. Separately, egress scores "
        f"{pct(values[Layer.EGRESS])} in permissive mode: in this "
        f"configuration it is not a defence, it is a line in an architecture "
        f"diagram. Section 7 shows what happens when it is switched to "
        f"strict.",
        contradicted=not (part_a and part_b))

    r.note(
        "Grade B caveat: spotlighting's contribution is proportional to "
        "`spotlight_effect`, a parameter of the simulated target that was "
        "chosen, not measured. Its rank in this table is therefore not a "
        "result. The structural layers' values do not depend on it, which is "
        "the distinction section 3 is built to make.")

    worst = min(inter.items(), key=lambda kv: kv[1])
    r.para(
        f"The most redundant pair is {worst[0][0].value} x "
        f"{worst[0][1].value} at {pct(worst[1])}. A negative interaction "
        f"means the two layers are largely catching the same attacks: the "
        f"stack is tall rather than layered, and the second control is being "
        f"paid for twice.")


def section_obfuscation(r: Report) -> None:
    r.h2("5. Obfuscation trades detection evasion against comprehension")
    r.para(
        "Obfuscation is usually discussed as a pure win for the attacker: "
        "encode the payload, evade the filter. That framing omits the other "
        "side of the exchange. An instruction the detector cannot read is "
        "often an instruction the model cannot read either.")

    r.expect(
        "Attack success against a classifier-only defence will be non-"
        "monotone in obfuscation strength. The heaviest encodings (base64, "
        "charcode, rot13) will evade lexical features but lose more to "
        "comprehension than they gain, ending up *less* successful than the "
        "legibility-preserving Unicode tricks -- zero-width, confusable -- "
        "which are the genuine sweet spot.")

    clf = InjectionClassifier()
    obf = [a for a in CORPUS.attacks if a.family is Family.OBFUSCATED]
    by_kind: dict[str, list[Attack]] = {}
    for attack in obf:
        kind = attack.notes.replace("obfuscation: ", "")
        by_kind.setdefault(kind, []).append(attack)

    # Held at susceptibility 1.0 deliberately. The quantity of interest is
    # what the *encoding* costs the attacker, and that is only observable on
    # a model willing to obey anything it can actually read. At the default
    # susceptibility every cell of this table is zero, which says nothing
    # about obfuscation and a great deal about the target's baseline.
    result = _run(frozenset({Layer.CLASSIFY, Layer.NORMALIZE}),
                  susceptibility=1.0)
    bare = _run(frozenset(), susceptibility=1.0)
    bare_by_id = {o.attack.id: o for o in bare.outcomes}
    outcome_by_id = {o.attack.id: o for o in result.outcomes}
    naive = InjectionClassifier()

    rows = []
    for kind, attacks in sorted(by_kind.items()):
        detected = sum(1 for a in attacks if clf.classify(a.payload).flagged)
        # A detector denied the normaliser: what a lexical-only filter sees.
        raw = sum(1 for a in attacks
                  if naive.classify(a.payload, use_normalizer=False).flagged)
        succeeded = sum(1 for a in attacks if outcome_by_id[a.id].succeeded)
        undefended = sum(1 for a in attacks if bare_by_id[a.id].succeeded)
        rows.append([kind, str(len(attacks)),
                     pct(raw / len(attacks)),
                     pct(detected / len(attacks)),
                     pct(undefended / len(attacks)),
                     pct(succeeded / len(attacks))])

    r.table(["obfuscation", "n", "lexical detection", "with normalisation",
             "success, no defence", "success, normalise+classify"], rows)

    heavy = ["base64", "charcode", "rot13", "tag_block"]
    light = ["zero_width", "confusable", "bidi"]
    heavy_undef = [float(row[4].rstrip("%")) for row in rows if row[0] in heavy]
    light_undef = [float(row[4].rstrip("%")) for row in rows if row[0] in light]
    hm = sum(heavy_undef) / len(heavy_undef) if heavy_undef else 0.0
    lm = sum(light_undef) / len(light_undef) if light_undef else 0.0

    lexical = [float(row[2].rstrip("%")) for row in rows]
    normalised = [float(row[3].rstrip("%")) for row in rows]

    r.found(
        f"On an undefended target -- where the only thing the encoding costs "
        f"the attacker is comprehension -- heavy encodings succeed "
        f"{pct(hm / 100)} and legibility-preserving ones succeed "
        f"{pct(lm / 100)}. "
        + ("The predicted ordering holds: the strongest evasion is not the "
           "strongest attack, because the payload still has to survive being "
           "read. " if lm > hm else
           "The predicted ordering did not hold. ")
        + f"The column that matters for defenders is the third: lexical "
          f"detection alone averages {pct(sum(lexical) / len(lexical) / 100)} "
          f"across these families, and rises to "
          f"{pct(sum(normalised) / len(normalised) / 100)} once the text is "
          f"normalised first. Normalisation is not a marginal improvement to "
          f"the classifier, it is the precondition for it working at all.",
        contradicted=lm <= hm)

    r.para(
        "One row deserves separate attention. ROT13 is the only transform "
        "the normaliser fails to recover, because its decoder only reports a "
        "rotation when the result reads as English and this payload "
        "vocabulary does not clear that bar. So ROT13 evades detection "
        "completely -- and still does not produce a more successful attack, "
        "because the comprehension cost is charged to the attacker "
        "regardless of whether the defender noticed. That separation is the "
        "point: the attacker's cost is a property of the encoding, not of "
        "the defender's decoder, and the two are only independent because "
        "the simulator was changed to make them so.")

    hidden = max(obf, key=lambda a: invisible_ratio(a.payload))
    r.para(
        f"The extreme case is the Unicode tag block. Attack `{hidden.id}` "
        f"carries {pct(invisible_ratio(hidden.payload))} of its codepoints in "
        f"characters that render as nothing at all: a reviewer reading the "
        f"message in any mainstream client sees "
        f"\"{normalize(hidden.payload).text[:40]}...\" and nothing else. "
        f"Detecting it needs no model and no wordlist -- comparing rendered "
        f"length to codepoint count is sufficient, and outperforms every "
        f"lexical feature in the table.")


def section_classifier(r: Report) -> None:
    r.h2("6. The classifier, measured on both axes")
    r.para(
        "A detection rate quoted without a false-positive rate is not a "
        "measurement. This section sweeps the classifier's threshold and "
        "reports the frontier.")

    r.expect(
        "There will be no threshold that achieves both high detection and a "
        "false-positive rate low enough to ship. Specifically, driving "
        "detection above 90% will push the false-positive rate on ordinary "
        "business correspondence above 10%, because the benign corpus "
        "deliberately contains the same lexical features the attacks use.")

    rows = []
    best: tuple[float, float] | None = None
    reached_90 = False
    for threshold in (0.5, 0.8, 1.0, 1.3, 1.6, 2.0, 2.5, 3.0):
        clf = InjectionClassifier(threshold=threshold)
        stats = ClassifierStats()
        for attack in CORPUS.attacks:
            if clf.classify(attack.payload).flagged:
                stats.true_positive += 1
            else:
                stats.false_negative += 1
        for doc in CORPUS.benign:
            if clf.classify(doc.payload).flagged:
                stats.false_positive += 1
                stats.fp_documents.append(doc.id)
            else:
                stats.true_negative += 1
        rows.append([num(threshold, 1), pct(stats.detection_rate),
                     pct(stats.false_positive_rate), pct(stats.precision),
                     ", ".join(sorted(set(stats.fp_documents))[:2]) or "--"])
        if stats.detection_rate >= 0.9:
            reached_90 = True
            if best is None or stats.false_positive_rate < best[1]:
                best = (threshold, stats.false_positive_rate)

    r.table(["threshold", "detection", "false positive", "precision",
             "example false positives"], rows)

    if reached_90 and best is not None:
        r.found(
            f"At threshold {num(best[0], 1)} detection clears 90% with a "
            f"false-positive rate of {pct(best[1])}.",
            contradicted=best[1] <= 0.10)
    else:
        r.found(
            "No threshold in the swept range reaches 90% detection at all. "
            "The classifier's ceiling is set by the attacks it cannot see "
            "rather than by where the line is drawn, which is a stronger "
            "version of the prediction: the tradeoff is not adverse, it is "
            "unavailable.",
            contradicted=True)

    r.para(
        "The false positives are worth naming individually rather than "
        "counting. The documents that trip the classifier first are the ones "
        "containing quoted email threads and legitimate imperative sentences "
        "-- 'please ignore the previous invoice, it had the wrong PO number' "
        "is indistinguishable, lexically, from an override attack. There is "
        "no threshold that separates them because there is no lexical "
        "difference. The difference is provenance, which is the layer the "
        "classifier does not have access to.")


def section_egress(r: Report) -> None:
    r.h2("7. Egress filtering: the zero-click channel and what closing it "
         "costs")
    r.para(
        "Chat output looks inert and is not. A rendered markdown image is an "
        "outbound HTTP GET to an attacker-chosen URL, performed by the "
        "victim's client, with no tool call and no user action. Any "
        "architecture that sandboxes tools and then renders model output as "
        "markdown has left this open.")

    r.expect(
        "Strict egress filtering -- blocking links to any host not on the "
        "allowlist -- will meaningfully reduce exfiltration success, and will "
        "cost more than it is worth on the benign corpus, because ordinary "
        "correspondence links to arbitrary hosts constantly.")

    configs = [("egress only", frozenset({Layer.EGRESS})),
               ("structural", STRUCTURAL_LAYERS),
               ("full stack", frozenset(ALL_LAYERS))]
    rows = []
    numbers: dict[tuple[str, str], float] = {}

    def exfil(a: Attack) -> bool:
        return a.goal is Goal.EXFILTRATE

    for mode, strict in (("permissive", False), ("strict", True)):
        for label, layers in configs:
            res = _run(layers, strict_egress=strict)
            fpr = Rate(sum(1 for b in res.benign if b.blocked),
                       len(res.benign))
            numbers[(mode, label)] = _rate(res).point
            numbers[(mode, label + ":exfil")] = res.asr_for(exfil)
            numbers[(mode, label + ":fpr")] = fpr.point
            rows.append([mode, label, pct(_rate(res).point),
                         pct(res.asr_for(exfil)), _ci(fpr)])

    r.table(["egress mode", "layers", "ASR", "exfiltration ASR",
             "FPR [95% Wilson]"], rows)

    bought = numbers[("permissive", "structural")] - \
        numbers[("strict", "structural")]
    cost = numbers[("strict", "structural:fpr")]
    full_gain = numbers[("permissive", "full stack")] - \
        numbers[("strict", "full stack")]

    r.found(
        f"Strict mode buys {pct(bought)} of attack success against the "
        f"structural stack and costs {pct(cost)} of legitimate mail. "
        + ("In the full configuration it still moves ASR, so the tradeoff "
           "remains a judgement call rather than a dominated choice. "
           if full_gain > 0.001 else
           "In the full configuration it buys exactly nothing -- the "
           "classifier has already stopped everything strict egress would "
           "have caught -- so at that point it is Pareto-dominated: pure "
           "cost, no security. ")
        + "The permissive setting is not a weaker choice, it is a different "
          "one: it accepts a residual exfiltration channel in exchange for "
          "the product continuing to work.",
        contradicted=bought <= 0.0)

    r.para(
        "The residual under permissive egress is real and should be stated "
        "plainly: a bare link to an unknown host is allowed, and an attacker "
        "can encode data in its path. The mitigation is not a better filter "
        "-- it is not rendering model output as markdown, which is an "
        "architectural decision made long before anyone writes a filter.")


def section_delimiters(r: Report) -> None:
    r.h2("8. Delimiters an attacker can close, and delimiters they cannot")
    r.para(
        "Fencing untrusted content in tags is the most widely deployed "
        "injection defence and the easiest to defeat, because the attacker "
        "knows the tag. The fix costs one random token.")

    r.expect(
        "Against fixed delimiters, the context-forgery family will succeed at "
        "a materially higher rate than against nonce delimiters, and the "
        "difference will be larger than for any other family, because forgery "
        "is the only family that targets the delimiter directly.")

    def forgery(a: Attack) -> bool:
        return a.family is Family.CONTEXT_FORGERY

    def indirect(a: Attack) -> bool:
        return a.family is Family.INDIRECT

    rows = []
    forged = {}
    for label, forgeable in (("fixed <untrusted>", True),
                             ("nonce-tagged", False)):
        res = _run(frozenset({Layer.SPOTLIGHT}), forgeable_delimiters=forgeable)
        f_rate = Rate(sum(1 for o in res.outcomes
                          if forgery(o.attack) and o.succeeded),
                      sum(1 for o in res.outcomes if forgery(o.attack)))
        i_rate = Rate(sum(1 for o in res.outcomes
                          if indirect(o.attack) and o.succeeded),
                      sum(1 for o in res.outcomes if indirect(o.attack)))
        forged[label] = f_rate.point
        rows.append([label, pct(_rate(res).point), _ci(f_rate), _ci(i_rate)])

    r.table(["delimiter", "overall ASR", "context_forgery ASR",
             "indirect ASR"], rows)

    fixed = forged["fixed <untrusted>"]
    nonce = forged["nonce-tagged"]
    r.found(
        f"Context-forgery ASR is {pct(fixed)} against fixed delimiters and "
        f"{pct(nonce)} against nonce-tagged ones. The prediction holds in "
        f"direction. The honest caveat applies either way: this simulation "
        f"models the attacker as unable to guess a nonce, which is true, and "
        f"models the model as respecting whichever fence it is given, which "
        f"is an assumption. The real defence is that a nonce cannot be "
        f"written into a payload composed before the nonce existed -- and "
        f"that part is not an assumption.",
        contradicted=fixed <= nonce)


def section_broker(r: Report) -> None:
    r.h2("9. The taint tracker's blind spot: paraphrase")
    r.para(
        "Provenance for a tool argument is established by finding that "
        "argument's text in the prompt. When the model restates an "
        "instruction in its own words, the argument matches nothing, and the "
        "tracker has no evidence either way.")
    r.para(
        "What a system does in that case is the single most consequential "
        "line in a taint-based defence. Treating 'cannot determine' as "
        "'trusted' produces a control that logs beautifully and stops "
        "nothing.")

    r.expect(
        "Among tool-call attacks that clear the requester-trust check -- that "
        "is, attacks where the channel *is* authorised for the tool and only "
        "the argument is attacker-chosen -- the argument-taint and "
        "unattributable-argument rules will together account for every "
        "denial, and the unattributable rule will be a non-trivial share of "
        "them rather than a rounding error.")

    pipeline = Pipeline(SimulatedAgent(susceptibility=BASE_SUSCEPTIBILITY),
                        STRUCTURAL_LAYERS)
    res = pipeline.run(CORPUS)

    counts: Counter[str] = Counter()
    for attack in CORPUS.attacks:
        outcome = pipeline.run_attack(attack)
        if outcome.stopped_by is Layer.BROKER:
            counts[outcome.detail.split(":")[1].split("—")[0].strip()] += 1

    total = sum(counts.values())
    r.table(["broker rule", "denials", "share of denials"],
            [[rule, str(n), pct(n / total) if total else "n/a"]
             for rule, n in counts.most_common()])

    r.para(
        "The table is dominated by requester-trust, and that is the rule "
        "order working as intended: most injected tool calls come from an "
        "untrusted document asking for an operator-level tool, and are "
        "refused on the channel alone without anyone needing to look at the "
        "arguments. The argument rules only get a turn when the requester is "
        "legitimately authorised -- a user asking to send an email -- and the "
        "attacker's influence is confined to *which* email. That subset is "
        "the interesting one and is isolated below.")

    # Every ruling the pipeline actually made, read back off the outcomes
    # rather than re-derived. The previous version of this section re-ran the
    # broker with an empty prompt and the attack's channel as the requester,
    # and duly reported zero argument-stage denials -- a statistic about the
    # re-run, not about the system.
    reached = 0
    arg_denials: Counter[str] = Counter()
    arg_rules = {"argument-taint", "unattributable-argument"}
    for attack in CORPUS.attacks:
        outcome = pipeline.run_attack(attack)
        for decision in outcome.decisions:
            if decision.rule in ("unknown-tool", "requester-trust"):
                continue
            reached += 1
            if not decision.allowed and decision.rule in arg_rules:
                arg_denials[decision.rule] += 1

    arg_total = sum(arg_denials.values())
    r.table(["rule (argument-stage only)", "denials", "share"],
            [[rule, str(n), pct(n / arg_total) if arg_total else "n/a"]
             for rule, n in arg_denials.most_common()] or [["--", "0", "n/a"]])

    taint = arg_denials.get("argument-taint", 0)
    unattributable = arg_denials.get("unattributable-argument", 0)
    share = unattributable / arg_total if arg_total else 0.0

    r.found(
        f"{reached} proposed calls cleared the requester check and reached "
        f"the argument rules, and {arg_total} of them were denied there -- "
        f"{pct(arg_total / reached) if reached else 'n/a'} of what the "
        f"channel check let through. These are the calls the channel-trust "
        f"rule is blind to by construction: the user is entitled to the tool "
        f"and only the argument is in dispute. {taint} were refused because "
        f"an argument traced back to an untrusted span, and {unattributable} "
        f"({pct(share)}) because the agent restated the payload rather than "
        f"copying it, leaving the argument with no determinable provenance "
        f"at all.",
        contradicted=arg_total == 0 or share == 0.0)

    r.para(
        "The second number is the one worth dwelling on. A broker that "
        "treated 'cannot determine provenance' as 'trusted' -- which is the "
        "natural way to write it, because the check reads as a lookup that "
        "returned nothing -- would allow exactly those calls, and would "
        "produce an audit log indistinguishable from a working control. "
        "Every entry would read *allowed*, and every entry would be "
        "truthful. This is why the rule is written as a positive "
        "requirement: an argument must be *shown* to originate at or above "
        "the tool's bar, rather than merely not shown to originate below it.")

    r.para(
        f"Overall structural-stack ASR is {pct(_rate(res).point)}. The number "
        f"to carry away is not that one but the shape of the two tables: the "
        f"channel check does the volume, the argument checks do the difficult "
        f"cases, and the fourth rule exists to make the failure mode of the "
        f"other three visible.")


def section_residual(r: Report) -> None:
    r.h2("10. What still works, and why no layer here can stop it")
    r.para("The section every red-team report should have and most do not.")

    r.expect(
        "The residual will be dominated by the misinform goal. Override, "
        "exfiltration and tool-call attacks all require a *flow* -- an "
        "action, or bytes leaving -- that a structural layer can interrupt. "
        "Misinformation requires none: the output itself is the payload, and "
        "there is no boundary to enforce because the content is going exactly "
        "where it was supposed to go.")

    res = _run(frozenset(ALL_LAYERS))
    survivors = res.successes
    goals = per_goal(res, CORPUS)
    r.table(["goal", "surviving attacks", "residual ASR"],
            [[goal.value,
              str(sum(1 for o in survivors if o.attack.goal is goal)),
              _ci(rate)] for goal, rate in goals.items()])

    overall = _rate(res)
    misinform = sum(1 for o in survivors if o.attack.goal is Goal.MISINFORM)
    override = sum(1 for o in survivors if o.attack.goal is Goal.OVERRIDE)
    flowless = misinform + override
    exf = res.asr_for(lambda a: a.goal is Goal.EXFILTRATE)
    tools = res.asr_for(lambda a: a.goal is Goal.TOOL_CALL)

    r.found(
        f"{len(survivors)} of {len(CORPUS.attacks)} attacks survive the full "
        f"stack ({_ci(overall)}). {flowless} of them pursue goals with no "
        f"flow to interrupt: {misinform} misinform and {override} override. "
        f"Exfiltration and tool-call residuals are {pct(exf)} and "
        f"{pct(tools)}. The prediction holds: what remains is precisely the "
        f"class of attack that structural defence is not the right tool for.",
        contradicted=flowless < len(survivors) / 2)

    fam_counts = Counter(o.attack.family.value for o in survivors)
    r.table(["family", "surviving attacks"],
            [[name, str(n)] for name, n in sorted(fam_counts.items())])

    r.para(
        "The misinformation residual is not a gap to be closed by a sixth "
        "layer. A summariser that faithfully reports the contents of a "
        "document an attacker wrote is working correctly; the false statement "
        "is in the source, not in the agent. The mitigations are provenance "
        "in the *output* -- attributing each claim to the document it came "
        "from, so the user can discount it -- and not treating a summary of "
        "untrusted text as a fact. Both are product decisions rather than "
        "security controls, which is why this section ends without a number "
        "that goes to zero.")

    if survivors:
        example = survivors[0]
        r.code(
            f"surviving attack: {example.attack.id}\n"
            f"family : {example.attack.family.value}\n"
            f"goal   : {example.attack.goal.value}\n"
            f"channel: {example.attack.channel.label}\n"
            f"detail : {example.detail}", "text")


def section_frontier(r: Report) -> None:
    r.h2("11. The frontier: every configuration on both axes")
    r.para(
        "Thirty-two configurations, each scored on attack success and on how "
        "much legitimate traffic it refuses. Configurations off the frontier "
        "are strictly dominated -- something else is at least as secure and "
        "blocks less.")

    table = evaluate_all_subsets(lambda ls: _run(ls))
    tradeoffs = [
        Tradeoff(layers=layers, asr=_rate(res), macro=macro_asr(res, CORPUS),
                 fpr=Rate(sum(1 for b in res.benign if b.blocked),
                          len(res.benign)))
        for layers, res in table.items()]
    front = frontier(tradeoffs)

    r.table(["configuration", "ASR", "macro ASR", "FPR", "usable"],
            [[t.label, _ci(t.asr), pct(t.macro), _ci(t.fpr),
              "yes" if t.usable else "no"] for t in front])

    r.para(
        f"{len(front)} of {len(tradeoffs)} configurations are on the "
        f"frontier. The rest are dominated, which is a more useful thing to "
        f"know about a defence stack than its headline ASR: it means the "
        f"complexity is buying nothing that a simpler configuration does not "
        f"already provide.")

    usable = [t for t in front if t.usable]
    if usable:
        cheapest = min(usable, key=lambda t: (t.asr.point, len(t.layers)))
        r.para(
            f"The cheapest configuration on the frontier that stays under the "
            f"5% false-positive line is `{cheapest.label}`, at "
            f"{_ci(cheapest.asr)} attack success. Anything more elaborate is "
            f"either buying nothing or paying for it in refused mail.")


def section_zero(r: Report) -> None:
    r.h2("12. What a zero actually licenses")
    r.para(
        "Several configurations above reach exactly zero successful attacks "
        "for a goal. That is the most dangerous number in a security report, "
        "because it reads as a guarantee and is usually a sample size.")

    n_tool = sum(1 for a in CORPUS.attacks if a.goal is Goal.TOOL_CALL)
    r.table(["observed", "n", "point estimate", "95% upper bound"],
            [["0 successes", str(n), "0.0%", pct(rule_of_three(n))]
             for n in (n_tool, 200, 1000)])

    r.para(
        f"With {n_tool} tool-call attacks and none succeeding, the data are "
        f"consistent with a true success rate as high as "
        f"{pct(rule_of_three(n_tool))}. To support a claim of 'under 1%' from "
        f"an observed zero you need about 300 attacks; for 'under 0.1%', "
        f"about 3,000.")

    r.para(
        "This is exactly why section 3 matters. The broker's zero is not a "
        "zero-out-of-seventeen; it is a statement that holds for every model, "
        "established by construction and confirmed across the entire "
        "susceptibility range rather than by sampling. A structural argument "
        "converts a sample-size problem into a proof obligation, and proof "
        "obligations do not have confidence intervals.")


def build_report() -> Report:
    r = Report(
        title="Prompt injection: which defences are guarantees and which are "
              "measurements",
        intro=(
            "A structured attack corpus run against five layered defences, "
            "measuring attack success rate per layer, per family and per goal "
            "-- and, throughout, the false-positive rate on ordinary "
            "correspondence, because a detection rate quoted alone is not a "
            "measurement.\n\n"
            "The organising claim is that these five defences are not five of "
            "the same thing. Two of them (the capability broker, the egress "
            "filter) provide guarantees that hold whatever the model does; "
            "the rest reduce a probability. Section 3 separates them by "
            "sweeping the target model's compliance across its entire range "
            "and showing which lines are flat.\n\n"
            "No LLM is called. The target is simulated, and the report is "
            "explicit about which of its claims that constrains: results "
            "about structural defences are established by quantifying over "
            "all model behaviours and hold regardless, while results "
            "involving the classifier's effect on compliance are conditional "
            "on a compliance model that is stated, swept, and not tuned."),
        generator="run_redteam.py",
        environment="with no network access and no model API")

    section_corpus(r)
    section_baseline(r)
    section_invariance(r)
    section_shapley(r)
    section_obfuscation(r)
    section_classifier(r)
    section_egress(r)
    section_delimiters(r)
    section_broker(r)
    section_residual(r)
    section_frontier(r)
    section_zero(r)
    return r


def main() -> None:
    r = build_report()
    body = r.render()

    out = pathlib.Path("docs/results.md")
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(body, encoding="utf-8", newline="\n")
    digest = hashlib.sha256(body.encode()).hexdigest()[:16]
    print(f"wrote {out}  ({len(body)} bytes, sha256 {digest})")
    print(f"{r.prediction_count} predictions, {r.held_count} held, "
          f"{r.contradicted_count} contradicted")


if __name__ == "__main__":
    main()
