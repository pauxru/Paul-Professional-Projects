# ADR-006 — Separate executable system simulators

## Context

Reliability code is unconvincing if all downstream responses are mocked as immediate 200s. The build host cannot use Docker or external services.

## Options

1. Mock every `HttpMessageHandler`.
2. Call real vendor sandboxes.
3. Host small vendor-like ASP.NET Core applications in the solution.

## Decision

Create separate CRM, ERP, and payment simulator projects on ports 5112, 5212, and 5312. They enforce distinct auth, expose different pagination styles, ETags, idempotency, validation errors, 429 responses, OAuth token refresh, and configurable faults.

## Consequences

The demo is realistic while tests remain in-process and network-independent. Simulator behavior is deterministic and source-controlled.

## Risks

The simulators can drift from real vendor semantics and may create false confidence if mistaken for contract tests against actual products.

## Alternatives

Provider sandboxes should complement these tests in a real delivery pipeline. Pure handler fakes remain useful for narrow timing and protocol edge tests.
