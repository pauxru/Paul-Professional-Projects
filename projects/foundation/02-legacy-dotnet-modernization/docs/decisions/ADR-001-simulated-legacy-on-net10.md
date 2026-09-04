# ADR-001: Simulate legacy style on .NET 10

## Context
The build host cannot install .NET Framework 4.x, but the project must demonstrate concrete modernization risks from a classic enterprise application and still build/test on this host.

## Options
1. Omit a runnable legacy application and describe it only.
2. Use a non-runnable historical .NET Framework project.
3. Build a runnable `net10.0` MVC application that deliberately contains documented legacy patterns.

## Decision
Choose option 3. The `legacy/` application targets `net10.0` but visibly implements raw ADO.NET, static XML configuration, fat controller logic, session workflow, blocking calls, direct files, static cache, broad catches, and a marked SQL injection smell.

## Consequences
Reviewers can run and characterize the as-is behavior locally. The project remains honest that runtime target is modern while the architecture is intentionally legacy-style.

## Risks
Some Framework-only integration concerns (WebForms, `System.Web`, Windows authentication, COM) cannot be executed by the simulation and remain discovery items in the compatibility matrix.

## Alternatives
Option 1 loses demonstrable behavior; option 2 violates the repeatable-build constraint.
