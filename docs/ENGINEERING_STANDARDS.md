# PRINCIPAL BUILD SPEC — projects 31–50

**This document is binding.** It extends `AGENT_BUILD_SPEC.md` (which you must also read and
follow). Where the two conflict, this one wins.

You are building a **principal-engineer-level** portfolio project. The bar is higher than for
projects 01–30, in a specific and testable way described below.

---

## 1. The bar: what "principal" means here

Projects 01–30 proved "I can build this system correctly." That is not enough now.

| Senior (already done) | Principal (your job) |
|---|---|
| Builds the system | Builds the **mechanism that makes changing the system safe** |
| Writes tests that pass | Builds a **harness that finds bugs nobody thought to test for** |
| Uses a library | **Implements the algorithm** the library hides |
| Reports a metric | Reports it with a **confidence interval** and an **ablation** |
| Handles the failure | **Proves** it cannot happen, or bounds it |

Your project must contain at least one piece of genuinely hard engineering — a real algorithm,
a real analysis, a real proof or an exhaustive simulation. A CRUD API with a clever README is a
failure of this brief, no matter how polished.

**The single most important instruction:** the headline capability must be *demonstrated by
running code that produces real measured output*, not asserted in Markdown.

---

## 2. Non-negotiable rules (same as the first 30, restated because they matter)

1. **Never fabricate a number.** Every metric in every document must come from a real run you
   actually executed. If you did not measure it, do not write it.
2. **Never claim something is verified when it is not.** Docker is NOT installed. Anything
   container-related is authored-but-`UNVERIFIED`, and must be labelled so.
3. **Report honest results, including bad ones.** A measured 61% that you explain is worth far
   more than a fabricated 97%. If your approach loses to the baseline, publish that and explain
   why — that is a senior signal, not a failure.
4. **No secrets, ever.** No real credentials, tokens or connection strings in any file.
5. **It must build and pass tests from a clean checkout**, with only the toolchain installed.
6. If something cannot be done in this environment, **write down why** in
   `docs/known-limitations.md` and implement the closest honest thing.

---

## 3. Environment

| Toolchain | Availability |
|---|---|
| .NET SDK | **10.0.400** — target `net10.0` |
| Node.js | **24.18.0** (npm 11.16.0) |
| Python | **3.12.10** at `%LOCALAPPDATA%\Programs\Python\Python312\python.exe` |
| Go, Rust, JDK 21 + Maven, clang | installed via winget — **verify with `--version` before use** |
| Docker, databases, brokers, cloud | **NOT AVAILABLE.** Do not require them to run tests. |

Network access to package registries may be restricted or slow.

**Prefer the standard library.** A principal-level project that depends on ten packages to do
its core work is weaker than one that implements the core itself. Implement the hard part
yourself — that is the entire point of these projects. Use dependencies for peripheral concerns
(HTTP, serialisation, test frameworks), not for the headline capability.

If a package cannot be installed, do not stall: implement it yourself or adjust scope, and
record the decision in an ADR.

### Language-specific requirements

| Language | Build | Test | Notes |
|---|---|---|---|
| C# | `dotnet build -c Release` | `dotnet test -c Release` | xUnit. 0 warnings required. |
| Go | `go build ./...` | `go test ./...` | `go vet ./...` must be clean. Use `-race` for concurrent code. |
| Rust | `cargo build --release` | `cargo test` | `cargo clippy -- -D warnings` must be clean. |
| Java | `mvn -q package` | `mvn -q test` | JUnit 5. Maven wrapper if possible. |
| Python | n/a | `python -m pytest` | Type hints required. Use `venv`. Pin deps in `requirements.txt`. |
| C++ | `clang++ -std=c++20 -O2` | own test binary or CTest | Build via a script; `-Wall -Wextra` clean. |
| TypeScript | `npm run build` | `npm test` | `strict: true`. No `any` in public APIs. |

Provide a **`build.ps1`** and **`test.ps1`** at the project root that work from a clean checkout,
so a reviewer never has to guess the incantation. They must exit non-zero on failure.

---

## 4. Required deliverables

```text
<project>/
  README.md                  # see §5
  build.ps1  test.ps1        # must work from clean checkout
  src/ ...                   # implementation
  tests/ ...                 # real automated tests
  docs/
    decisions/               # >= 4 ADRs (ADR-001..., see AGENT_BUILD_SPEC.md format)
    security/security-review.md
    test-results.md          # REAL pasted output of your build + test run
    known-limitations.md     # honest, specific
    <headline-evidence>.md   # THE measured result (benchmark/ablation/eval/proof)
    portfolio/
      portfolio-summary.md
      demo-script.md
      screenshots-needed.md
      upwork-description.md
      interview-talking-points.md
  demo.ps1                   # must actually run end-to-end; RUN IT before claiming it works
  .gitignore
```

`docs/<headline-evidence>.md` is the most important document in the project. It is the file that
proves the hard part works. Name it for what it measures, e.g. `docs/ablation-results.md`,
`docs/simulation-findings.md`, `docs/equivalence-proof.md`, `docs/benchmark-results.md`.

---

## 5. README standard

Follow the 21-section structure in `AGENT_BUILD_SPEC.md`, plus these additions:

- **§ "The problem this exists to solve"** — open with the *story*: the concrete, expensive,
  real-world situation. Written for an engineering manager, not a compiler.
- **§ "Why this is hard"** — state the non-obvious difficulty explicitly. If it isn't hard, you
  have chosen the wrong scope.
- **§ "What was measured"** — the real numbers, with method and caveats.
- **§ "What this does not do"** — explicit non-claims.

Tone: precise, calm, senior. No marketing language. No emoji. No "blazing fast", no "robust",
no "seamless". Never claim production use, clients, or employers. Everything is a self-directed
engineering case study with fictional demo data.

---

## 6. Testing standard

- Tests must be **real and meaningful**: they must fail if the logic is wrong.
- **Run the whole suite at least twice** before you report. A suite that passes once is not
  proven. If results differ between runs, you have a flakiness bug — fix it, do not retry until
  green. (This exact check caught a real `SQLITE_BUSY` concurrency bug in project 08.)
- Test the **hard part**, not just the plumbing. If your project is about correctness under
  concurrency, you need a test that actually exercises concurrency.
- Property-based, fuzz, or exhaustive-simulation tests are strongly encouraged where relevant.
- Determinism: seed all randomness and record the seed so any failure is reproducible.

---

## 7. Git

`git init` in your project folder only. Commit everything (no `bin/`, `obj/`, `target/`,
`node_modules/`, `__pycache__/`, `.venv/`, no databases, no secrets). Use this trailer:

```text
Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
```

Set identity locally: `git -c user.name='pauxru' -c user.email='pauxru@users.noreply.github.com'`.

Confirm `git status --porcelain` is **empty** after a test run. If running tests dirties the
tree, write volatile output to a gitignored `artifacts/` directory.

---

## 8. Definition of done

You are done when **all** of these are true:

- [ ] The build command completes with **zero warnings**.
- [ ] The full test suite passes, **run twice**, with identical results.
- [ ] `demo.ps1` was actually executed end-to-end and its real output is in the docs.
- [ ] `docs/test-results.md` contains genuine pasted output, not a summary you wrote.
- [ ] The headline evidence document contains **real measured numbers** from a real run.
- [ ] ≥4 ADRs, security review, known limitations, and the 5 portfolio files exist.
- [ ] Everything is committed and `git status --porcelain` is empty.

## 9. Reporting back

Report: the exact build and test commands with their real output summary, the headline measured
result, anything you could not do and why, and any place where your honest result is worse than
you would like. **Do not report success you have not verified by running.** The coordinator
independently re-runs every build and test and will find discrepancies.
