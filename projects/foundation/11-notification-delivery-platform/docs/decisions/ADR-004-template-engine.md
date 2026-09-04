# ADR-004 — Template engine: own vs library, and the escaping decision

## Context

Notifications carry a payload rendered into a template. Templates must
support at minimum: token substitution with dotted paths
(`{{user.firstName}}`), conditionals, and loops. Rendering must be safe by
default for HTML output (email bodies) and must reject unknown tokens in
strict mode. Templates ship with tests.

## Options

1. **Handlebars.NET / Fluid / Scriban / Liquid.NET.**
2. **Roll our own limited engine.**

## Decision

Adopt **option 2**: a small engine in
`NotificationPlatform.Infrastructure.Templates.TemplateEngine`:

- Token substitution with dotted paths.
- `{{#if user.premium}}...{{/if}}` conditionals.
- `{{#each order.items}}...{{/each}}` loops.
- **HTML-escape by default.** A `{{raw:x}}` marker exists but requires an
  explicit opt-in flag on the template.
- Strict mode rejects unknown tokens; default mode substitutes empty.

## Consequences

- Zero third-party render surface. The escape semantics are exactly what
  the security model needs and nothing more.
- The engine is small enough to test comprehensively —
  `TemplateEngineTests` covers XSS, missing data, conditionals, loops, and
  strict-mode rejection.
- If we ever need a full Liquid dialect we would swap the engine behind
  `ITemplateEngine` without changing anything else.

## Risks

- Feature growth pressure: someone will ask for filters
  (`{{ x | upper }}`). We will add them as needed, keeping the escape
  invariant.
- Custom engines invite subtle escape bugs. Mitigated by tests and by the
  security review.

## Alternatives revisited

At the point templates become a first-class product surface (a template
editor, marketplace, etc.), moving to a real templating library is
justified.
