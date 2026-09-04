# ADR 0004 — Permission filtering: at the store, then again as a post-filter

**Status:** Accepted &nbsp; **Date:** 2025 &nbsp; **Owner:** Security

## Context

Enterprise RAG has an ugly failure mode: a well-ranked chunk from a restricted
document ends up in the prompt, the LLM paraphrases it, and the answer leaks
information the user was never authorised to see. We need a design that
prevents this by construction, not by hope.

## Decision

We apply ACLs at **two independent layers**.

1. **At the store query level.** `SqliteVectorStore.SearchAsync` receives the
   caller's `UserPrincipal` and filters candidates before scoring:

   - `document.Acl.Classification <= user.MaxClassification`
   - `!document.Acl.Roles` OR one of them is in `user.Roles`
   - `!document.Acl.Departments` OR one of them is in `user.Departments`

   This is the primary filter — it minimises the surface area of the ranker
   and keeps restricted content out of BM25 term statistics for the query.

2. **As a post-filter before the prompt.** `AclPostFilter.Filter` re-evaluates
   every retrieved chunk against the caller with a fresh `AccessControlList`
   fetched via `IDocumentRepository`. Any chunk whose document's ACL does not
   allow the user is dropped. Unknown documents are dropped too — fail closed.

   The post-filter runs **after** the retriever but **before** the prompt
   assembly and grounding checker. That way even a retriever bug (e.g., a new
   adapter that forgets to pass the user through) cannot leak.

## Consequences

**Positive.**

- Two-layer defence: a retriever regression can only cause a performance
  regression, not a data leak.
- Enforceable in tests. `Query_RestrictedDoc_LeaksNothing` asks a restricted
  question as an unauthorised user and asserts that no citation and no
  substring of the restricted content reaches the answer.
- The `AclPostFilter` is trivially unit-testable — takes a chunk list, a
  user, and a title lookup, returns a filtered list.

**Negative.**

- Two lookups per chunk in the post-filter (chunk → document → ACL). We
  mitigate by caching the document ACL lookup for the request duration in
  practice (not yet cached in the current code — an acceptable optimisation
  gap for a corpus this small).
- ACLs are not versioned — updating a document's ACL retroactively changes
  what past queries would have seen if replayed. Logged as a known limitation.

## Alternatives considered

- **Post-filter only.** Cheaper but fails open if the retriever caches
  restricted chunks; also lets restricted content pollute BM25 statistics.
- **Encrypted embeddings per role.** Overkill, and impossible with a
  brute-force cosine search.
