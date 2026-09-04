# Security Review — Enterprise Search Platform

## Scope and method
This lightweight design review inspected the HTTP boundary, JWT policy boundary, parser/index execution boundary, SQLite snapshot boundary, and ACL/facet boundary. It is focused on the fictional Contoso Retail corpus and the code paths in this repository.

## Assets
- Search documents, including restricted support articles and product metadata.
- Group ACLs and JWT claims.
- Index aliases, snapshots, and document versions.
- Query/click analytics, which can reveal search intent in a real deployment.
- Development signing configuration.

## Trust boundaries
1. Browser/service caller to ASP.NET Core API.
2. JWT issuer to API authorization policies.
3. Untrusted query string and document JSON to parser/indexer.
4. Application memory to SQLite logical snapshot.
5. Caller groups to ACL-trimmed results, facets, and suggestions.

## Data classification
All supplied data is fictional. In a real deployment, product data is generally internal, support content may be confidential, group claims are sensitive authorization data, and search/click logs may contain personal or commercially sensitive intent. Logs must not record raw credentials or full sensitive query text without a retention decision.

## Threat model (STRIDE per boundary)

| Boundary | S | T | R | I | D | E |
|---|---|---|---|---|---|---|
| Caller → API | Stolen/forged token | malformed JSON | request attribution gap | response/header leakage | request flooding | scope bypass | JWT validation, policies, correlation IDs, security headers, rate limiting |
| JWT issuer → API | dev issuer exposed | altered claims | token issuance not audited | group disclosure | token-validation load | `search.read` used as manage | HS256 validation, audience/issuer/signature/lifetime checks, explicit read/manage policies; production default-key refusal |
| Query → parser/index | parser ambiguity | unsafe syntax | unclear errors | error reflects internals | wildcard/deep-page/phrase expansion | AST clause injection | AST-only parser, no raw SQL, character/term/clause/page/wildcard/fuzzy/time limits, RFC 7807 errors |
| Index → SQLite | local-file substitution | snapshot modification | no persistent admin audit | file access | write lock / large refresh | index alias mutation | parameterised EF Core, scoped service API, filesystem deployment controls required |
| Groups → results/facets | forged group claim | ACL metadata changes | no immutable audit log | facet/autocomplete count leakage | ACL filter cost | restricted document visibility | query-time allow-list trim applied to hits and every facet scope; tests cover result and facet exclusion |

## Mitigations implemented
- JWT bearer authentication, policy-based `search.read` and `search.manage` authorization, and a Development/Testing-only token helper.
- Startup refuses the known development key in Production; `.env` and database files are ignored.
- `ProblemDetails`, validation checks, request-size/model-binding defaults, correlation IDs, CSP/no-sniff/frame/referrer/permissions headers, and IP-partitioned fixed-window rate limiting.
- Bounded `Channel` indexing queue returns `429`; search has hard page, AST clause, wildcard expansion, fuzzy candidate, term-length, and timeout controls.
- Query strings are tokenized into a closed AST. They never become raw SQL, regular expressions, or dynamic code.
- Document ACLs are applied at query time; facet counts use the same security boundary and exclude only the facet's own selection.
- Highlight text is HTML encoded around explicit `<em>` tags.

## Residual risk
The local token issuer is intentionally a development mechanism, not an enterprise identity service. SQLite file permissions, key rotation, audit log retention, JWT revocation, group freshness, abuse telemetry, and query-log privacy need deployment-specific controls. In-memory analytics disappear on restart. Suggestions are secured but could still disclose authorised vocabulary more broadly than a production policy permits.

## What would change for a real production deployment
Use OIDC/JWKS validation and managed secret storage; use a durable queue and append-only audit sink; encrypt and back up storage; restrict CORS and network exposure; use an external search service with mandatory server-side ACL filters; add per-field authorization, retention, privacy review, SAST/DAST, dependency scanning, alerting, and independent penetration testing. Load test query limits using expected corpus and traffic shapes.

## Explicit non-claims
This is a self-directed engineering demonstration. No formal security audit, penetration test, or compliance certification (PCI DSS, SOC 2, HIPAA, ISO 27001) has been performed or is claimed.
