# ADR-002 — Own a small safe expression evaluator

## Context

Field transformations require composition, but accepting C#, JavaScript, PowerShell, reflection, or arbitrary method names would turn configuration into remote code execution.

## Options

1. Embed a scripting runtime.
2. Use unrestricted dynamic expressions/reflection.
3. Implement a parser for paths, literals, and a function allow-list.

## Decision

Implement option 3. The evaluator recognizes JSON paths and explicitly enumerated pure functions. It limits expression length, recursion depth, result size, mapping count, and never resolves CLR types or members.

## Consequences

The security boundary is reviewable and tested. The DSL supports the common integration transformations while remaining deterministic. Adding a function requires code review and a test.

## Risks

A custom parser can contain correctness bugs, and users may request unsupported expressions. Fuzzing and more grammar tests would strengthen it.

## Alternatives

Sandboxed JavaScript offers more power but secure isolation is difficult in-process. JSONata/JMESPath packages could be considered after supply-chain, licensing, and escape-surface review.
