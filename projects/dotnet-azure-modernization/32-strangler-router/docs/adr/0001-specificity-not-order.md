# ADR 0001 — Rules resolve by specificity, not by declaration order

**Status:** accepted

## Context

A ruleset is a list of pattern/operation pairs. When two patterns both match a
node — `$.meta` and `$.meta.tier`, say — something has to decide which wins.

The two obvious answers are *first match wins* and *last match wins*. Both are
common. Both are wrong for this problem.

Ignore rules are added incrementally, by different people, over months, usually
by appending to the bottom of a config file during an incident. Under either
ordering rule, the meaning of the file depends on the order of its lines, which
means:

- Appending a narrow rule to the bottom either always beats or never beats the
  broad rules above it, depending on which convention you picked.
- Sorting the file alphabetically — which someone will do — silently changes
  what the system compares.
- Reviewing a one-line addition requires reading every line above it.

That last point is the killer. The whole value of this project is that ignore
rules are load-bearing safety configuration. Configuration whose meaning is not
locally reviewable is configuration that will drift.

## Decision

Rules resolve by **specificity**. Each literal segment in a pattern — a named
field or a numeric index — scores 2. Wildcards (`*`, `[*]`, `..`) score 0. The
matching rule with the highest score wins; ties are broken by later declaration.

```
$..*                      spec 0
$.order.lines[*].total    spec 6   ← wins for that node
$.order                   spec 2
```

Additionally, an ignore rule on an *ancestor* does not outrank a more specific
rule on a descendant. `$.meta` ignored plus `$.meta.tier` compared is a valid,
useful configuration, and it does what it says.

## Consequences

- Adding a narrow rule to the bottom of a file is safe and reviewable in
  isolation. It beats the broad rules by construction, not by position.
- Sorting or reformatting the file cannot change behaviour. There is a test for
  exactly this: the same two rules in either order produce identical output.
- Implementing "ignore this subtree except one field" required the walk to keep
  descending under an ignore rather than short-circuiting, which costs a
  traversal of ignored subtrees. That cost is capped by a precomputed `maxSpec`:
  if no rule in the set is more specific than the ignore, the walk stops
  immediately, which is the common case.
- Specificity as "count the literal segments" is CSS's rule, and it is
  approximate. `$.a.*.c` (spec 4) beats `$.a.b` (spec 4) only by declaration
  order. In practice patterns in a ruleset are near-disjoint and this has not
  come up; a lexicographic tuple comparison would be the fix if it did.
