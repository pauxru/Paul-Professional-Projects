# Screenshots Needed

No screenshots are committed because this repository uses Mermaid and runnable code rather than generated binary artifacts.

| Screenshot | What to capture | Purpose |
|---|---|---|
| Legacy claims list | `http://localhost:5102` with synthetic Acme/Contoso records | Establish as-is MVC context |
| Modern OpenAPI | `http://localhost:5002/openapi/v1.json` rendered in a compatible viewer | Show API contract |
| Authenticated modern claim list | API response with `X-Correlation-Id` header | Show secure migrated read slice |
| 409 response | Stale assessment request RFC 7807 body | Show optimistic concurrency handling |
| Test terminal | Actual Release test summary | Show verification evidence |
| Migration docs | Strangler routing Mermaid diagram | Show consulting deliverable |

Before sharing any screenshot, ensure it contains only the fictional names/data supplied by this repository and no local paths, tokens, headers, or credentials.
