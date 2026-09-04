# Portfolio Positioning Notes

## Where this fits in the portfolio

**Project 15** in the portfolio's ordering. The anchor for anyone hiring for:
- **Fintech / payments** — the domain is a mainstream payments risk pipeline.
- **Backend architecture** — the streaming loop, ring buffer, and versioned-rule reproducibility
  are the interesting engineering.
- **Distribution / event-driven** — bounded channels + partition-preserving ordering + dead-letter
  is the same shape as Kafka + consumer groups, without the Kafka.
- **Data engineering** — the rebuildable feature store and the replay-first mindset generalise
  well.

## The one-line pitch

> "A stateful streaming fraud pipeline that scores in under 1 ms at p99, explains every decision,
> and closes the loop from analyst disposition back into rule tuning — with honest measured recall."

## The one-line risk

> "Rules-only recall on the baseline is low, and the document says so out loud."

## Why the risk is a feature

Because it demonstrates the *engineering process* is honest. Every portfolio project has room to
improve; the ones with fabricated numbers are the ones you cannot trust. The recall number here is
paired with:
- A documented explanation of *why* it is low.
- A tuning tool that produces recommendations.
- A commitment to reproducibility so improvements are verifiable, not vibes.

## The two questions this project must answer

1. **"Can you build a stateful low-latency system?"** — yes, and the tests prove correctness of
   the tricky bits: window boundaries, partition ordering, replay parity, latency-budget
   degradation.

2. **"Can you build something an analyst can trust?"** — yes, because every decision is
   explainable and reproducible, and the case workflow enforces four-eyes at high exposure.

If the interviewer asks a third question — **"can you build something that actually catches
fraud?"** — the answer is: on the baseline, no better than the measured recall; and here is the
tuning path, the shadow mode, the labelled replay, and the ADR that explains why we chose
explainability first.

## The truthful framings I use

- ✅ "Self-directed engineering case study."
- ✅ "Fictional context, synthetic data, ground truth."
- ✅ "Real measured numbers on the seeded dataset."
- ✅ "Not deployed. No real transactions. No real cardholder data."

## The framings I avoid

- ❌ "Production-grade" (my code may be production-*shaped* but nobody has run this against real
  traffic).
- ❌ "Battle-tested" (no).
- ❌ "Scales to millions of transactions per second" (unmeasured; the numbers I *do* have are
  documented).
- ❌ Any specific company name or dollar figure that could imply real client work.
