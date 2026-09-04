# Portfolio summary — Enterprise Order & Payments Platform (Project 01)

**Self-directed engineering case study** by @pauxru. Copilot pair-authored
under `Co-authored-by: Copilot`.

## What this project is

A production-shaped .NET 10 ordering + payments platform that implements the
reliability engineering real payment systems require — and proves it with
tests. Not a demo of "wired-up CRUD"; a demo of *idempotent* CRUD, of a
transactional outbox, of HMAC-verified webhooks, of a domain state machine,
and of provider settlement reconciliation.

## Why it exists

Every "senior developer with 10 years of experience" ad wants to see
distributed-systems judgement. This project condenses that judgement into
one repository:

- One middleware for idempotency plus a UNIQUE index plus a test — that is
  the whole "no duplicate charges" story, and it is legible in five minutes.
- One `BackgroundService` polling an `OutboxMessages` table plus a
  `DomainEvent` write in the SAME EF Core transaction — that is the whole
  atomic-event guarantee.
- One `HmacSha256WebhookSignatureVerifier` using
  `CryptographicOperations.FixedTimeEquals` plus a replay guard — that is
  the whole "vendor can't spoof us and can't retry us into a bad state."
- One `Order` aggregate with explicit `MarkPending` / `MarkAwaitingPayment`
  / `MarkPaid` / `Cancel` methods, each guarded by the current status —
  that is the whole state-machine invariant.
- One `ReconciliationService` that compares a provider CSV to our
  `PaymentIntents` and reports every mismatch class — that is the whole
  "no missing money" story.

Each of these is unit- OR integration-tested. Removing the guard breaks the
test, which is the point.

## Numbers

- 4 layers (Domain / Application / Infrastructure / Api).
- **84 automated tests** — 53 unit, 31 integration.
- **0 test failures** on `dotnet test -c Release`.
- **0 build warnings** on `dotnet build -c Release`.
- **Zero external infrastructure** required to run the tests — no Docker,
  no Postgres, no Redis, no broker.
- **21-section README** with two Mermaid sequence diagrams.
- **5 ADRs** covering the biggest architectural bets.
- **3 runbooks** for the failure modes an on-call engineer would actually
  encounter.

## What I would demo in a 30-minute interview

1. Show `IdempotencyMiddleware.cs`. Explain the UNIQUE index. Run
   `PaymentFlowTests.Authorize_same_key_twice_only_charges_once` live.
2. Show `OutboxDispatcher.cs`. Explain retry + jitter + DLQ. Run
   `OutboxDispatcherTests.Failing_message_is_dead_lettered_after_MaxAttempts`.
3. Show `HmacSha256WebhookSignatureVerifier.cs`. Point at
   `CryptographicOperations.FixedTimeEquals`. Run
   `WebhookTests.Bad_signature_returns_401_and_does_not_mutate`.
4. Show `Order.MarkPending`. Point at the transition guard + the domain
   exception. Run `OrderStateMachineTests`.
5. Show `ReconciliationService.cs`. Show the synthetic file generator that
   can inject every mismatch class. Run `ReconciliationTests`.

## What I deliberately did not do

- Real vendor integration (Stripe, mPesa). The `IPaymentProvider` port and
  the deterministic simulator are the honest substitute.
- Real broker. `ChannelEventBus` is the default in-process bus; RabbitMQ +
  Azure Service Bus are documented adapter stubs.
- Load testing. This is a case study, not a live service.
- Docker verification. The `docker-compose.yml` is committed but labelled
  `UNVERIFIED` because the build host cannot start containers.

## What this signals about my engineering judgement

- **I write for the reviewer, not the compiler.** Every service has a
  reason encoded in an ADR. Every test has a name that reads as an
  invariant.
- **I take the honesty rule seriously.** The README's "Non-claims" and
  "Known Limitations" sections are as detailed as the "Executive Summary."
- **I care about failure modes.** The runbooks are what I would actually
  hand a colleague at 2 a.m. — not marketing copy about "highly available."
