# ADR-003: Make broken and fixed paths explicit runtime modes

## Context
The lab needs reviewers to run one incident in both pathological and corrected forms without maintaining two repositories or relying on code comments. Broken code must remain contained and never become an accidental API default.

## Options
1. Keep broken snippets only in Markdown.
2. Maintain a branch per incident state.
3. Implement an `IIncidentScenario` catalogue selected by `--scenario` and `--mode`.

## Decision
Use a `ScenarioCatalog` of `IIncidentScenario` implementations. The harness passes `ScenarioRunOptions` with an incident ID, `Broken`/`Fixed` enum, bounded operation count, and hard timeout. The Northstar HTTP API is not switched into an unsafe production mode; toggling is scoped to the harness.

## Consequences
Each incident has executable before/after evidence and a focused test. It also makes the unsafe path visible in source and easy to review for cleanup and cancellation.

The repository-wide no-`.Wait()`/no-`.Result` convention has one intentional, documented exception: the isolated `INC-005` broken branch uses `Task.Delay(...).Wait(...)` solely to reproduce sync-over-async. It is never mapped into the sample API, is cancellation-bounded, and is covered by an incident trait test.

## Risks
Mode branches can drift if only one mode is tested. Each unit test runs both paths and asserts the intended difference.

## Alternatives
Compile-time symbols would make comparison cumbersome and risk a build that excludes evidence paths. Feature flags would add configuration surface without improving the standalone lab.
