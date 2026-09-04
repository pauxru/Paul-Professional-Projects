# Demonstration Script

1. Start the API at `http://localhost:5014` and open `/docs`.
2. Request a Development token with all four loan scopes.
3. List seeded `SME-FLEX` product and synthetic `Jua Kali Manufacturing Ltd (fictional)` customer.
4. Create and submit a KES application. Show the workflow event history and document checklist.
5. Upload/verify documents, run deterministic KYC, then call decision. Inspect every entry in `decisionTrace.rules` and score contributions.
6. Open the underwriting queue, explain claim expiry, approve under delegated authority, then create an offer with schedule/APR logic.
7. Accept the offer and send an idempotent simulated disbursement. Repeat its provider reference to show original result.
8. Query the decision record and audit list using the correlation ID. Finally send a candidate policy to `/rulesets/what-if` and explain flips without changing history.
