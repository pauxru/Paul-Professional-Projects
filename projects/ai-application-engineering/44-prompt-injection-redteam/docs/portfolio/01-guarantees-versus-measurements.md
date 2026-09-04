# A blocked percentage is not a security property

The standard artifact of a prompt-injection evaluation is a percentage. "Our
defences block 94% of injection attempts." It is the number that gets into the
slide, and it is close to meaningless — not because it is inaccurate, but
because it is a measurement of two entirely different kinds of thing, added
together and reported as one.

## Two kinds of defence

Consider two controls.

The first is a classifier. It reads incoming text, scores it, and rejects
anything above a threshold. Its output is a probability reduction: attacks that
used to work now work less often.

The second is a capability broker. It sits *after* the model, examines each
proposed tool call, and refuses any call whose arguments cannot be traced to
the principal. Its output is a property: **no tool call executes unless the
principal asked for it.**

Both might contribute 20 points to a blocked percentage. They are not
comparable, and the reason is what happens when you change the model.

The classifier's 20 points were measured against one model's tendency to
comply. Swap the model — a new version, a cheaper vendor, a temperature
change, a longer context that dilutes the system prompt — and the 20 points
are a number from a previous experiment. Nobody re-runs the eval. The slide
stays.

The broker's 20 points do not depend on the model at all. The model can be
maximally compliant, adversarially aligned, or replaced wholesale, and the
statement holds, because the broker never asks the model anything. It examines
a concrete proposed action and a provenance record.

Averaged into a single percentage, these become indistinguishable.

## Separating them mechanically

The way to tell them apart is not to argue about it. It is to **sweep the
model across its entire range of behaviour and see which lines are flat.**

This project parameterises its simulated target by a single susceptibility
knob: the probability that a plainly-worded injection on an untrusted channel
is obeyed with no defences applied. Then it runs the whole corpus at
susceptibility 0.0, 0.5 and 1.0 — a model that never complies, a coin flip,
and a model that always complies — and reads the columns.

For the family designed to defeat a broker:

| defence | susceptibility 0.0 | 0.6 | 1.0 |
|---|---|---|---|
| none | 0.0% | 83.3% | 100.0% |
| behavioural stack | 0.0% | 50.0% | 83.3% |
| capability broker | 0.0% | 0.0% | 0.0% |

The behavioural row tracks the model. That is what a probability reduction
looks like: it works when the model was going to mostly behave anyway, and it
degrades exactly when you need it. The broker row is flat. That flatness is
the property, and it is visible without any argument about mechanisms.

This is a general technique and it is cheap. If you can parameterise the thing
your defence is defending against, sweep it. Whatever stays flat is a
guarantee.

## The family that makes the test honest

The first version of this experiment produced a broker that looked
extraordinary — it blocked every tool-call attack in the corpus. That should
have been suspicious, and it was: every injected tool call was arriving from
an untrusted document and asking for an operator-level tool, so every one was
refused on the channel rule before anyone examined an argument.

That is not the interesting threat. The interesting threat is that the user's
own agent, holding the user's authority, is induced to make a call the user
did not intend. The channel is legitimate. The requester is legitimate. Only
the *arguments* are the attacker's.

So the corpus gained a family — `authorised_tool` — aimed at tools the user is
already entitled to use. The user asks the agent to send an email; the
injected document changes the recipient. The channel check is blind to this by
construction, and argument provenance is the only control that can apply.

Adding it made the broker look weaker on paper and made the result mean
something. Section 3 is now a claim about the mechanism that survives contact
with the case it was designed for.

## The boundary, stated exactly

Under the full sweep, one tool call gets through the broker. It arrives on the
USER channel: the operator typed it themselves.

That is not a miss. Allowing it is correct — an authorisation system that
refuses the principal's own requests is not a security control, it is an
outage. But it is the boundary of the guarantee, and stating it precisely is
what turns a number into a property:

> A capability broker reduces the tool-call attack surface to exactly the set
> of actions the principal asked for themselves.

Notice what that sentence does not say. It does not say the agent is safe. If
the principal can be socially engineered, the broker enforces the engineering
faithfully. It says something narrower and much more useful: **the injection
in the document cannot expand the action set.** Whatever the agent does, a
human asked for it.

## What this changes about how you deploy

If you accept the split, the deployment order follows. Structural layers
first, because their value is portable across models and does not need
re-measuring. Behavioural layers after, as depth, with the understanding that
their contribution is a number from a specific experiment on a specific model.

And when someone asks for the blocked percentage, the answer is a table with
two sections, and the sections are labelled.
