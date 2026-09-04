# The failure mode of a taint system is a clean audit log

Taint tracking for LLM agents has an obvious design. Label every span of the
prompt with where it came from. When the model proposes a tool call, look up
each argument in the prompt, find the span it came from, and check that span's
trust level against the tool's requirement.

It is a good design. It is also one line away from being a control that logs
beautifully and stops nothing, and the line is the one nobody writes
deliberately.

## The line

```python
origin = prompt.trust_of_substring(value)
if origin < tool.required_trust:
    return deny(...)
```

Read it. It is correct-looking, it is the shape every code reviewer expects,
and it is what you write if you are thinking about the case where the lookup
succeeds.

Now the case where it does not. The model was asked to summarise a document
and send an email. It complied, and it *restated* the injected recipient in its
own words — a normalised address, a re-cased string, a paraphrase. The argument
does not appear verbatim in the prompt. `trust_of_substring` finds nothing.

What does it return? In the natural implementation: nothing. `None`, or a
default, or — in this codebase's first version, and this is the good part —
`Trust.SYSTEM`, the *highest* trust in the lattice.

That last one is not a typo. `"".find()` succeeds at index 0. The resulting
zero-width slice contains no characters. The meet of an empty set of trust
levels is the lattice's top element. Every step is correct. The composition
says the empty string is the most trusted input in the system.

Whatever it returns, the comparison `origin < required` is false, and the call
is allowed. **The check ran. The check passed. The check examined nothing.**

## Why it survives review

The audit log is the reason.

Every denied call has an entry with a rule name and a reason. Every allowed
call has an entry too. An operator reviewing the log sees a working control:
denials where you would expect them, allows where you would expect them, all
with plausible reasons. The paraphrased calls appear as *allowed*, and the
entry is truthful — the broker did allow them.

There is no error. There is no exception. There is no missing log line. The
system does not have a bug in the sense of producing wrong output; it has a
bug in the sense of a question that was never asked being recorded as a
question that was answered satisfactorily.

This is the specific failure mode of provenance-based controls and it is worth
naming: **a lookup that returns nothing is not a lookup that returns "safe".**

## The fix is a change of polarity

Write the rule as a positive requirement rather than a negative one.

Not: *deny if the argument is shown to come from an untrusted span.*

But: *allow only if the argument is shown to come from a span at or above the
tool's bar.* Anything else — untrusted origin, no origin, an origin the
tracker could not determine — is a denial, under a rule with its own name:
`unattributable-argument`.

The name matters. It means the log distinguishes "I found the argument and it
came from a document" from "I could not find the argument at all", and those
are different operational situations. The first is an attack. The second might
be an attack, or might be your model paraphrasing normally, and if it is
happening constantly you have a design problem you need to see.

## What it is worth

Section 9 measures it on the subset that matters — tool calls where the
requester *is* authorised for the tool, so the channel check is blind by
construction and only the argument rules can act:

| rule | denials | share |
|---|---|---|
| argument-taint | 7 | 70.0% |
| unattributable-argument | 3 | 30.0% |

Eleven proposed calls reached the argument stage. Ten were denied there. Three
of those ten — **30%** — were caught by the rule that exists solely to handle
"I don't know".

A broker written the natural way allows those three. Its blocked percentage
drops from 90.9% to 63.6% on the subset where it is the only defence. And
nothing in its output indicates that anything went wrong.

## The same reflex, three times

Once you have seen this shape you find it everywhere, and I did, in this
codebase, within an hour:

- `trust_of_substring("")` returning the top of the lattice, because a
  zero-width slice joins to top vacuously.
- The broker's argument loop opening with `if not value: continue` — skip
  empty arguments, they cannot carry anything — so `send_email(to="")` was
  allowed having been examined by nothing.
- `RunResult.broker_log`, a public field typed as the record of every
  authorisation decision in the run, that `Pipeline.run` never wrote to. It was
  always empty. Any analysis that had trusted it would have concluded the
  broker never ran, and would have cited an audit log to prove it.

Three different layers, three different authors' worth of reasoning, one
reflex: **absent, empty, or unknown was treated as fine.**

The reflex has a cause. Each of these reads as a *lookup* — find the span, get
the value, fetch the log — and a lookup that comes back empty feels like the
uninteresting case. It is the interesting case. In a security control it is
the only case that matters, because it is the one an adversary can arrange.

## The general rule

For any check whose result feeds an authorisation decision, ask what happens
when the check has no information. If the answer is "it passes", the check is
decorative, and it will produce an audit trail indistinguishable from a
working one.

Then write a test for it. Not a test that the check works — a test that the
check *denies when it cannot answer*. In this repository that test is called
`test_unattributable_argument_fires_when_text_is_absent`, and it is worth more than the
thirty tests around it that verify the happy path.
