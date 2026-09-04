# Demo Script

1. Run `scripts\demo.ps1` from a Development API instance on port 5026.
2. Observe the initial gate and SLO status for `checkout`.
3. Generate a baseline and inject a partial outage through the real simulator endpoint.
4. Evaluate alerts and inspect the fast page state/burn rates.
5. Declare, mitigate, and resolve an incident; inspect attached error-budget impact.
6. Create a postmortem, add an action, progress the review state, and query themes/overdue work.
7. Re-query the deploy gate and dashboard.

Narrate the mathematics: the page needs both long and short burn predicates, and the incident window is an attribution scope rather than proof of causality.
