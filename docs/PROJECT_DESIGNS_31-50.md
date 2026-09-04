# Proposed Projects 31–50 — Principal-Level Portfolio

**Status: PROPOSAL — awaiting approval. Nothing built yet.**

These extend the existing 30 (senior-level) with 20 projects pitched at **principal engineer**
complexity, split across two Upwork profiles from `upwork-strategy-brief.md`:

- **S1 — .NET & Azure Modernization Engineer** ($75/hr) → projects 31–40
- **S2 — AI Engineer / AI Application Engineer** ($70–85/hr) → projects 41–50

---

## What makes these *principal* rather than senior

The existing 30 prove "I can build this system correctly." These prove something harder:

| Senior | Principal |
|---|---|
| Builds the system | Builds the **mechanism that makes changing the system safe** |
| Writes tests that pass | Builds **harnesses that find bugs nobody thought to test for** |
| Uses a library | **Implements the algorithm** the library hides |
| Reports a metric | Reports a metric with a **confidence interval and an ablation** |
| Handles the failure | **Proves** the failure cannot happen, or bounds it |

Concretely, the recurring principal signals below are: static analysis over compiled artefacts,
deterministic simulation of distributed failure, correctness-by-construction, formal-ish
verification, queueing theory, statistical rigour, and cross-ABI memory safety.

---

## Language diversity

| Language | Projects |
|---|---|
| **Rust** | 37, 47 |
| **Python** | 38, 40, 41, 43, 44, 46, 50 |
| **Go** | 32, 39, 42, 49 |
| **Java** | 33, 48 |
| **C++** | 35, 46 |
| **C#/.NET** | 31, 34, 35, 36 |
| **TypeScript** | 45, 50 |

Deliberately no longer .NET-only: a principal engineer is expected to pick the right tool and to
be credible when the client's stack is not yours. Each non-.NET choice below is justified by the
problem, not by novelty.

---

# S1 — .NET & Azure Modernization (31–40)

> Profile promise: *"I can safely modernize systems that companies are afraid to touch."*
> Every project below is therefore about **safety under change**, not greenfield building.

---

### 31 — Assembly Archaeologist: IL-level legacy dependency analyser
**Language: C# (Roslyn + Mono.Cecil)**

**Story.** A client has 400 DLLs in production. Source is missing for 40 of them. Nobody
still employed knows what calls what. Every modernization proposal has been rejected because
nobody can answer "what breaks if we touch this?"

**What it does.** Reads compiled IL directly (no source required), builds the complete
type-and-method call graph across every assembly, then detects .NET Framework–only API usage —
`AppDomain`, Remoting, WCF server, `System.Web`, GAC binding, `BinaryFormatter` — and classifies
each assembly by migration difficulty. Produces a **dependency-ordered migration plan** via
topological sort, with deliberate cycle-breaking where the graph is circular (it always is).
Finds genuinely dead code so it can be deleted rather than migrated.

**Principal signal.** Static analysis over compiled artefacts; cycle-breaking heuristics; the
output is a *decision*, not a report.

---

### 32 — Strangler Router: shadow traffic with semantic response diffing
**Language: Go**

**Story.** You cannot big-bang a monolith cutover. The only honest way to know the rewrite is
correct is to send it real production traffic and compare — *before* it serves anyone.

**What it does.** A reverse proxy that mirrors live traffic to both legacy and modern
implementations, returns only the legacy response, and diffs the two. The diffing is
JSON-structure-aware with configurable ignore rules (timestamps, GUIDs, ordering), so it
surfaces *semantic* divergence rather than noise. Tracks per-endpoint equivalence confidence
over time and drives a shadow → canary → cutover state machine with automatic rollback when
divergence spikes.

**Principal signal.** This is the mechanism that makes migration safe. Semantic diffing with
ignore-rule algebra is the hard part, and it is what people get wrong.

**Why Go.** Network proxy under concurrent load — goroutines and the stdlib HTTP stack are the
honest tool. Also proves the profile isn't .NET-only.

---

### 33 — Heterogeneous Database Migration Verifier
**Language: Java**

**Story.** SQL Server → PostgreSQL. The schema conversion is a weekend. Proving that not one
row was silently corrupted is the six-month part — and silent corruption is the thing that ends
careers.

**What it does.** Orchestrates dual-write, then continuously reconciles row-level checksums
across two engines that disagree about almost everything. Implements an explicit taxonomy of
silent-corruption classes: collation and case sensitivity, `DECIMAL` precision and rounding
mode, `DATETIME2` vs `timestamptz` and DST, `NULL` vs empty string, trailing-whitespace
semantics, and Unicode normalisation. Backfills with watermarks, detects drift, and gates
cutover on measured equivalence.

**Principal signal.** The taxonomy *is* the expertise. Anyone can compare row counts.

**Why Java.** JDBC is the most credible neutral ground for heterogeneous database work, and it
shows range beyond .NET on the profile that most needs it.

---

### 34 — Saga Extractor: proving distributed transactions still hold
**Language: C#**

**Story.** The monolith wraps twelve operations in one `TransactionScope`. Split it into
services and ACID quietly becomes "hope". Someone has to decide where the sagas go and prove the
compensations are complete.

**What it does.** Models each extracted transaction as a saga, then **verifies compensation
completeness**: every forward step has a reachable inverse, compensations are idempotent and
commutative where required, and no interleaving leaves an unrecoverable state. Exhaustively
simulates failure at every step boundary — including failure *during* compensation — using
model-checking-style state exploration rather than sampled tests.

**Principal signal.** Exhaustive state exploration and a correctness argument, not example tests.

---

### 35 — Crown Jewels Bridge: a 20-year-old C++ engine, safely in the cloud
**Languages: C++ core + C# host**

**Story.** The pricing engine is 60,000 lines of C++, written by someone who left in 2009. It is
correct, it is fast, and the business will not authorise a rewrite. It must nonetheless run in
Azure and be callable from .NET — without the memory-safety of the whole platform depending on
1990s pointer discipline.

**What it does.** A real computational C++ core (option pricing / actuarial reserving) exposed
through a deliberately narrow, stable **C ABI**, consumed from .NET via P/Invoke with
`SafeHandle` ownership and explicit lifetime rules. Fuzzes the interop boundary for memory
safety, and **benchmarks marshalling strategies** — blittable structs vs marshalled classes vs
`Span<T>`/`ref struct` zero-copy — quantifying the real cost of crossing the boundary.

**Principal signal.** Cross-ABI memory safety and measured marshalling cost. Most engineers
never touch this; it is exactly the "we can't rewrite it" problem clients actually have.

---

### 36 — Authentication Migration: Forms/WS-Fed → OIDC with zero lockout
**Language: C#**

**Story.** Auth migration is the highest-risk modernization there is. Get it wrong and either
every user is locked out, or — far worse — none of them are.

**What it does.** Runs legacy Forms cookies, WS-Federation, and modern OIDC **simultaneously**,
bridging sessions across all three. Migrates users incrementally with **rehash-on-login**
(PBKDF2 → Argon2id at the moment of successful authentication, so no password reset email is
ever sent), transforms legacy claims to modern ones, and keeps a tested rollback path at every
stage. Fully threat-modelled, including downgrade attacks between the coexisting stacks.

**Principal signal.** Security-critical, zero-downtime, irreversible-if-wrong. The
rehash-on-login trick is the detail that shows you've actually done this.

---

### 37 — Deterministic Distributed Systems Simulator
**Language: Rust**

**Story.** The new microservices pass every test and fail in production every third Tuesday.
The bug is a timing interleaving that occurs once in ten million runs. You cannot fix what you
cannot reproduce.

**What it does.** A FoundationDB-style deterministic simulation harness: a seeded scheduler that
makes concurrency reproducible, a simulated network with partitions, delays, reordering and
duplication, simulated clock skew, and systematic fault injection. **Any failure is replayable
from its seed.** A real replication/consensus protocol runs inside it, and the harness finds
genuine bugs in that protocol — then reproduces each one on demand.

**Principal signal.** This is how elite infrastructure teams actually test. Probably the single
most senior-signalling project in the entire portfolio.

**Why Rust.** Deterministic execution and precise control over scheduling and allocation; also
the honest modern language for this class of work.

---

### 38 — Migration Wave Planner: cost, risk and blast radius under uncertainty
**Language: Python**

**Story.** "Move it all to Azure." In what order? At what cost? And when wave 4 fails at 2am,
what else goes down with it? These are the questions that decide whether the programme is funded.

**What it does.** Models the estate as a dependency DAG, then **optimises migration wave
ordering** under constraints (team capacity, freeze windows, coupling). Computes true cost
curves including the parts people forget — egress, dual-running overlap, licence
double-payment — and runs **Monte Carlo simulation on schedule risk** to produce P50/P90
completion dates instead of a single fictional date. Computes blast radius per wave.

**Principal signal.** Decision-support under uncertainty. Principals are paid for sequencing
judgement, and this makes that judgement explicit and defensible.

---

### 39 — Zero-Downtime Schema Evolution Engine
**Language: Go**

**Story.** Add a `NOT NULL` column to a 900-million-row table on a system with no maintenance
window. The naive migration takes an exclusive lock and the business stops.

**What it does.** Orchestrates expand → migrate → contract, backfilling in adaptive batches
**throttled by live replication lag** rather than a guessed sleep. Ships a **safety linter that
statically rejects unsafe DDL** before it ever reaches production, with the specific lock class
each statement would take and why it is dangerous. Every migration must declare a tested
rollback, and the engine simulates lock-wait storms before applying.

**Principal signal.** Operational safety mechanisms at scale; refusing unsafe changes is a
stronger stance than executing them carefully.

---

### 40 — COBOL Batch Decommissioning with byte-equivalence proof
**Language: Python (+ C# consumer)**

**Story.** A nightly COBOL batch has produced the fixed-width file that drives the business
since 1994. Nobody may change the output format. Everybody wants the batch gone.

**What it does.** A real COBOL data decoder: copybook parsing, EBCDIC translation, **COMP-3
packed decimal**, signed overpunch, `OCCURS DEPENDING ON`, and implied decimal scaling. Then a
modern event-driven reimplementation runs in parallel, and a semantic differ **proves
byte-equivalence** of the output before the legacy job is switched off.

**Principal signal.** COMP-3 and overpunch decoding is genuinely niche, genuinely hard, and
instantly credible to anyone who has worked near a mainframe. Equivalence proof is the safety
mechanism.

---

# S2 — AI Application Engineer (41–50)

> Profile promise: *"I build AI capabilities that actually fit into production software."*
> So these are deliberately **not** "another RAG chatbot" — they are the engineering substrate
> that makes AI systems measurable, affordable, safe and debuggable.

---

### 41 — LLM Evaluation & Regression Harness
**Language: Python**

**Story.** A team ships a prompt change on Friday. Quality drops 12%. Nobody notices for three
weeks because there are no tests — and you cannot unit-test a probabilistic system.

**What it does.** Golden datasets with versioning; LLM-as-judge that is **calibrated against
human labels** rather than trusted blindly (measures judge–human agreement with Cohen's κ, and
reports when the judge is not trustworthy); **statistical significance testing** on eval deltas
using bootstrap confidence intervals, so "3% better" is only claimed when it survives the CI;
CI regression gates; cost and latency tracked alongside quality.

**Principal signal.** Statistical rigour applied to AI. Nearly everyone reports eval deltas
without significance testing, which makes most reported improvements noise.

---

### 42 — Semantic Cache & Request Coalescing Gateway
**Language: Go**

**Story.** 60% of the LLM bill is duplicate questions. An exact-match cache gets a 4% hit rate,
because humans never phrase things the same way twice.

**What it does.** Embedding-based semantic caching with a tunable similarity threshold — and,
crucially, it **measures the false-hit rate**, because in a cache a false hit is not a
performance issue, it is a *correctness bug* that returns someone else's answer. Publishes the
precision/cost Pareto curve so the threshold is an informed business decision. Adds single-flight
coalescing so a thundering herd of identical concurrent questions costs one inference, with
streaming passthrough preserved.

**Principal signal.** Recognising that cache correctness dominates cache hit rate, and
quantifying the tradeoff instead of asserting it.

---

### 43 — Retrieval Quality Lab: chunking and embedding ablation
**Language: Python**

**Story.** "Our RAG isn't working." The team has spent six weeks tuning prompts. The problem was
never the prompt — it is that the right document was never retrieved.

**What it does.** Systematic ablation across the retrieval stack: chunking strategy (fixed,
recursive, semantic, proposition-level), embedding model, reranking, and fusion method —
evaluated on a labelled corpus with **nDCG@k, MRR and recall@k**, significance-tested, and
plotted as a **cost/latency/quality Pareto frontier**. Output is a decision guide stating which
configuration wins for which query class, and where the differences are not statistically real.

**Principal signal.** Disciplined empiricism and the willingness to report null results.

---

### 44 — Prompt Injection Red Team: attack corpus and layered defence
**Language: Python**

**Story.** Your agent summarises customer emails and can call tools. A customer emails it
instructions. The agent, being helpful, follows them.

**What it does.** A structured attack corpus — direct and **indirect** injection, data
exfiltration, tool abuse, encoding and obfuscation, multi-turn manipulation — run against
layered defences: spotlighting, delimiter and provenance tagging, a classifier, a
**capability-scoped tool broker**, and output filtering. Reports honest **attack success rate
per defence layer**, before and after, including the attacks that still succeed.

**Principal signal.** Security-research methodology, and the integrity to publish residual
attack success rather than claim the problem is solved.

---

### 45 — Durable Agent Runtime: resumable execution with exactly-once side effects
**Language: TypeScript**

**Story.** A 40-step agent run fails at step 37. Re-running from the top costs $4 and — much
worse — sends the customer a second refund.

**What it does.** Event-sourced, deterministic **durable execution**: every step is journalled,
so a crashed run resumes exactly where it stopped rather than restarting. Tool side effects are
**exactly-once** via idempotency keys and a side-effect ledger. Adds budget enforcement,
cancellation, human-approval gates that survive process restarts, and a **replay debugger** that
steps through any historical run.

**Principal signal.** Durable execution semantics are a genuinely hard distributed systems
problem — this is Temporal-class work applied to agents.

**Why TypeScript.** The agent ecosystem clients actually deploy is TypeScript; this makes the
project immediately usable to them.

---

### 46 — Constrained Decoding: schema-valid output by construction
**Languages: C++ core + Python bindings**

**Story.** "Just ask it for JSON" fails about 5% of the time. At a million documents, that is
50,000 failures — and retries make latency and cost unpredictable.

**What it does.** Compiles a JSON Schema / grammar into a **finite automaton**, aligns it to the
tokenizer vocabulary, and **masks logits so that invalid tokens are literally impossible to
emit** — making malformed output unrepresentable rather than unlikely. Benchmarked against
retry-based and function-calling approaches on validity, latency, and cost.

**Principal signal.** Correctness *by construction* instead of by retry. Tokenizer-to-automaton
alignment is subtle and is where naive implementations break.

**Why C++.** Logit masking is on the hot path per generated token; this is a real performance
constraint, not a language preference.

---

### 47 — Inference Gateway: continuous batching and SLO-aware admission control
**Language: Rust**

**Story.** Serving models at scale is a **queueing theory** problem wearing an API's clothing.
Teams add replicas when the real issue is head-of-line blocking.

**What it does.** Continuous batching, priority queues with **SLO-aware admission control**
(shed load deliberately rather than degrade everyone), fair-share scheduling across tenants,
backpressure, and token-level streaming. Measures the throughput/latency frontier under load and
analyses it with **Little's Law**, showing where queueing — not compute — is the bottleneck.

**Principal signal.** Applying queueing theory rather than adding hardware.

---

### 48 — Graph + Vector Hybrid Reasoning
**Language: Java**

**Story.** "Which of our suppliers are within two hops of a sanctioned entity?" Vector search
cannot answer this. Embeddings do not do joins, and no amount of chunking will fix that.

**What it does.** Extracts entities and relations into a property graph, then **fuses graph
traversal with vector retrieval** to answer multi-hop questions, returning the **provenance
path** — the actual chain of relationships — as the justification. Includes an honest comparison
showing which query classes graph wins, which vector wins, and which need both.

**Principal signal.** Understanding the *limits* of embeddings is a more senior signal than
enthusiasm for them.

---

### 49 — Model Router and Cascade: multi-objective cost/quality optimisation
**Language: Go**

**Story.** Routing every request to the largest model is the most common and most expensive
mistake in production AI. Roughly 70% of traffic is handled fine by something far cheaper.

**What it does.** Classifies query difficulty, then runs a **cascade** that escalates to larger
models only when confidence is low. Learns the routing policy from outcome data, enforces
per-tenant budgets, fails over across providers with circuit breakers, and publishes the
**cost/quality Pareto frontier** so the business chooses its operating point explicitly.
Monitors for drift as traffic changes.

**Principal signal.** Multi-objective optimisation with an explicit, defensible operating point.

---

### 50 — AI Observability: detecting failures that don't throw
**Languages: Python + TypeScript dashboard**

**Story.** AI systems fail **silently**. Quality degrades for eleven days, every request returns
`200 OK`, and the dashboards stay green. Traditional APM is blind to this by design.

**What it does.** Semantic tracing of chains and agent runs over OpenTelemetry, **embedding
drift detection** (population stability index, MMD) on both inputs and outputs, continuous
quality canaries, and anomaly detection on refusal, hallucination and escalation rates — alerting
on **semantic** regression rather than error rate. Ships a dashboard that makes a silent
degradation visible.

**Principal signal.** Instrumenting for failures that produce no exception is a fundamentally
different discipline from APM.

---

## Toolchain plan (verified feasible)

This host had only .NET 10 and Node.js. `winget` works **without administrator rights**, and
Python 3.12.10 has already been installed and verified this way as a proof of concept:

| Toolchain | Package | Status |
|---|---|---|
| Python 3.12 | `Python.Python.3.12` | **installed and verified** |
| Go | `GoLang.Go` | to install |
| Rust | `Rustlang.Rustup` | to install |
| Java 21 | `Microsoft.OpenJDK.21` + `Apache.Maven` | to install |
| C++ | `LLVM.LLVM` (clang) or MSVC Build Tools | to install |
| TypeScript | via existing Node 24 | available |

Docker remains unavailable, so — exactly as with the first 30 — anything container-related stays
authored-but-`UNVERIFIED`, and no project will claim otherwise.

## Delivery standard (unchanged from the first 30)

Every project still gets: real tests that actually run, ≥4 ADRs, a security review, honest
limitations, a `docs/portfolio/` pack, its own git history — and **independent re-verification by
me**, including the 3× flakiness sweep that caught the concurrency bug in project 08.
