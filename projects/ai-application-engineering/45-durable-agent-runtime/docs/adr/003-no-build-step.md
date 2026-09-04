# ADR 003 — No build step, no dependencies

**Status:** accepted

## Context

A TypeScript project normally arrives with `package.json`, a lockfile, `tsc`, a test
runner, and a few hundred megabytes of `node_modules`. For a portfolio project whose whole
argument is "here is a measurement you can reproduce", that is a lot of surface between the
reader and the numbers.

Node 22 runs `.ts` files directly by **stripping types**, and `node --test` runs
`.test.ts` files natively. So the whole apparatus is optional.

## Decision

Zero dependencies. No `package.json`, no lockfile, no compiler, no test framework. Node
executes the source.

Stage 5 of `test.ps1` enforces it: it fails if `node_modules` or `package-lock.json`
appear, and it scans every import in `src/`, `tests/` and `tools/` for a specifier that is
neither relative nor `node:`-prefixed. A claim on the tin that is not tested stops being
true within a month.

## Consequences

**What it costs.** Strip-only mode cannot *emit* code, so any TypeScript construct that
compiles to a runtime artefact is rejected outright with
`ERR_UNSUPPORTED_TYPESCRIPT_SYNTAX`:

- **Constructor parameter properties.** `constructor(readonly x: number)` desugars to an
  assignment, which is emission. Every class here declares its fields explicitly and
  assigns them in the constructor body. This cost one build cycle to discover and is the
  reason `CrashError` looks more verbose than it needs to.
- **Enums.** They compile to an object. Replaced with unions of string literals —
  `type UnknownPolicy = 'retry-same-key' | 'retry-new-key' | 'escalate'` — which is a
  better type anyway, since it is structurally comparable and serialises to itself in the
  journal.
- **Namespaces, decorators, `--experimental-*` syntax.** Not used.
- **No type checking.** Stripping is not compiling. Nothing here verifies that the types
  are *sound*; they are documentation that the parser validates the syntax of. `build.ps1`
  loads every module, which catches syntax errors and import cycles but not type errors.
  A reader who wants type checking can point `tsc --noEmit` at the tree; the project does
  not depend on it having been run.

**What it buys.** `git clone` and `pwsh test.ps1` — no install step, nothing to go stale,
no lockfile drift, and no supply chain. The measurement is the only thing between the
reader and the claim. For a project about what survives a crash, "you can run it in six
seconds on a stock Node" is worth more than a type checker.

## What would change this

A team codebase. The absence of type checking is acceptable for 1,200 lines of
self-contained code with 143 tests over it, and unacceptable for anything a second person
maintains. The migration is `npm i -D typescript` and a `tsconfig.json` — nothing in the
source has to change, because everything here is already valid TypeScript.
