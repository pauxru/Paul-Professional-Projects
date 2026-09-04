# Authentication coexistence: measured results

Deterministic findings only. Regenerated and byte-compared by `test.ps1` stage 5.

## 1. The claims transformation cannot be finished

Decision space: 4096 decisions (64 role sets x 8 request contexts x 8 resources). Every one is evaluated; nothing is sampled.

| transformation | divergent decisions | escalations | lockouts |
| --- | ---: | ---: | ---: |
| naive role-to-scope table | 592 | 592 | 0 |
| + deny channel | 16 | 16 | 0 |
| + one grant row corrected | 0 | 0 | 0 |

Legacy authorization is **not monotone** in the role set: 72 distinct (role set, added role, resource) triples exist where granting an additional role *removes* a permission. Examples:

| roles | plus one more role | loses |
| --- | --- | --- |
| `Admin` | `Admin+Auditor` | `ledger.write` |
| `Admin` | `Admin+Vendor` | `users.manage` |
| `Admin` | `Admin+Temp` | `users.manage` |
| `Manager` | `Manager+Temp` | `payment.approve` |
| `Admin+Manager` | `Admin+Auditor+Manager` | `ledger.write` |
| `Admin+Manager` | `Admin+Manager+Vendor` | `users.manage` |

A role-to-scope table unions scopes across roles, so it is monotone by construction. A monotone function cannot agree everywhere with a non-monotone one. The naive transformation is therefore not a transformation with bugs in it -- it is one that provably cannot be completed, no matter how many rows are added to the table.

**Every single divergence grants access the legacy system denied. Not one denies access the legacy system granted.**

That direction is the finding. A migration whose failures are all lockouts generates support tickets on day one and gets fixed. A migration whose failures are all escalations passes user acceptance testing, because no user has ever reported a door that should have been locked.

| kind | roles | resource | local network | tenant matches | temp staff |
| --- | --- | --- | --- | --- | --- |
| Escalation | `Admin+Auditor` | `ledger.write` | False | False | False |
| Escalation | `Admin+Auditor` | `ledger.write` | False | False | True |
| Escalation | `Admin+Auditor` | `ledger.write` | False | True | False |
| Escalation | `Admin+Auditor` | `ledger.write` | False | True | True |
| Escalation | `Admin+Auditor` | `ledger.write` | True | False | False |
| Escalation | `Admin+Auditor` | `ledger.write` | True | False | True |

Adding a negative channel -- roles that *remove* a scope, with deny winning -- brings divergence from 592 to **16**. The fix is a change of shape, not a longer table.

The 16 that survive are a different animal, and it is worth being precise about which is which. They are not a limit of the deny channel: they are one wrong row in the grant table -- `Temp` was given `ledger.read`, which the legacy policy never granted it. Correcting that single row takes the count to **0**.

So of the original 592 divergences, 576 were structural -- unreachable by any grant table, however carefully reviewed -- and 16 were an ordinary data error. Both produce identical symptoms. Only one of them is fixed by reviewing the mapping more carefully, which is the remedy invariably proposed for both.

## 2. Rehash-on-login does not converge

20,000 simulated users, 1095 days, seeded PCG32 (seed 20260904). Users are assigned to cohorts by login frequency; a user migrates on their first login after the feature ships.

| population | day to 50% | to 90% | to 95% | to 99% | at 1 year | at 3 years | ceiling |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| typical | 8 | 166 | 657 | never | 93.7% | 95.7% | 96.0% |
| optimistic | 2 | 19 | 37 | 97 | 100.0% | 100.0% | 100.0% |
| seasonal | 46 | never | never | never | 80.4% | 87.3% | 88.0% |

The simulation agrees with the closed form it should agree with: at day 365 the model gives 93.7% and the analytic mixture 1 - E[(1-p)^d] gives 93.6%. The curve is not a fixed seed agreeing with itself.

The ceiling column is the part that matters. Any cohort that never signs in never migrates, so the asymptote sits strictly below 100% and the last few per cent are not slow -- they are unreachable. Rehash-on-login is a mechanism for migrating everybody who comes back, and it must be paired from day one with a dated plan for everybody who does not.

## 3. The rollback window closes before the evidence arrives

Rehashing is one-way. Reverting to the previous scheme after day D means a forced password reset for everyone rehashed by day D -- there is no third option, because the old hash no longer exists. (Keeping it would reintroduce exactly the weakness the migration removed.)

| population | last day under 1% rehashed | under 5% | under 25% | rehashed by day 14 |
| --- | ---: | ---: | ---: | ---: |
| typical | 0 | 0 | 1 | 61.0% |
| optimistic | 0 | 0 | 0 | 86.5% |
| seasonal | 0 | 0 | 6 | 33.9% |

The window is measured in days, and it is shortest exactly where the migration is healthiest -- a population that signs in often migrates fast and becomes un-revertible fast. By the time there is enough production evidence to be confident the change was safe, the option to undo it has already gone. Plan the rollback for the first week or do not claim to have one.

## 4. A migrated account is only as strong as the weakest door left open

`finance.director` holds an Argon2id hash of a long passphrase and signs in through OIDC. Assuming the legacy validation key has leaked -- it lives in `web.config`, it is identical across the farm, and in a system this old it is usually in source control history -- a forged Forms ticket for that account is:

- accepted during coexistence: **True**
- accepted after the legacy path is switched off: **False**

The password migration bought this account nothing while the ticket path stayed open. Account security is the minimum over every enabled authentication path, and the credential path is the only one anybody measures. Every stack you keep running for the sake of the stragglers is running for everybody.

Nor can that path be closed by waiting. Forms authentication renews its ticket on use, so an active session never ages out. 180 days after a cutoff is announced, with a 30-day sliding timeout, 9,454 of 20,000 simulated sessions are still valid and 10,546 have lapsed -- and the survivors are the most active accounts, which is to say the most valuable ones. A sliding window has no natural end; the cutoff has to be a date.

## 5. What the hardened stacks refuse

Distinct rejection reasons observable from outside, over four tampered tickets: legacy protector **3** (BadPadding, BadMac, Malformed), hardened protector **1** (BadMac).

The legacy composition authenticates the plaintext and then encrypts the result, so integrity cannot be checked before decryption and the unpadding step runs on attacker-controlled bytes. Encrypt-then-MAC removes the distinction by making the dangerous code unreachable rather than by handling its error paths consistently -- a property that survives future edits instead of needing to be re-established by each one.

Token and assertion checks: **17 of 17** behave as required.

| check | holds |
| --- | --- |
| alg=none is rejected | True |
| HS256 token is rejected by an RS256 validator | True |
| token for another audience is rejected | True |
| expired token is rejected | True |
| id_token with the wrong nonce is rejected | True |
| a valid token is accepted | True |
| code redemption with the wrong verifier is rejected | True |
| code redemption with the right verifier succeeds | True |
| replaying a redeemed code is rejected | True |
| challenge method 'plain' is refused | True |
| an unregistered redirect_uri is refused | True |
| a valid assertion is accepted | True |
| replaying an assertion is rejected | True |
| an assertion with an appended attribute is rejected | True |
| an assertion with an edited role is rejected | True |
| an assertion minted for another relying party is rejected | True |
| an expired assertion is rejected | True |

All three stacks produce an identical canonical principal for the same user: **True**. That is the functional requirement -- and, per section 4, the reason the security property above is what it is.

## 6. Predictions

14 predictions were written before the experiments were run. **9 were contradicted.**

| # | prediction | verdict | what actually happened |
| ---: | --- | --- | --- |
| 1 | Legacy authorization is monotone in the role set: adding a role never removes a permission. | contradicted | 72 counterexamples to monotonicity |
| 2 | The naive role-to-scope transformation will diverge from the legacy policy on fewer than 200 of the 4096 decisions. | contradicted | 592 of 4096 decisions diverge |
| 3 | Divergences will run in both directions, with lockouts outnumbering escalations. | contradicted | 592 escalations, 0 lockouts |
| 4 | The deny-aware transformation will reduce divergences substantially but not to zero. | held | 16 divergences remain, all from one wrong grant row |
| 5 | Argon2id at the deployable RFC 9106 profile will cost between 30x and 100x a PBKDF2-HMAC-SHA1 verification at 1000 iterations. | timing | see results.md |
| 6 | Without padding work, a single timing sample from a failed login will identify which hash format an account uses with better than 90% accuracy. | timing | see results.md |
| 7 | With constant-work padding enabled, that accuracy falls to roughly chance (25% for a four-way choice). | timing | see results.md |
| 8 | Constant-work padding will roughly double the median verification time across a mixed population. | timing | see results.md |
| 9 | Rehash-on-login will migrate 95% of a typical population within 90 days. | contradicted | 95% reached on day 657 |
| 10 | After three years, fewer than 1% of a typical population will remain unmigrated. | contradicted | 4.3% still unmigrated at 3 years |
| 11 | The rollback window -- the period during which fewer than 5% of users have been rehashed -- will last at least two weeks. | contradicted | under 5% only until day 0; 17.9% already rehashed after one day |
| 12 | Once every user's password has been rehashed to Argon2id, leaving the legacy Forms ticket path enabled costs nothing. | contradicted | a forged legacy ticket still authenticates the fully migrated account |
| 13 | Legacy Forms tickets will all have expired within 60 days of the cutoff announcement, given a 30-day sliding timeout. | contradicted | 9,454 sessions still valid after 180 days |
| 14 | Hardening the ticket protector to encrypt-then-MAC will leave at least one externally distinguishable rejection reason. | contradicted | 1 distinct rejection reason from the hardened protector |

