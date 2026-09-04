# The bug class your tests cannot see

Of the ten nondeterminism patterns I tested, four were caught immediately by replaying in
the same process. Those are fine. A guard fires, the run fails loudly, someone fixes it.

Two were not caught, and were still wrong.

`branch-on-wall-clock` reads `Date.now()` and branches on it. Replay it on the next line
of the test and it passes — because the replay happens in the same millisecond, reads the
same clock, and takes the same branch. Replay it an hour later, during an actual recovery,
and it takes the other branch and the run collapses.

I want to be precise about how I found this, because the discovery is more interesting
than the finding.

The first version of the experiment used the real clock. The result was **flaky**. Run the
suite on an idle machine and `branch-on-wall-clock` came back clean; run it while something
else was compiling and it came back detected. I spent a while assuming I had a bug in the
harness before realising that the flakiness *was the measurement*.

Because that is exactly what this bug class does in a codebase. It does not present as a
failure. It presents as a test that goes red on CI about one time in twenty, gets
investigated for ten minutes, gets a `retry: 3` annotation, and is then permanently
load-bearing in production. The engineering culture around flaky tests — quarantine it,
retry it, move on — is precisely the wrong response to this specific signal, and there is
no way to tell it apart from a genuinely flaky test without knowing what you are looking
at.

So the experiment now freezes the clock in the in-process mode, on purpose. That makes the
result deterministic and it makes the claim exact: **an immediate replay cannot see this
bug**, and the in-process column is a lower bound on what a test suite catches. A slow
enough suite catches more, by accident, unrepeatably.

---

There is one worse category. `clock-read-not-branched-on` reads the clock and puts the
value in the output without branching on it. The sequence of steps is identical on replay.
Every guard the runtime has compares the *operation sequence* against the journal, so
every guard passes. The run completes. The answer is different.

Nothing can detect this from inside the runtime, and I want to be clear that this is not a
gap I intend to close. A guard that compared step *results* would have to re-execute the
step body to have something to compare against, and re-executing step bodies is the exact
thing durable replay exists not to do. The defence is structural, not detective: put the
clock read inside a step, where it gets journalled, which is the rule from the previous
essay.

---

The taxonomy that comes out of this is what I would actually put on a slide:

| class | caught by | what it feels like |
| --- | --- | --- |
| **detectable** | a replay, immediately | a loud failure; fine |
| **latent** | a replay after time passes | a flaky test |
| **silent** | nothing | a wrong answer, months later |
| **inert** | n/a | not actually a bug |
| **prevented** | n/a | the API does not allow it |

Four of the ten were detectable. That is the column everyone's test suite covers, and it is
the least interesting one. The engineering effort belongs in moving things from *latent*
and *silent* into *prevented*, and the only way to know which column you are in is to write
the bug deliberately and watch.
