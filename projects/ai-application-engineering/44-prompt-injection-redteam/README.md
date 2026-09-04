# Prompt injection: which defences are guarantees, and which are measurements

A red-team harness for LLM agent defences that refuses to report a single
number. It runs a structured corpus of 81 attacks and 45 benign documents
against all 32 subsets of a five-layer defence stack, and its central claim is
that the five layers are **not five of the same thing**:

- Three are **structural**. A capability broker, an egress filter and a
  normaliser make statements about what the system *can do*, and those
  statements hold whatever the model decides.
- Two are **behavioural**. A classifier and spotlighting change how likely the
  model is to comply. They reduce a probability, and a probability reduction
  is not a control.

Almost every prompt-injection write-up mixes these together, quotes a blocked
percentage, and leaves the reader unable to tell which half of the number
survives a model upgrade. Section 3 separates them mechanically: it sweeps the
simulated target's compliance across its **entire** range from 0.0 to 1.0 and
asks which lines are flat. A flat line is a guarantee. A sloping line is a
measurement of one model on one day.

> **Read [`docs/results.md`](docs/results.md) first.** It is the deliverable;
> the code exists to produce it. 12 sections, 9 predictions registered before
> the corresponding measurement was read, 7 held, 2 did not.

---

## The two headline results

**A capability broker is flat at 0.0% across the whole susceptibility range**
for the family designed to defeat it — `authorised_tool`, where the attacker
targets a tool the *user is already entitled to use*, so the channel check
cannot help and only argument provenance can. Undefended, that family runs at
0.0% / 83.3% / 100.0% as the model becomes more suggestible. Behavioural
defences track it down to 0.0% / 50.0% / 83.3%. The broker holds the line at zero everywhere.

**The one call it allows is the honest boundary of the mechanism.** A single
tool call survives, and it arrives on the USER channel: the operator typed it
themselves. Allowing it is not a miss, it is correct. Stated precisely, that
is the guarantee:

> A capability broker reduces the tool-call attack surface to exactly the set
> of actions the principal asked for themselves.

**Nine of 81 attacks survive the full stack (11.1%).** Section 10 is about
those nine and why nothing in this repository can stop them.

---

## What makes the measurement trustworthy

**Success is judged by a marker, not by a model.** Every attack carries a
unique token; the attack landed if and only if that token appears in the
output or in a tool argument. No judge model, no rubric, no second system's
opinion in the loop ([ADR-0001](docs/adr/001-marker-based-judging.md)).

**False positives are reported beside every detection rate.** A detection rate
quoted alone is not a measurement — a layer that blocks everything scores
100%. 45 benign documents run through every configuration, including ones
written to look alarming: an incident-response runbook that contains the words
"ignore previous instructions", a bug report quoting an injection verbatim.

**Layer contribution is attributed by Shapley value over all 32 subsets**, not
by a sequential waterfall. Order-of-evaluation artefacts are the single most
common way layered-defence write-ups mislead: whichever layer is measured
first absorbs all the shared credit. The values sum to the full stack's total
reduction — the efficiency axiom — and that identity is asserted in the tests.

**Zero is reported as an interval.** Several configurations block everything.
Section 12 is about what that licenses: 0/28 is a 95% upper bound near 10%,
not proof of a defence.

**Predictions are registered before results are read.** The report DSL raises
if a prediction is left unresolved. Two of nine were wrong and both are
argued in place rather than quietly rewritten.

---

## Structure

| path | what it is |
|---|---|
| `redteam/channels.py` | Trust lattice and character-granular provenance tracking |
| `redteam/corpus.py` | 81 attacks in 8 families, 45 benign documents, deterministic |
| `redteam/normalize.py` | Unicode tag blocks, zero-width, bidi, confusables, one-level decoding |
| `redteam/defenses.py` | Lexical classifier, spotlighting, datamarking, delimiters |
| `redteam/broker.py` | Four ordered authorisation rules over proposed tool calls |
| `redteam/egress.py` | Zero-click vs click-required exfiltration channels |
| `redteam/target.py` | The simulated model — a compliance function, not an LLM |
| `redteam/pipeline.py` | Layer composition and the run loop |
| `redteam/metrics.py` | Wilson intervals, Shapley values, interaction indices, Pareto frontier |
| `run_redteam.py` | Builds `docs/results.md` |

## Running it

```powershell
.\test.ps1     # lint, tests, mutation check, determinism, report freshness
.\demo.ps1     # the three results worth seeing, on the console
python run_redteam.py
```

407 tests. Python 3.12, standard library only. No network, no model API, no
API keys.

## Documents

- [`docs/results.md`](docs/results.md) — the report
- [`docs/known-limitations.md`](docs/known-limitations.md) — what this does not establish
- [`docs/security-review.md`](docs/security-review.md) — threat model and residual risk
- [`docs/portfolio/04-bugs-the-experiment-found.md`](docs/portfolio/04-bugs-the-experiment-found.md)
  - eleven defects, and why a measurement harness finds a specific *kind* of bug

## The largest caveat, stated once

**The target is simulated.** No LLM is called. This constrains the results
asymmetrically, and the report is explicit about it in every section:

- Results about **structural** layers quantify over *all* model behaviours by
  sweeping susceptibility end to end. They do not depend on the simulation
  being accurate, because they hold at every value it could take.
- Results involving **behavioural** layers are conditional on a compliance
  model that is stated in `target.py`, swept rather than fitted, and not tuned
  to produce any conclusion.

Section 4's contradicted prediction is a live example: spotlighting takes the
largest Shapley value, and the report says plainly that its *rank* is a
consequence of a chosen parameter and is therefore not a result. The
structural layers' values do not depend on that parameter, which is the whole
distinction the report is built on.

