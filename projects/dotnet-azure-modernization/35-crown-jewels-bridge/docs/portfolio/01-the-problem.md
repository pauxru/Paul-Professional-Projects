# 1. The building nobody is allowed to enter

Every modernisation programme meets one eventually.

There is a library at the centre of the business. It was written in 2009 by three people,
two of whom have retired and one of whom is now a managing director and does not answer
emails about C++. It has no test suite. It has no documentation beyond a header file with
comments in it. It is compiled by a build script that only works on one machine, and
somebody keeps a virtual machine image of that machine on a NAS.

And it is **right**. It has priced the book correctly for fifteen years. When the desk
disagrees with it, the desk is usually wrong. The number it produces is the number that
goes into the risk report, the number the regulator sees, and the number the traders get
paid on.

The mandate arrives: modernise the estate. Move to .NET 10, get onto Azure, containerise
the services, introduce CI/CD. And -- in the same sentence, from the same sponsor -- *do
not touch the pricing library.*

## The wrong two responses

Most engineers respond to this in one of two ways, and both of them are wrong.

**The first is to treat the constraint as ignorance.** Build the case. Explain technical
debt, quantify the bus factor, produce a slide showing the library as a red block in the
middle of the architecture diagram. Ask for six months to rewrite it in a modern language
with a test suite.

This fails, and it deserves to fail. The sponsor is not being irrational. They are
weighing a certain, quantified, catastrophic downside -- the book gets repriced wrongly
and nobody notices for a month -- against a benefit that is real but diffuse. Nobody has
ever been fired for refusing to rewrite the pricer. The constraint is not ignorance; it
is a correct risk assessment that the engineer has not yet engaged with.

**The second is to accept the constraint and route around it.** Leave the library where
it is, on the old server, and call it over HTTP from the new system. Wrap it in a
service, put a queue in front of it, and declare the boundary a network boundary.

This fails more quietly. You have not removed the risk, you have added latency, a
serialisation format and an operational dependency to it. The library still crashes on
the same inputs; now it crashes behind a load balancer, and the failure surfaces as a
timeout in a system three hops away. The 2009 code is now *harder* to reason about,
because its failures have been laundered through infrastructure.

## The question nobody asks

Here is the thing that took me a while to see, and that this project exists to
demonstrate rather than assert:

**Where, exactly, is the danger?**

Everybody assumes it is in the 60,000 lines. That assumption is why the constraint feels
like a wall. If the danger is spread through the code, then making it safe requires
reading the code, and reading the code means understanding it, and understanding it means
the six-month rewrite nobody will fund.

But that assumption has never been tested. It is inherited. Somebody said it in a meeting
in 2014 and it has been true ever since by repetition.

So: test it.

## The experiment

Build the same C++ engine into three DLLs. Compile `engine.cpp` from the same file, with
the same flags, into all of them. Change one thing -- a preprocessor definition that
controls a few hundred lines in a *different file*, `abi.cpp`, which is the thin C layer
the outside world calls through.

Then throw six hundred hostile inputs at both and count what happens.

| outcome | 2009 boundary | hardened boundary |
|---|---:|---:|
| refused with a status code | 0 | 446 |
| **wrong answer, success code** | 263 | 0 |
| **wrote past the caller's buffer** | 7 | 0 |
| **killed the process** | 58 | 0 |
| **unsafe outcomes** | **328** | **0** |

`engine.cpp` is byte-identical in both.

Not one line of the crown jewels was audited, reviewed, or changed. The entire difference
is argument validation, a capacity check and an exception barrier in the three hundred
lines the library is *reached through*.

## What this changes about the conversation

The sponsor's constraint and the engineer's goal turn out not to be in conflict at all.
They only looked like it because both parties believed the danger was in the part nobody
is allowed to touch.

It is not. It is in the part that nobody thinks about, that has no owner, that was
written last, in an afternoon, by whoever needed to call the thing from Excel. The C ABI
is not part of the crown jewels. It is scaffolding somebody nailed to the side of the
building in 2009 and never came back for.

You are allowed to work on scaffolding.

## The finding underneath the finding

Look at the shape of the 328 failures again.

Fifty-eight killed the process. Two hundred and sixty-three returned a **wrong answer
with a success code**.

The crashes are the safe ones. A process that dies at 09:14 has someone looking at it by
09:20, with a stack trace and a blast radius of one process. A negative option price
returned as `PJ_OK` flows into the position, the position flows into the risk report, the
report passes a limit check, and the trade settles. It surfaces three weeks later in a
reconciliation, as a difference of pennies across a book of thousands of trades, and the
investigation starts nowhere near the pricing library.

This has a consequence for how the 2009 code got to be the way it is. The crashes would
have been fixed. They announce themselves; somebody hits one, somebody patches it. What
survives fifteen years of production use is precisely the class of defect that never
announces itself -- and that is why a library with a spotless operational record can be
sitting on 263 silent ways to be wrong.

The absence of incidents is not evidence of safety. It is evidence that the failures you
have are the quiet kind.

---

*Next: [how it is built](02-how-it-is-built.md) -- three DLLs from one source file, and
why the host had to be split in two.*
