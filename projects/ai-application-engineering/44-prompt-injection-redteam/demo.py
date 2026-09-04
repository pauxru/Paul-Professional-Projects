"""The three results worth seeing, on the console, in about fifteen seconds.

Not a tutorial. Each block is an experiment with a claim attached, and the
claim is the kind you could act on in a design review.
"""

from redteam.corpus import Family, load_corpus
from redteam.metrics import Rate, evaluate_all_subsets, rule_of_three, shapley_values
from redteam.pipeline import ALL_LAYERS, Layer, Pipeline
from redteam.target import SimulatedAgent

CORPUS = load_corpus()
STRUCTURAL = frozenset(l for l in ALL_LAYERS if l.structural)
BEHAVIOURAL = frozenset(l for l in ALL_LAYERS if not l.structural)


def rule(title):
    print()
    print("=" * 74)
    print(title)
    print("=" * 74)


def asr(layers, susceptibility, predicate=None):
    run = Pipeline(SimulatedAgent(susceptibility=susceptibility),
                   layers).run(CORPUS)
    return run.asr if predicate is None else run.asr_for(predicate)


def demo_flat_line():
    rule("1. Which defences survive a change of model")
    print("""
The target's susceptibility -- how readily it obeys a plain injection -- is
swept end to end. A defence whose effectiveness is a property of the CODE
draws a flat line. A defence whose effectiveness is a property of the MODEL
does not. Shown for the authorised_tool family: a tool the user is already
entitled to use, where only the arguments are attacker-chosen.
""")
    authorised = lambda a: a.family is Family.AUTHORISED_TOOL
    print(f"  {'susceptibility':>15} | {'no defence':>11} | "
          f"{'behavioural':>12} | {'broker':>8}")
    print(f"  {'-' * 15} | {'-' * 11} | {'-' * 12} | {'-' * 8}")
    for s in (0.0, 0.25, 0.5, 0.75, 1.0):
        print(f"  {s:>15.2f} | "
              f"{asr(frozenset(), s, authorised):>10.1%} | "
              f"{asr(BEHAVIOURAL, s, authorised):>11.1%} | "
              f"{asr(frozenset({Layer.BROKER}), s, authorised):>7.1%}")
    print("""
  The behavioural column tracks the model: it works when the model was going
  to mostly behave anyway, and degrades exactly when you need it. The broker
  column is flat. That flatness is the guarantee, and it is visible without
  any argument about mechanisms.""")


def demo_attribution():
    rule("2. Layer attribution without the ordering artefact")
    print("""
Sequential attribution gives all shared credit to whichever layer is measured
first. Shapley values enumerate all 32 subsets and average each layer's
marginal contribution over every ordering.
""")
    table = evaluate_all_subsets(
        lambda layers: Pipeline(SimulatedAgent(susceptibility=0.6),
                                layers).run(CORPUS))
    values = shapley_values(table, lambda r: -r.asr)
    for layer, value in sorted(values.items(), key=lambda kv: -kv[1]):
        kind = "structural" if layer.structural else "behavioural"
        print(f"  {layer.value:>10}  {kind:<12} {value:>7.1%}")

    total = table[frozenset()].asr - table[frozenset(ALL_LAYERS)].asr
    print(f"\n  sum {sum(values.values()):.4f} == full-stack reduction "
          f"{total:.4f}   (efficiency axiom, asserted in the tests)")
    print("""
  normalize scores near zero alone. It is not useless -- it is a
  PRECONDITION. Its interaction with the classifier is larger than its solo
  value, a shape no sequential waterfall can show, and a team measuring it
  last would have deleted it.""")


def demo_zero():
    rule("3. What a zero actually licenses")
    print("""
Several configurations block every attack in a family. Reported as '0%' that
looks like a solved problem. It is a sample size.
""")
    full = Pipeline(SimulatedAgent(susceptibility=0.6),
                    frozenset(ALL_LAYERS)).run(CORPUS)
    for family in (Family.AUTHORISED_TOOL, Family.TOOL_CHAIN,
                   Family.OBFUSCATED):
        members = [o for o in full.outcomes if o.attack.family is family]
        rate = Rate(sum(1 for o in members if o.succeeded), len(members))
        note = ""
        if rate.point == 0.0:
            note = f"   <- 0/{rate.trials}, rule of three: <{rule_of_three(rate.trials):.1%}"
        print(f"  {family.value:>16}  {rate}{note}")
    print("""
  No table in the report prints a bare 0.0% without its interval. A zero over
  6 trials and a zero over 6000 are different claims, and only one of them is
  evidence.""")


def demo_residual():
    rule("4. What still works, and why no layer here can stop it")
    full = Pipeline(SimulatedAgent(susceptibility=0.6),
                    frozenset(ALL_LAYERS)).run(CORPUS)
    survivors = full.successes
    print(f"""
{len(survivors)} of {len(CORPUS.attacks)} attacks survive the full stack.
""")
    for outcome in survivors[:6]:
        print(f"  {outcome.attack.id:<26} {outcome.attack.goal.value:<12} "
              f"{outcome.attack.family.value}")
    if len(survivors) > 6:
        print(f"  ... and {len(survivors) - 6} more")
    goals = {o.attack.goal.value for o in survivors}
    print(f"""
  Goals represented: {', '.join(sorted(goals))}.

  Almost all produce no tool call and no egress, so no structural layer
  applies by construction. An agent that can write text to a user can mislead
  that user, and nothing in this repository is a control on truthfulness.
  Every red-team report should have this section. Most do not.""")


if __name__ == "__main__":
    demo_flat_line()
    demo_attribution()
    demo_zero()
    demo_residual()
    print("\nFull report: docs/results.md\n")

