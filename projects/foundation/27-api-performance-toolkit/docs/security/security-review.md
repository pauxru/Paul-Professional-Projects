# Security review — load testing as an attack shape

## Threat model

Load-testing tools are, by construction, **denial-of-service tools with a licence**. Every
review therefore starts with the question: *who authorised this run, and what does it hit?*

This review covers three actors:

- **The user of `loadrun` (an engineer).**
- **The API under test.**
- **The build/CI host that runs the load in a pipeline.**

## STRIDE table (per boundary)

| Boundary | Spoofing | Tampering | Repudiation | Information disclosure | DoS | Elevation |
|---|---|---|---|---|---|---|
| Scenario file → CLI | Low — file has no signature, engineer trusts their own file. | Medium — a compromised repo could inject a scenario targeting an internal host. Mitigation: code review of `scenarios/`, and the runner refuses unresolvable/localhost-only scenarios by default in CI. | Low — every run persists the exact scenario body and environment snapshot. | Medium — a scenario may carry `Authorization` headers as literal strings. Mitigation: `.env.example` documents `{{env.TOKEN}}` templating; commit hook can grep for `Bearer eyJ`. | High — **this IS the DoS surface.** Mitigation: `--allow-danger` gate on high stress rates, `MaxVUs` cap. | Low — no privilege boundary crossed. |
| CLI → Target API | Low — scenario `baseUrl` is opaque, no host rewriting. | Low — scenario body is a literal payload; no injection surface. | Medium — the API's logs are the auth trail. Add a scenario-name header (`X-LoadTest-Scenario`) so target-side logs can attribute traffic. | High — response bodies may include PII. Mitigation: JSON-path `ExtractFromJson` only reaches values it's explicitly configured to extract; the toolkit never logs response bodies. | High — DoS of the API is the *goal* of a stress scenario. Only run against systems you own. | Low — no exec surface on either side. |
| CI runner → Target API | Medium — CI token accidentally granted broader access than needed. Mitigation: separate CI credential per environment, least-privilege bearer. | Low. | Low — GitHub Actions audit log records the workflow. | Medium — HTML report may be attached to a public PR. Mitigation: `perf.yml` uploads reports as private artefacts, not to a comment. | Medium — CI-driven overload of a shared dev environment. Mitigation: `perf.yml` targets only the ephemeral in-workflow SampleApi. | Low. |

## Non-claims — what this toolkit is **not**

- Not a security scanner. It does not check for auth bypass, SSRF vulnerabilities in the
  target, TLS misconfigurations, or content-injection issues. `zap`, `nuclei`, `dastardly`
  are for that.
- Not a compliance artefact. Nothing here supports a SOC 2, ISO 27001, or PCI DSS claim.
- Not a fuzz tester. Scenarios drive fixed payloads; there is no mutation engine.
- Not a distributed load platform. Single-node only.
- Not audited by a third party.
- Not warranted against misuse. If you point it at a system you don't own, that's on you.

## Authorisation to test

Before running a scenario against any environment other than your own laptop:

1. Get written permission from the owner of the target system.
2. Announce the run in the appropriate channel (SRE Slack, status page, whatever your team
   uses) — with start / end times and expected offered load.
3. Confirm the environment is *not* shared with production — either logically or via a
   noisy-neighbour setup.
4. Have a kill switch: know how to stop the CLI (`Ctrl-C` is graceful; the run persists what
   it has).
5. Document what you ran; attach the JSON output as evidence.

## Credential handling in scenarios

**Never commit a live bearer token.** The template engine supports `{{env.NAME}}` so you can
write `Authorization: Bearer {{env.LOADRUN_TOKEN}}` and set `LOADRUN_TOKEN` in the CI secret
store. `.env.example` documents this pattern; `.env` itself is gitignored.

The persisted `RunResult.json` includes the scenario as-is, which means it will include
templated placeholders (`{{env.LOADRUN_TOKEN}}`) but never the resolved token — templating
happens per-request inside the executor and the resolved header is not stored on the sample.

## SSRF via scenario URLs

The runner honours `baseUrl` verbatim. If you set it to `http://169.254.169.254/latest/meta-data`
you will get whatever that endpoint returns. There is no allow-list built into the toolkit
because the runner has no way to know what's local to your environment. Mitigations:

- CI runs against a scenario checked in to the repo — code review is the control.
- In CI, run the sample API on a random loopback port and target it explicitly; do not
  accept a `baseUrl` from workflow inputs unless the workflow explicitly checks it.

## Blast-radius controls

- `MaxVUs` per scenario (documented) caps the concurrent worker pool.
- `stressMaxRate` caps the top rate a stress scenario can reach.
- The CLI refuses `stressMaxRate > 10 000` unless `--allow-danger` is passed.
- HTTP client timeout is fixed at 30 s per request — a hung server does not create an
  unbounded backlog on the CLI side, only in the scheduler channel (which is unbounded but
  workers block on `MaxVUs`).

## PII and data handling

The sample API is entirely synthetic. Customer references are `CUST-NNNNN`, product SKUs are
`SKU-NNNNN`, categories are literal words. There is no real customer data in this repo.

If you point the tool at a real environment: response bodies are not logged. `ExtractFromJson`
pulls only the specific values you configured. But **the persisted `RunResult.json` may
include response bytes** (as counts, not content). Treat the JSON output as sensitive.

## Reporting and disclosure

If you find a bug in the toolkit that would cause it to leak credentials from a scenario file
into a run result, please open an issue tagged `security` and do not include the offending
scenario in the report.
