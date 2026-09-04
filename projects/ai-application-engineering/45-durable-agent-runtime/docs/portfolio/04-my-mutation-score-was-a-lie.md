# My mutation score was a lie

`test.ps1` has a mutation stage. It takes eight load-bearing decisions in the source —
the journal's sequence-gap check, the positional idempotency key, the budget
re-accumulation, the late-signal read on resume — edits each one into a plausible
alternative, and requires the test suite to fail every time. A mutant that survives means
the tests describe the code's shape rather than its behaviour.

The first time I ran it: **8/8 killed**. Very satisfying.

It was completely false. Every mutant run had died with
`Error: Cannot find module ...\tests` before executing a single test, because I had
invoked `node --test tests/` and Node wanted a glob. Node exits non-zero on a module
resolution error. My harness checked `status !== 0` and called it a kill.

So the harness reported a perfect score while measuring nothing at all. Eight for eight,
on a suite that never ran.

---

What makes this worth writing down is not the mistake — it is an ordinary one — but the
shape of it. The metric failed in the direction that looks like success. A broken mutation
harness does not report 0/8 and demand attention. It reports 8/8 and gets pasted into a
README.

That is the same shape as three of the findings in this project:

- The naive budget counter fails by reporting a number that is **too high**, and aborting.
  Nobody reviews a budget check for being too strict.
- `escalate` fails by being **too cautious**, which turns into an unstaffed queue and a
  refund that never arrives.
- The latent nondeterminism class fails by making a test **flaky**, which gets a retry
  annotation rather than a fix.

Every one of them is a failure disguised as prudence. I do not think that is a
coincidence. Failures that look like carelessness get fixed, because someone is
embarrassed. Failures that look like caution get merged.

---

The fix in the harness is three lines: capture stdout, require it to contain a
`tests <n>` summary line, and refuse to score any run whose suite did not start.

```ts
const ran = /^(ℹ|#) tests \d+/m.test(run.stdout ?? '');
if (!ran) { /* not a kill -- a harness error */ }
```

The genuine score is still 8/8. It just means something now.

The general rule I take from it: **every quality metric needs a check that it was
measured**. A coverage number needs the instrumentation to have loaded. A mutation score
needs the mutants to have compiled and the suite to have run. A benchmark needs the
workload to have executed rather than been optimised away. In each case the failure mode
of the measurement is to report the best possible result, because "nothing happened" and
"everything passed" produce the same output unless you look for the difference.

I now hold the test harness to the same standard as the code it tests, which is why stage
5 of `test.ps1` checks the zero-dependency claim by scanning imports rather than by
asserting it in prose, and why the report generator's tests check that the scoreboard's
row count matches the number of predictions rather than trusting a hardcoded header.

The tests that check the tests are the ones nobody writes.
