# Conflict Resolution — Algorithms, Guarantees, and Known Limitations

> This is the most important engineering document in the project. It explains **exactly** how
> concurrent edits converge, **what is guaranteed**, and — just as importantly — **what is not**.
> Read it before trusting anything the system says about "real-time collaboration".

Collab uses **two different conflict-resolution strategies**, chosen deliberately because the two
data shapes have different correctness requirements:

| Data shape | Example | Strategy | Why |
|---|---|---|---|
| Plain text | Incident runbook, notes | **RGA CRDT** (Replicated Growable Array, a causal-tree CRDT) | Character order must be preserved and merged; text has no natural primary key per character |
| Structured record | Release checklist (list of items with fields) | **Field-level Last-Writer-Wins with version vectors** | Each field has a stable identity; "latest edit wins per field" is intuitive and correct for records |

The contrast is intentional: **different data shapes deserve different conflict strategies.** A
single "one algorithm for everything" answer is a red flag, not a feature.

---

## 1. Plain text: RGA (Replicated Growable Array)

### 1.1 The model

Every character ever inserted becomes an immutable **node** in a tree. A node has:

- an **`ElementId`** — the pair `(lamport, replicaId)`, rendered on the wire as `"lamport@replica"`
  (for example `7@ada`). This is globally unique and never reused.
- a **parent** — the `ElementId` of the node the character was inserted *after* (its causal
  predecessor). The very first characters point at the sentinel **Root**, whose id renders as `"0@"`.
- a **value** — the single character.
- a **`deleted`** flag — deletion is a **tombstone**, never a physical removal.

The visible document is produced by a deterministic **pre-order traversal** of this tree:

```
materialize(tree):
    text = ""
    walk Root's subtree depth-first, visiting each parent's children
        in DESCENDING ElementId order
    for each visited node that is not deleted: append node.value
    return text
```

`ElementId` ordering is a total order: **higher Lamport timestamp wins; ties are broken by the
ordinal comparison of the replica id string.** Children are stored in *descending* id order, so a
newer concurrent insert at the same position sorts ahead of an older one deterministically on every
replica.

### 1.2 Why it converges

An RGA document's rendered text is a **pure function of the set of operations applied to it** —
nothing else. Two replicas that have received the same set of operations render identical text,
*regardless of the order in which they received them*, because:

1. **Inserts are commutative.** An insert only adds a `(id → parent, value)` mapping. The traversal
   that produces text sorts siblings by id, so the final tree shape — and therefore the text — does
   not depend on arrival order.
2. **Deletes are idempotent and commutative.** A delete just sets a tombstone flag on an existing
   node. Applying it once or many times, before or after neighbouring inserts, yields the same flag.
3. **Concurrent inserts at the same position are ordered by id**, a rule every replica computes
   identically. There is never a "coin flip" that two replicas could resolve differently.

This is the standard CRDT convergence argument: the operations form a **join-semilattice** and the
merge (set union of nodes, logical-OR of tombstones) is associative, commutative, and idempotent.

### 1.3 Causality and out-of-order delivery

An insert references its parent's id. If that parent has not yet arrived (packets reordered, a
client resumed mid-stream), the operation is **not** dropped and **not** applied out of place — it is
held in a **causal buffer** and retried every time new nodes arrive, until its parent exists. A
delete that references an unknown node is buffered the same way. This gives us **causal delivery**
without requiring the transport to guarantee ordering.

Lamport timestamps provide the logical clock: every replica advances its clock past any id it
observes, so a new local insert always gets an id greater than everything it has seen, preserving the
"happens-after" relationship in the id order.

### 1.4 The server is authoritative

Peer-to-peer CRDTs converge *eventually*. That is not good enough for an operations tool where people
ask "what is the current version?" So Collab funnels every operation through the server, which:

- assigns a **monotonic, gap-free `ServerSequence`** per document (the authoritative version axis);
- appends the operation to a durable **operation log**;
- rejects an operation that references state it cannot resolve (returns a well-defined
  `Rejected{ code = "unknown_reference" }` rather than corrupting the document);
- broadcasts the accepted operation, tagged with its sequence, to the other participants.

So we get CRDT convergence **and** a single authoritative history. Clients still apply their own
edits optimistically and reconcile against the server sequence (see §3).

### 1.5 What RGA text **guarantees**

- **Strong eventual consistency.** Any two replicas that have applied the same set of operations
  render byte-for-byte identical text. Proven by a randomised property test (see §5).
- **No lost inserts.** Every accepted character is retained; concurrent inserts at the same spot are
  both kept, in a deterministic order.
- **Intention-preserving deletes.** Deleting text removes exactly the targeted characters; a
  concurrent insert "inside" a deleted range survives (it is re-parented onto surviving nodes).
- **Causal safety.** An operation is never applied before its causal dependencies.

### 1.6 What RGA text **does NOT guarantee** (read this)

- **It is not semantic merge.** If Ada turns "cat" into "cats" and Grace concurrently turns "cat"
  into "cot", RGA converges to a well-defined string (for example "cots" or "cats" depending on ids)
  — it does **not** understand that both wanted to edit the same word and will not produce an
  English-correct merge. CRDTs guarantee *convergence*, not *human intent reconciliation*.
- **Interleaving of concurrent typed runs is possible.** If two people simultaneously type whole
  sentences at the same cursor position, the characters can interleave by id rather than staying as
  two clean blocks. This is a known and documented property of character-level RGA. Block-level
  intention preservation would require a more complex CRDT (e.g. Peritext) and is out of scope.
- **Tombstones accumulate.** Deleted nodes are retained in the CRDT state forever (until a snapshot
  compacts them — see below). Memory/state grows with total edits, not current length. We mitigate
  with periodic snapshots but do **not** implement full distributed tombstone garbage collection
  (that needs every replica's acknowledgement watermark, which we do not track).
- **No rich-text/formatting model.** Only plain text. Bold/lists/tables are not represented.
- **Unicode is handled at the .NET `char` (UTF-16 code unit) level.** Characters outside the Basic
  Multilingual Plane (emoji, some CJK) are surrogate *pairs* and are inserted as two nodes. They
  converge correctly but a concurrent edit could in principle split a surrogate pair. Production rich
  text would operate on grapheme clusters. Documented, not fixed.

---

## 2. Structured documents: field-level LWW with version vectors

### 2.1 The model

A structured document (e.g. a checklist) is a set of **items**, each with a set of **fields**
(`title`, `done`, `assignee`, …). Each item and each field carries an **`LwwStamp`** = `(lamport,
replicaId)`, rendered `"lamport@replica"`.

- **Set field**: accepted iff its stamp is **greater** than the field's current stamp (higher
  Lamport; ties broken by replica id). Otherwise the update is a no-op (an older write cannot clobber
  a newer one).
- **Add item / remove item**: same stamp comparison at the item level. A remove is an **LWW-Element**
  tombstone: an add with a higher stamp than the remove resurrects the item; a remove with a higher
  stamp than the last add hides it.

A **version vector** (`replicaId → max lamport seen`) summarises how much of each replica's history a
document has absorbed, so a client can ask "what changed since my vector?" and get just the delta.

### 2.2 What structured LWW **guarantees**

- **Convergence.** Field values are the max-stamp write; the merge is commutative/associative/
  idempotent, so replicas converge.
- **Per-field last-writer-wins is intuitive and total.** No field is ever left in a "conflicted"
  placeholder state; there is always a single defined value.

### 2.3 What structured LWW **does NOT guarantee** (read this)

- **Concurrent writes to the same field lose data by design.** If Ada sets `assignee = "Grace"` and
  Grace concurrently sets `assignee = "Linus"`, exactly one survives — the higher stamp — and the
  other is **silently dropped**. That is what "last writer wins" *means*. It is the correct trade-off
  for a single-value field (there is no meaningful "merge" of two names) but it is **data loss**, and
  we surface it honestly rather than pretending otherwise.
- **No cross-field invariants / transactions.** Two fields can be updated by two replicas such that
  the combined record violates an application invariant the individual writes each respected. LWW
  merges field-by-field; it does not know about "if `done` then `completedBy` must be set".
- **Clock is logical, not wall-clock.** "Last" means "highest Lamport stamp", **not** "latest in real
  time". A user on a laggy connection whose Lamport clock is behind can have their newer-in-real-time
  edit lose to an older-in-real-time edit with a higher logical stamp. This is inherent to LWW and is
  documented as such.

---

## 3. Optimistic local editing and reconciliation

Clients do **not** wait for a server round-trip to show their own keystrokes. The protocol is:

1. The client applies the edit to its **local** RGA immediately (zero-latency typing).
2. It sends the operation to the server and keeps it in a **pending** set.
3. The server assigns a sequence, persists, and broadcasts.
4. Remote operations that arrive while local edits are pending are applied to the local RGA too —
   because RGA operations commute, there is **no explicit transform/rebase step required**: the local
   and remote ops merge by the same id-ordered traversal. When the client's own operation echoes back
   with its server sequence, it is simply removed from the pending set.

This is the key ergonomic advantage of a CRDT over classic OT here: **reconciliation is merge, not
transform.** OT would require a function to rebase each pending op against each incoming op; RGA gets
it for free from commutativity. (The trade-off — per-character id overhead — is discussed in
`docs/decisions/ADR-001-ot-vs-crdt.md`.)

The web client (`src/Collab.Api/wwwroot`) demonstrates this end to end, including caret preservation
when a remote edit lands before the local caret.

---

## 4. Persistence, snapshots, and time travel

- Every accepted change set is an **operation-log** row `(documentId, serverSequence, payload)`.
- Every *N* operations (default 200, configurable) the server writes a **snapshot**: the full CRDT
  state plus the rendered text at that sequence.
- **Load** = latest snapshot + replay of the log tail. Verified by a test that snapshot+replay
  reproduces the document byte-for-byte.
- **Time-travel read** at sequence *k* = replay the log up to *k*.
- **Restore** to an old version does **not** rewrite history: it computes a diff from current → target
  and applies it as **new forward operations**, so the audit trail and the CRDT invariants are intact.
- **Diff** between versions uses an LCS dynamic program (character or line granularity), tested for
  correctness.

---

## 5. How convergence is actually proven

`tests/Collab.UnitTests` contains a **randomised property test** (fixed seed `20260903` for
reproducibility):

- generate random concurrent operation sequences from *M* simulated clients;
- apply them to independent replicas in **different, shuffled orders** (exercising the causal buffer);
- assert **every replica converges to an identical final document**, over 150 iterations.

There are also exhaustive unit tests for every transform/merge pair — insert/insert, insert/delete,
delete/delete, at the same position, adjacent, and overlapping — plus causal-buffer tests for
out-of-order arrival and server sequence assignment under concurrent submits. The JavaScript client
port of RGA is independently checked for convergence under 200 shuffled permutations.

If you change the CRDT and this property test still passes with the fixed seed, convergence still
holds for the tested space. If you break commutativity, it fails loudly.

---

## 6. Summary: the honest one-paragraph version

Collab guarantees that **all replicas of a document converge to the same state** given the same set
of operations, that **no accepted edit is lost for text**, that **causality is respected**, and that
**the server holds a single authoritative, replayable history**. It does **not** guarantee semantic
or intention-level merge, it can **interleave** concurrent typed runs in text, it **drops the loser**
of a concurrent single-field write in structured documents, and it uses **logical (not wall-clock)**
ordering. Those are deliberate, documented trade-offs — not bugs — and they are the difference
between an honest CRDT implementation and a "pretend Google Docs".
