# 02 — The bug, and why it is a good one

## The protocol

An ABD-style quorum register over `n` replicas, majority = `⌊n/2⌋+1`. Each
replica holds `(timestamp, value)`, timestamps are `(counter, node_id)` so the
order is total.

**Write(v).** Query all replicas; on a majority of responses take the highest
timestamp seen, pick `(max.counter + 1, me)`, store `(ts, v)` to all replicas,
return once a majority acknowledges.

**Read.** Query all replicas; on a majority of responses take the value with the
highest timestamp; **store that pair back to all replicas and wait for a
majority**; then return it.

That second phase of the read is the whole subject of this document.

## The change that looks free

The read already knows the answer after phase 1. It has heard from a majority;
quorums intersect; whatever the highest timestamp among them is, it is at least
as new as any completed write. Phase 2 writes back a value that is already
there. It doubles read latency and doubles read-path load.

Deleting it is a one-line change, and every hand-written test still passes.

## Why it is wrong

Quorum intersection guarantees a read sees any **completed** write. It says
nothing about a write that is still **in flight**.

Consider three replicas and a write that has reached only replica B:

| | A | B | C |
|---|---|---|---|
| state | `(1, old)` | `(2, new)` | `(1, old)` |

The write has not returned — it has one ack, not two.

- **Read 1** gets responses from A and B first. Highest timestamp is B's. It
  returns `new`.
- **Read 2**, strictly later in real time, gets responses from A and C first.
  Highest timestamp is `(1, old)`. It returns `old`.

Both reads followed the protocol exactly. Both saw a majority. And the register
went backwards in real time, with no intervening write. There is no total order
of these operations consistent with real-time precedence, so the history is not
linearizable.

Phase 2 fixes it by making the read's observation durable before returning: once
Read 1 has written `new` back to a majority, Read 2 cannot miss it.

## Why it is a *good* bug to demonstrate on

**It is real.** This is why ABD has a write-back phase. It is a standard result
and it is also a standard thing to get wrong when someone profiles the read path
and finds a redundant round trip.

**It is invisible when things are healthy.** Measured: 0 violations in 2,000
runs on a perfect network. It needs a write to be partially propagated *and*
stay that way long enough for two reads to straddle it. On a fast, reliable
network, writes propagate to everyone in one latency and the window barely
exists.

**Every individual client sees something sensible.** Read 1's client sees a
value. Read 2's client sees a value. Neither client, on its own, observes
anything anomalous. Only a global real-time view across clients reveals it —
which is precisely why per-client assertions and single-client tests will never
catch it, and why the checker needs the oracle clock (ADR 0003).

**It fails an assertion nobody writes by hand.** "The register never goes
backwards across clients" is not a natural thing to write down. Linearizability
is, and it implies it.

## What the harness reports

```
seed 89
  network: 213 delivered, 7 dropped, 4 duplicated, 20 reordered, 56 partition changes
  history: 16 operations (14 completed, 2 abandoned)
  NOT LINEARIZABLE (operation 8)
  read by client 0 returned 62 at t=118049, but by the time it was invoked
  (t=71321) the completed writes were [12, 62]; no total order over the
  remaining operations makes that value current
     op10  client2      54155..64365      Read(53)
     op14  client2      66365..75327      Read(53)
  >> op8   client0      71321..118049     Read(62)
```

Client 2 read `53` twice, finishing at t=75327. Client 0's read was invoked at
t=71321 and returned `62` — a value written earlier — at t=118049. The register
moved backwards.

Note `op3 client3 412..pending Write(53)`: the write of 53 never completed. The
checker allows a pending operation to be placed anywhere or nowhere, which is
what makes the verdict trustworthy — it did not assume the convenient case.

## The protocol details that are not the bug

Worth listing, because each was a place a lazier implementation would have
produced a *false* violation and sent someone chasing a phantom:

- **Duplicate responses are deduplicated by sender.** The network duplicates
  messages. Counting a duplicate towards a quorum would let one replica form a
  majority alone.
- **Requests carry ids.** A response to an abandoned operation must not be
  counted towards the current one.
- **Replicas apply stores strictly-greater-than.** A delayed or duplicated older
  store must never overwrite a newer value.
- **Timed-out operations stay in the history as pending.** Dropping them would
  hide real violations; asserting they took effect would invent fake ones.
