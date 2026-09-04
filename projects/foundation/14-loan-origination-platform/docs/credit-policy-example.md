# Seeded Credit Policy Example

The Development seed publishes immutable ruleset `SME-CREDIT-POLICY` version `1`. It is fictional policy wording for a technical demonstration, not lending advice or an approved credit policy.

| Rule ID | Business rule | Outcome | Explanation retained in trace |
|---|---|---|---|
| `arrears-hard-stop` | If the declared/history-derived past-arrears count is three or more, do not proceed. | `Fail` | The exact arrears count is listed as an input read. |
| `stress-dsr-refer` | If existing monthly debt service exceeds 45% of net income, send the case to a human. | `Refer` | The computed DSR is retained. |
| `large-unsecured-document` | If requested principal is above KES 500,000 and collateral is below KES 500,000, obtain collateral evidence before underwriting. | `RequireDocument` | Both requested principal and collateral values are retained. |
| `new-business-limit` | SMEs trading for fewer than twelve months have a KES 250,000 policy ceiling. | `AdjustLimit` | SME flag and age-of-business facts are retained. |
| `bureau-rate-adjustment` | Synthetic bureau grades C/D add 2.5 percentage points to the recommended rate. | `AdjustRate` | Grade is retained; this is not a real bureau result. |
| `baseline-pass` | Positive net income is a baseline control. | `Pass` | Income fact is retained. |

## Decision precedence
Any matching `Fail` produces a failed rules decision, even if another rule refers the case. `RequireDocument` produces a referral and blocks progression until the extra document is verified. Multiple limit rules use the lowest maximum; rate adjustments are summed deterministically. A rules decision is one input to underwriting, not a hidden final credit score.

## What-if rollout
Credit operations can send a candidate `RuleSetDefinition` to `POST /api/v1/rulesets/what-if` (or `/simulate`). The endpoint re-evaluates stored historical applicant facts without mutating their original decisions and reports total evaluated applications, flips, old/new decisions, and a summary for every application. Publishing a candidate uses `POST /api/v1/rulesets`; an existing ID/version cannot be overwritten.

## Fact model
The allowed condition fields are: age, net income, expenses, existing monthly debt service, dependants, business age, income stability, past arrears, collateral value, requested principal, term, bureau grade, KYC status, SME flag, currency, DSR, DTI, and disposable income. Conditions are typed numeric, boolean, or string comparisons plus `all`, `any`, and `not`; arbitrary executable expressions are intentionally not allowed.
