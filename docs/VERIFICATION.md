# Verification

Every number in the root README is produced by a command, not typed by a person.
This file explains which command, and what it found.

## How it works

```powershell
pwsh tools/verify.ps1
```

The sweep walks `projects/**`, and for each project runs **the project's own test
script** — not a generic runner that happens to know about xUnit. That distinction
matters. A project's `test.ps1` does more than run tests: it fails the build on a
compiler warning, regenerates the project's results document and compares it byte for
byte against the committed copy, and mutates the source to prove the tests would have
caught the mutation. A sweep that only ran `dotnet test` would report green on a project
whose headline results document no longer matched the code that produced it.

For each project the sweep records status, tests passed, tests failed, wall-clock
seconds, which runner it used, and which parser read the count:

- `tools/verification.tsv` — one row per project, committed
- `tools/logs/<project>.log` — the complete stdout of that run, committed

`tools/gen_readme.py` and `tools/gen_assets.py` read that TSV. If the sweep has never
been run, `gen_assets.py` refuses to render a banner rather than fall back to a
remembered figure.

## Counting tests honestly

Counting tests turned out to be the part with the sharp edge, and two of the bugs were
in the counter rather than in any project.

**`go test` without `-v` prints one line per package, not per test.** A passing Go
project therefore reports either zero tests or — worse — a small number picked up from
whichever few packages happened to run verbosely. Project 39 reported **3 tests** for a
suite of several hundred. Nothing about "3" looks wrong on the way into a README. The
sweep now treats *fewer tests than packages that reported `ok`* as proof that the tally
is not a test count, and re-runs the suite verbosely purely to count it.

**Summing every "N passed" in a log double-counts re-runs.** Project 43 deliberately
runs a subset of its suite twice, in two different orders, to prove that its
session-scoped fixtures are not mutated by the tests that share them. Adding up the
three pytest summaries gives 680 for a suite of 366. Project 41, meanwhile, runs two
genuinely different suites whose totals *should* be added. The two cases are
indistinguishable from stdout.

So the harnesses were changed rather than the parser: every project in tracks 2 and 3
now ends with a line stating its own total, which the sweep prefers over anything it
could infer:

```
all 6 stages passed -- 206 tests, 7/7 mutations killed, 150.0s
```

That is the right place for the knowledge. The script knows which of its stages are
re-runs; a regular expression reading its output never will.

## What the sweep found

Running all 47 built projects on a machine, on a date, that none of them were written
on turned up four defects that no amount of reading would have found.

### Two frozen-clock time bombs (projects 04 and 24)

Both projects issue JWTs from an injected `IClock` so that tests can freeze time. Both
then let the token's `NotBefore` and `IssuedAt` default to ambient `DateTime.UtcNow`.

With the test clock frozen at `2026-09-03` and the machine clock reading `2026-09-04`,
every issued token had `Expires` a day *before* its `NotBefore`. `IDX12401`. Every
authenticated integration test returned 500.

These suites passed for months. They pass on the day they are written and every day
until the frozen date falls behind the wall clock, and then they fail everywhere at
once, in a way that reads like a broken auth stack rather than a broken test fixture.
The fix is the general one: if a component takes a clock, it must take *all* of its
time from that clock, and the validator on the other side has to be given the same
clock — a `LifetimeValidator` reading `DateTime.UtcNow` re-introduces the bug at
verification time even after the issuer is fixed.

There is a second, smaller lesson in project 04. The test helper wrapped every request
in `EnsureSuccessStatusCode()`, which throws away the response body — so fifteen
distinct failures produced fifteen identical, contentless stack traces. The server had
been explaining the problem in the body the whole time. That helper now includes the
status and the body in the exception.

### A determinism claim that was only true at one parallelism (project 42)

The semantic cache ships a report that its own test suite re-generates and compares
byte for byte, and that check had always passed. It failed in the sweep — because the
sweep's preceding stage left `GOMAXPROCS=8` set, and one number in the report changed:
*late joiners* went from 1 to 18.

The demo builds a thundering herd of 32 subscribers against one in-flight request and
counts how many arrive after the first chunk is emitted. It was commented
"deterministic by construction". It was not. `Do` launches the leader's work in a
goroutine, which then races the loop that is still registering the other 31
subscribers. How many count as "late" depends on the scheduler.

This is the failure mode worth naming: it never crashes, never throws, and never
produces an implausible number. It quietly changes a measurement in a document whose
entire purpose is to be trustworthy. The fix adds a gate so the leader cannot emit
until the herd is fully subscribed — the ordering the counters depend on is now a
happens-before edge rather than a scheduling accident. The report is now identical at
`GOMAXPROCS` 1, 4, 8, 16 and default.

### A test script that could not have run (project 46)

`test.ps1` refuses to run against a missing binary rather than testing whatever was
left over from a previous build. That is correct behaviour, and it means the sweep has
to build the project first. It now runs `build.ps1` when one exists.

## Reproducing

```powershell
pwsh tools/verify.ps1                      # everything, roughly 45 minutes
pwsh tools/verify.ps1 -Filter '^4[0-9]-'   # one track
python tools/build_catalogue.py            # rescan the tree
python tools/gen_assets.py                 # redraw the diagrams
python tools/gen_readme.py                 # rewrite README.md
```

The sweep requires **PowerShell 7** (`pwsh`) and exits rather than continuing under
Windows PowerShell 5.1: several harnesses use `SHA256.HashData`, which does not exist
there, and the resulting `MethodNotFound` five stages into a forty-minute run is a
worse outcome than refusing to start.

See [`TOOLCHAINS.md`](TOOLCHAINS.md) for the compiler and SDK versions everything was
verified against.
