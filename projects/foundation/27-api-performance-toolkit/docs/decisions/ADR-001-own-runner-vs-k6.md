# ADR-001: Build the load runner in C# instead of using k6 / NBomber

* **Status:** Accepted
* **Date:** 2026-09-02
* **Author:** solo build

## Context

The project needs to drive HTTP load against an API, capture per-request latencies, produce
statistics, and gate CI on assertions. Three options are on the table:

1. Use k6 (Go, JavaScript scenarios), the industry-standard load tool.
2. Use NBomber, a .NET-native load-testing framework.
3. Build the runner ourselves in C#.

## Options considered

### k6
* Pros: mature, well-known, JS scenarios readable to almost any developer, good HTML reports
  (in Grafana Cloud), open-source, correct open-model support out of the box.
* Cons: **the k6 binary is not installed on this build host**, requires Go toolchain to
  build from source, JS scenarios are a separate skill/tool, all reporting is JS-side (integration
  needed for CI PR comments), and coordinated-omission handling depends on the executor choice
  which is subtle.

### NBomber
* Pros: .NET-native, F#/C# scenarios, integrates with xUnit, decent reporting.
* Cons: opinionated internal model (steps and feeds are re-invented rather than the standard
  HTTP client story), heavier dependency footprint, licensing (Community vs Pro), doesn't ship
  a rigorous statistical-comparison story, and — most importantly for the portfolio angle —
  it hides the exact behaviour of the load model behind a facade.

### Own runner (chosen)
* Pros: We control the load-model semantics (open vs closed, coordinated omission), the
  statistics layer (Hdr-style buckets, exact percentiles, Mann–Whitney U + bootstrap), and the
  report output (self-contained HTML with hand-rolled SVG). The portfolio value is exactly in
  *demonstrating* that we can build these correctly rather than delegating to a tool. And k6
  is not installed here — building against k6 would be pretending.
* Cons: Larger up-front cost. Risk of subtle correctness bugs (mitigated by 60+ unit tests
  that pin the histogram precision bound, the significance test on known distributions, and the
  open-model scheduler behaviour under a controllable slow-stub server).

## Decision

**Build the runner in C#.** Ship k6 scripts as a documentation artefact for anyone who wants
to run the same scenarios against a k6 install — clearly marked `NOT EXECUTED on this host`.

## Consequences

* We own the correctness story end-to-end. The tests can pin exact histogram behaviour rather
  than trusting a third-party implementation.
* Reporting is a single-file HTML with no external assets — friendly to PR comments and
  air-gapped environments.
* Two languages of scenarios exist: JSON for `loadrun` and JS for k6. They must be kept in
  sync manually. This is acceptable because the k6 scripts are documentation, not the
  primary code path.

## Risks

* Correctness bugs in the load model are on us. **Mitigation:** the open-model scheduler is
  tested against a controllable slow-stub server that verifies the target arrival rate is
  maintained even when the "server" is slower than the inter-arrival time. The significance
  test is tested against known-identical and known-shifted distributions.
* No community pressure to keep the tool honest. **Mitigation:** the ADR and the tests are the
  contract.

## Alternatives — when to revisit

* If a client uses k6 already and wants to gate CI on the same scripts they run manually,
  switch to k6 for that engagement — the scenario semantics translate cleanly, and this
  toolkit's reports would be replaced with k6's.
* If we need distributed generation across many boxes, revisit NBomber or Locust; building
  a distributed coordinator ourselves is out of scope here.
