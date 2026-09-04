# ADR-005 — Three-way match tolerances

- **Status:** Accepted
- **Date:** 2026
- **Context tags:** validation, three-way match, tolerances

## Context

The three-way match reconciles an invoice against its purchase order (PO) and delivery note (DN).
Real supply chains never match to the cent or the unit: prices drift within contract terms,
deliveries arrive partially or split across notes, and rounding differs across systems. A match rule
with **zero tolerance** would flag almost everything and destroy the STP rate; a rule with **too much
tolerance** would wave through genuine over-billing. The tolerances are therefore a deliberate
business/engineering decision, not an implementation detail.

## Options considered

1. **Exact match** on quantities and prices.
2. **Fixed absolute tolerances** (e.g. ±10 currency units).
3. **Relative percentage tolerances** with a small absolute floor, plus distinct pass/warn/fail
   semantics for partial vs over conditions.

## Decision

Adopt **option 3**, encoded in `ValidationTolerances.Default` and applied by `ThreeWayMatchRule`:

- **Arithmetic tolerance:** `max(0.02, |expected| × 0.01)` — a 1% relative band with a 0.02 absolute
  floor for rounding.
- **Quantity/price variance:** 5% (`0.05`).
- **Supplier fuzzy-match threshold:** 0.86 (Jaro-Winkler).

Rule outcomes for an invoice carrying a PO reference:

- **Fail** — billed quantity > ordered × 1.05 (over-billing); invoice total > PO total × 1.05; billed
  > delivered × 1.05.
- **Warn** — delivered < ordered × 0.95 (partial delivery); delivered > ordered × 1.05
  (over-delivery); PO referenced but not found.
- **Pass** — everything reconciles within tolerance.

Lines are matched by normalised description.

## Consequences

- **Positive:** Realistic behaviour — partial deliveries and minor price drift do not block, but true
  over-billing fails and blocks auto-approval; each outcome names the implicated fields for the
  reviewer.
- **Positive:** Tolerances are centralised configuration, so finance can tune them without code
  changes; boundary behaviour is pinned by unit tests (partial, over-delivery, over-billing,
  total-exceeds, missing PO).
- **Negative:** Any fixed threshold is a compromise; a supplier operating exactly at the 5% edge may
  oscillate between pass and warn.

## Risks & mitigations

- *Risk:* 5% is too loose or too tight for a given category. *Mitigation:* the value is a single
  configurable knob; different tolerance profiles per supplier/category are a documented future
  extension.
- *Risk:* description-based line matching mis-pairs lines. *Mitigation:* descriptions are normalised
  before matching; unmatched lines surface as warnings rather than silent passes.

## Alternatives not chosen

Option 1 (exact) is unusable in practice — it would fail nearly every real document. Option 2 (fixed
absolute) does not scale across order sizes (±10 is trivial on a large PO and huge on a small one);
the relative-with-floor approach in option 3 scales correctly while still guarding tiny amounts.
