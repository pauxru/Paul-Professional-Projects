# Bugs the checker found

Six of these are defects in the *model*, not in the saga being modelled. That
distinction matters more than it sounds. Every one of them made the checker
report a violation that was not real, or -- worse, twice -- miss one that was.
Each took a round of calibration to see, and each corresponds directly to a
mistake that is easy to make when writing a real orchestrator.

They are in the order they were found.

---

## 1. One bit per step is not enough to remember whether it ran

The state included a bitmask: step *i* had landed, or it had not. This is the
obvious representation and it is wrong.

A non-idempotent step can land **twice**. The orchestrator calls it, the call
times out, the orchestrator retries, and now the effect has been applied twice
while the orchestrator believes it may have been applied zero times. Roll back,
run the compensation once, and the bit flips to "not present" -- while the effect
is still there, once over.

The checker then reported that the compensation was *not neutral* on a subsequent
step, which was a lie: the compensation was fine, the bookkeeping was not.

Fixed with a two-bit saturating counter per step, packed into an `int`. Two bits
because the interesting distinction is zero / one / more-than-one, and a third
value adds states without adding meaning. `Config.MaxSteps` is 15 as a direct
consequence, and the `Saga` constructor refuses a longer saga rather than
silently corrupting a neighbour's counter -- a failure that no property could
have detected, because the state space would have been wrong rather than the
saga.

**The real-system version of this bug:** a `bool CompletedSteps[i]` in an
orchestrator's durable state, which is what most of them have.

---

## 2. A retriable step that gives up is not retriable

Forward steps have an attempt cap; that is what makes a saga abort instead of
hanging. The cap was applied uniformly, including to steps declared retriable.

Retriable steps sit *after* the pivot and have no compensation -- that is what the
declaration means. So when one exhausted its attempts, the rollback machinery
reached a step with no inverse, and the checker reported a `StuckState`.

The counterexample was correct about the model and meaningless about the world.
The saga had not gone wrong; the model had allowed a step to do something its own
declaration says it cannot do.

Fixed by weak fairness: at the attempt cap, a retriable step emits only the
succeeding transition. It may fail any number of times, but it may not fail
forever. This is exactly the assumption the word "retriable" encodes, and writing
it down in the transition relation is the point at which it stops being a
comment.

---

## 3. A rejection is authoritative about the attempt, not about the step

When step *i* was definitively rejected, the orchestrator rolled back starting at
*i-1*, reasoning that *i* had not happened so there was nothing to undo.

That reasoning holds for *this attempt*. It does not hold for the step. A
previous attempt may have landed and lost its acknowledgement, in which case the
rejection is the provider saying "no" to a duplicate it has already accepted --
and rolling back from *i-1* strands the effect of the earlier attempt.

The checker found this as a `DirtyAbort` at depth 3: timeout on step *i*, retry,
rejection, roll back, residue.

Fixed by starting rollback at *i* unless the orchestrator has positive evidence
that nothing landed -- either the step is queryable, or attempts are journalled
and the counter is genuinely zero.

**This is the single most transferable finding in the repository.** The
distinction between "this call was rejected" and "this step did not happen" is
invisible in a monolith, because there is no attempt-versus-step distinction when
there is no network.

---

## 4. Intent must be journalled before the call, not after

Fix 3 introduced a dependency on the attempt counter meaning something after a
crash. It did not, because the counter was incremented when the call *returned*.
Crash between sending the request and recording the attempt, and recovery sees
zero attempts for a step that has already been called.

Fixed by having crash recovery resume with `Attempts = max(Attempts, 1)` when
journalling is enabled, which models writing intent before the call.

And then, three sections later in `docs/results.md`, the measurements say this
mechanism fixes **nothing**. Turning journalling off changes no violation count
anywhere -- on the broken v4 or the correct v7 -- because by the time the rollback
scope was corrected in fix 3, the journal had nothing left to protect. It is a
correct mechanism for a problem that a different fix had already made
unreachable.

It is still in the code, with a switch, because that is the only reason the
finding is visible at all. The usual outcome is that such a mechanism is added,
is never re-measured, and is maintained forever.

---

## 5. A status query tells you about the step, not the attempt

Making the pivot queryable introduced a transition: ask the provider whether the
step happened, and if the answer is no, roll back. The transition was generated
whenever the current attempt was uncertain.

Same error as 3, in the opposite direction. The query answers a question about
the *step*. If an earlier attempt landed, the honest answer is "yes, it
happened", and the saga must roll **forward**.

Fixed by generating the "nothing happened, roll back" transition only when the
landing counter is genuinely zero. The symmetry with fix 3 is not a coincidence:
both are the same confusion between an attempt and the step it is an attempt at,
and finding it twice in different clothes is what convinced me the distinction
deserved to be in the state vector rather than in my head.

---

## 6. Two properties that nothing could violate

Not a modelling defect -- a hole in the safety net, found by a test written to ask
an unusual question: *is every property reachable from some design?*

The answer was no. Two of the seven `PropertyKind` values could not be violated
by anything in the catalogue or the mutation set:

- `DeclaredIdempotenceIsFalse`
- `ReverseOrderDoesNotRecover`

Both were real checks. Both were correctly implemented. `ReverseOrderRecovers`
was a complete, working, well-commented function that **was never called from
anywhere in the codebase**. It had been written, reviewed by nobody, and left.

A property nothing can violate provides no safety while looking exactly like
safety in the property list. Fixed by wiring the algebraic check into `Check` as
a pre-exploration pass and adding two mutations that inject defects the catalogue
never had:

- a step whose idempotence *declaration* is contradicted by its own effect
- a compensation that releases its neighbour's resource as well as its own

The second is the more interesting design. `AllocateSlot` reads the reservation
and writes a slot; its compensation releases the slot and also the reservation,
which belongs to `ReserveStock` and gets released again when rollback reaches it.
Every per-step property is satisfied -- neutral, idempotent, undoes its own effect
-- and only the *composition* is wrong. The only thing that sees it is running the
committed prefix backwards as pure algebra: microseconds, one line of output, and
it was sitting in the repository unreferenced.

---

## What the pattern is

Five of the six are the same mistake wearing different clothes: **conflating an
attempt with the step it is an attempt at.** In a monolith the distinction does
not exist, because a function call either returns or throws and there is no third
outcome. Across a network there is always a third outcome, and every piece of
state that says "did this happen?" has to decide which question it is answering.

The sixth is a different lesson, and a more uncomfortable one: the checker was
confidently reporting `Safe` on designs it had no ability to reject. It took a
test asking whether the *tests* were capable of failing to notice.
