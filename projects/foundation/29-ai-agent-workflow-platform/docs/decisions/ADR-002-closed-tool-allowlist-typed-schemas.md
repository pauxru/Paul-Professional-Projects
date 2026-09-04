# ADR-002: Closed, statically-registered tool allow-list with typed JSON-schema parameters

## Title
Closed, statically-registered tool allow-list with typed JSON-schema parameters

## Status
Accepted

## Context
Model output is untrusted input. In an agent platform, the most dangerous failure mode is not a bad sentence; it is allowing generated text to cross into an interpreter, shell, database, filesystem, dynamic HTTP client or dynamically loaded code path. This project is explicitly designed so model output can never cause arbitrary code, shell, SQL or filesystem execution.

The system still needs tools. Workflows call calculators, HTTP lookups, approval creation and other bounded capabilities. Those capabilities must be available without turning the model into a remote procedure dispatcher with ambient authority.

## Decision
Tools are closed, compiled in and statically registered. Each tool has a typed parameter contract represented as JSON Schema. Tool arguments are validated and coerced before execution. Invalid arguments produce a structured tool error that can be recorded and, where appropriate, returned to the model; they do not surface as arbitrary exceptions or partial executions.

There is no `eval`, no dynamic assembly loading, no shell tool and no arbitrary HTTP tool. The `http_get` tool is constrained to allow-listed hosts, applies SSRF guards that block private, loopback and link-local address ranges, and does not follow redirects to off-list destinations. The `calculate` tool uses a safe expression evaluator rather than language evaluation.

Per-step tool allow-lists further reduce authority. If a model attempts to call a hallucinated or unauthorised tool, the attempt is blocked and recorded in the trace as a policy violation.

## Options Considered
| Option | Pros | Cons |
| --- | --- | --- |
| Closed static tool registry with schemas | Strong security boundary, inspectable capabilities, deterministic validation, good audit trail | Requires code changes to add tools; less dynamic |
| Dynamic plugin loading | Extensible at runtime | Expands supply-chain and privilege risks; harder to audit |
| Shell or generic command tool | Maximum flexibility | Direct arbitrary execution; unacceptable blast radius |
| Arbitrary HTTP fetch tool | Simple integration | SSRF and data-exfiltration risk; redirects become policy bypasses |

## Consequences
Positive consequences: the model can only request known operations with known shapes; failures are structured; the trace can show exactly which policy allowed or denied an action; unit tests can exercise malformed JSON, hallucinated tools and unauthorised attempts.

Negative consequences: adding integrations is more deliberate. Developers must define schemas, validation, risk level and allow-list placement for each capability.

## Risks
Schema validation can be treated as sufficient when semantic validation is also required. The mitigation is to keep domain decisions in Domain/Application code and reserve tools for bounded operations. SSRF defenses must also be maintained as network environments evolve.

## Alternatives Rejected
We rejected interpreter-backed tools, shell execution, dynamic code loading and unconstrained HTTP because they would make prompt injection and model hallucination operationally dangerous. We also rejected prompt instructions as the primary defense; permissions must be enforced by code.
