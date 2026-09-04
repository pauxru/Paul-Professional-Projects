# Eleven bugs the experiment found

None of these were found by a test written to find them. Every one surfaced
because a number came out wrong, or came out *too clean*, and the only way to
explain the number was to go and look.

That is the argument for building a measurement harness rather than a test
suite. A test suite asks "does this code do what I said it does". A
measurement harness asks "is the quantity I am reporting the quantity I think
I am reporting", and those are different questions with different failure
modes. Eight of the eleven below are invisible to any assertion you would
think to write in advance, because the code did exactly what it said — it just
said the wrong thing.

They fall into four families.

---

## Family 1: the measurement consulted the thing it was measuring

The most dangerous class, because the results look *better* than reality and
every internal consistency check passes.

### Bug 5 — the attacker was scored using the defender's decoder

`SimulatedAgent._base` computed the model's comprehension penalty for an
obfuscated payload from `normalize(attack.payload).decoded` — the defender's
normaliser. If the normaliser recognised an encoding, the attack was scored as
legible; if it did not, the attack was scored as *illegible* and penalised.

Read that the other way around. Any encoding the **defence failed to
recognise** was scored as harder for the model to read, purely because the
defence failed to recognise it. The simulated attacker was punished for
succeeding. Section 5's entire thesis — that obfuscation trades detection
evasion against comprehension — was being manufactured by the coupling rather
than measured through it. rot13, which the normaliser does not handle, came
out as one of the *weakest* attacks for exactly the wrong reason.

The fix separates the two ideas that had been conflated. Legibility is now a
property of the attacker's encoding, declared in `corpus.ENCODING_LEGIBILITY`
and carried on `Attack.legibility`. `target.py` no longer imports `normalize`
at all, and `tests/test_target.py` parses the module's AST to assert it never
will again — a substring check would pass on the comment that explains the
bug.

### Bug 2 — the attribution machinery detected a defect in the thing it was attributing

`Layer.NORMALIZE`'s Shapley value came out at exactly `0.0`. Not near zero —
`0.0`, and `0.0` again in every pairwise interaction.

A layer with genuinely no effect scores *near* zero, with floating-point dust
from the factorial weights. Exact zeros across a 32-subset enumeration mean
the layer is not in the computation. It wasn't: the pipeline normalised the
prompt handed to the model, but the classifier re-read `attack.payload` from
the corpus, so normalisation could not affect the one thing that consumed it.

The Shapley decomposition found a bug in the pipeline. The fix threads
`use_normalizer` into `classify()`, and the corrected value is 1.7% solo
against a **4.7% interaction** with the classifier — the signature of a
preprocessing step, almost nothing alone and a great deal in company. That
shape is invisible to a sequential waterfall, and a team that measured
normalisation last would have deleted it as worthless.

### Bug 10 — the simulator matched tool names against the ciphertext

`_compliant_calls` decided which tool the model would invoke by searching the
raw payload for the tool's name. For the obfuscated family the payload *is*
the encoding, so `"delete_record" in payload.lower()` was false, and an
obfuscated tool attack that the model had **already been judged to
understand** emitted no tool call at all.

The consequence was quiet and bad: the broker was never tested against the
attacks specifically constructed to slip past lexical defences. Its numbers
were real but its exposure was not.

The fix could not be "decode the payload in the target" — that is bug 5 again.
Instead `Attack.plaintext` records what the attacker wrote before encoding it,
and the target dispatches on `attack.intent`. The attacker knows their own
intent; `legibility` already prices in how likely the model is to recover it.

---

## Family 2: absent, empty or missing was treated as fine

Three instances of the same reflex, at three different layers. Each time the
code read naturally and each time it defaulted to permission.

### Bug 6 — the empty string was the most trusted input in the system

`Tainted.trust_of_substring("")` returned `Trust.SYSTEM` — the **highest**
trust in the lattice. `"".find()` succeeds at index 0, the resulting
zero-width slice contains no characters, and the join of an empty set of
trust levels is the lattice's top element. Vacuously true, catastrophically
wrong: every step in the derivation is correct.

### Bug 7 — the same reflex one layer up

Found immediately after fixing bug 6, by going looking. The broker's argument
loop began `if not value: continue` — skip empty arguments, they cannot carry
anything. So `send_email(to="")` was **allowed**, having been checked by
nothing. An empty sensitive argument is now `unattributable`, which denies.

### Bug 11 — an audit log that was always empty

`RunResult.broker_log` is a public field, typed as a `BrokerLog`, offered to
callers as the record of every authorisation decision in the run. `Pipeline.run`
never wrote to it. Any report section that had trusted it would have concluded
the broker never ran — and would have said so with a citation.

This one is the reason section 9 reads `outcome.decisions` rather than the
aggregate log. It got the right answer by not using the broken thing, which is
luck, not design.

The general shape: **a lookup that returns nothing is not a lookup that
returns "safe"**. The broker's rules are now written as positive requirements
— an argument must be *shown* to originate at or above the tool's bar, rather
than merely not shown to originate below it. Section 9 quantifies what that is
worth: 30% of argument-stage denials come from the unattributable rule, and a
broker without it would produce an audit log where every entry reads *allowed*
and every entry is truthful.

---

## Family 3: the configuration was compared against itself

Two sections were reporting a difference between two things that were the same
thing.

### Bug 3 — the delimiter flag never reached the agent

Section 8 compares forgeable delimiters against nonce delimiters. `Pipeline`
accepted `forgeable_delimiters`, used it to pick the delimiter *string*, and
never told the agent which kind it had been given. The agent's authority bonus
for a forged `</untrusted>` tag applied unconditionally.

So both arms of the comparison ran with the same compliance model, and the
section reported the identical number twice as evidence of no effect. It was
right that there was no difference. There was no difference because there were
not two configurations.

Corrected, the section has a real result: context-forgery ASR is 25.0% against
fixed delimiters and 8.3% against a nonce. The mechanism is worth stating —
an attacker cannot close a fence whose tag was chosen after their payload was
written.

### Bug 4 — requester trust was the text's channel, not the turn's principal

The broker decided whether a caller was authorised by looking at the arrival
channel of the *payload*. So an injected instruction inside a document was
evaluated as a request *from* an untrusted document, and was refused on the
channel rule alone.

That is not the threat. The threat is that the **user's own agent**, acting
with the user's authority, is induced to make a call. The real requester is
always the principal; the attacker controls the *arguments*, not the identity.

Conflating those made the broker look far stronger than it is, because every
injected tool call was rejected before anyone examined an argument. `Pipeline`
now takes an explicit `principal`, and the fix is what forced the
`authorised_tool` family into existence — six attacks aimed at a tool the user
is already entitled to use, where argument taint is the only control that can
possibly apply. Section 3's headline result exists because of this bug.

---

## Family 4: detecting a thing is not doing anything about it

### Bug 8 — control characters were flagged and then left in place

The normaliser counted C0 control characters and reported `control_char: 2`.
It did not remove them. Two `\x01` bytes inserted into a payload took the
classifier's score from **0.80 to 0.00** — below any threshold — while the
normaliser sat beside it truthfully reporting that it had found two control
characters.

Both components were working. The normaliser detected the evasion and said so.
The classifier scored the text it was given. Nothing connected the detection
to a consequence, and the flag made the failure *harder* to see, because the
diagnostic output looked correct.

### Bug 1 — the confusable folder mangled every non-Latin document

Applied to a benign Russian-language document, the folder rewrote Cyrillic to
visually similar Latin characters and scored it `confusable: 15` — a top-weight
structural signal with no benign explanation. The result was a benign document
blocked, with a plausible-sounding reason.

Deployed, this would have been an outage with a demographic. Every user
writing in a non-Latin script would be blocked; every user writing in English
would see a working product; and the bug report would say "sometimes it blocks
legitimate emails".

Folding is now scoped to words that already *mix* scripts, which is the actual
attack signature. A word entirely in one script is a word, whatever the
script. This one is in the corpus permanently as a benign document.

### Bug 9 — spotlighting was applied to the user's own turn

`Pipeline._build_prompt` wrapped **every** channel in the spotlight fence,
including USER. Spotlighting means "the block below is data, not
instructions"; applied to the principal's own message it instructs the model
to ignore the person it works for.

In measurement terms it credited the layer with suppressing direct
USER-channel attacks that no real deployment would ever wrap, inflating
spotlighting's Shapley value. The fix restricts it to content below
`Trust.USER`.

The interesting part is the aftermath. Spotlighting **still** takes the
largest Shapley value after losing the unearned credit, so section 4's
contradicted prediction survived its own correction — which makes it a
stronger finding than it was before, not a weaker one.

---

## What this cost, and the one that nearly ended the project

Roughly a third of the build. Sections 3, 4, 5, 8 and 9 were each rewritten
after a bug changed what the measurement meant — not the prose around the
number, the *argument*. Section 3 previously stated the opposite of what it now
claims.

There was also a twelfth incident that was not a bug in the system under test
but is worth recording, because it nearly destroyed the project. A scripted
edit passed `newline="\\n"` — a literal backslash-n — to `io.open(path, "w")`.
That is an illegal newline value and `TextIOWrapper` raises on it. But
`FileIO` **opens and truncates the file before** the wrapper validates its
arguments, so the exception arrived after the 44KB runner had already become
zero bytes. It was not yet in git.

It was reconstructed from `docs/results.md`, because the report contained the
full rendered prose of all twelve sections and the library's API surface could
be read back from the modules that survived. The artefact rebuilt its own
generator. That is an accident, but it is also an argument for reports that
carry their reasoning rather than only their numbers.

The rule now: commit before running any script that writes in place.

---

## The pattern

Ten of eleven are in code that ran without error and produced plausible
output. Not one would have been caught by type checking, and only bug 7 by a
conventional unit test.

What caught them was **arithmetic that had to add up**: a Shapley value that
was exactly zero, an ASR that did not move when a flag was flipped, a family
that reached 0.0% too easily, a rule table dominated by the wrong rule, an
audit log with no rows in it. Bugs 2, 3, 4, 5 and 11 were each found by a
number that was *too clean*.

The corresponding discipline is to be suspicious of good results, and
specifically of results that arrive without resistance. A defence that appears
to work perfectly is more likely to be disconnected than perfect.

---

## Postscript: the mutation that should not die

`test.ps1` stage 5 reverts five of these fixes and requires the suite to
notice. A sixth was tried and removed, and the reason is worth a paragraph.

Reverting bug 7 — restoring `if not value: continue` in the broker's argument
loop — leaves every test passing. That looks like a coverage gap. It is not:
bug 6's fix independently denies the same call, because `trust_of_substring("")`
now returns `None` and lands the argument in `unattributable`. Either guard
alone is sufficient, so removing one changes no observable behaviour. It is a
textbook **equivalent mutant**, and mutation testing is right to report it.

The redundancy is deliberate. The two bugs were found an hour apart, in
different modules, and the reflex behind them — *empty means there is nothing
to check* — recurred a third time in bug 11. One guard against a mistake that
demonstrably reproduces itself is not enough.

What the mutation check *did* do is force that reasoning to be written down.
`test_the_empty_argument_is_guarded_twice_on_purpose` now records the intent,
so a future reader who notices the branch is unreachable under test does not
helpfully delete it.

