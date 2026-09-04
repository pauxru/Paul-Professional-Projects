# Bucketing Verification

## Method
This result was measured on the build host (Windows, .NET 10.0.400) against the committed `tests/Fixtures/bucketing-keys.txt` fixture containing 10,000 deterministic keys. The measurement invokes the same algorithm as `FeatureFlags.Domain.Bucketing`: SHA-256 over UTF-8 `flagKey|salt|contextKey`, first eight digest bytes read big-endian, modulo 100,000.

Command-equivalent measurement date: 2026-09-03. The distribution used `flagKey=checkout`, `salt=uniform-salt`; each bin spans 10,000 of the 100,000 bucket values.

## Real measured distribution

| Bucket range | Keys |
|---|---:|
| 0–9,999 | 1,014 |
| 10,000–19,999 | 1,001 |
| 20,000–29,999 | 1,007 |
| 30,000–39,999 | 1,000 |
| 40,000–49,999 | 1,004 |
| 50,000–59,999 | 984 |
| 60,000–69,999 | 983 |
| 70,000–79,999 | 1,026 |
| 80,000–89,999 | 972 |
| 90,000–99,999 | 1,009 |

Expected count per bin is 1,000. The measured chi-square statistic is **2.348** (9 degrees of freedom), well inside the test tolerance of `< 30` used as a lightweight uniformity sanity check. This is not a cryptographic randomness claim; it verifies that this fixed corpus does not show a material allocation skew.

## Sticky expansion verification
For `flagKey=rollout`, `salt=sticky-salt`, the same fixture produced:

| Allocation | Contexts selected |
|---|---:|
| 10% (`bucket < 10,000`) | 1,020 |
| 20% (`bucket < 20,000`) | 2,021 |
| retained 10% contexts after expansion | 1,020 / 1,020 |
| stickiness violations | 0 |

The contiguous allocation invariant makes this expected: no key under 10,000 can leave the first allocation when its upper bound moves to 20,000.

## Server/SDK parity
`ServerAndSdkEvaluation_ParityOverTenThousandKeySharedFixture` evaluates all 10,000 fixture keys using `FlagEvaluator` as the server-side oracle and `FeatureFlagClient.BoolVariationDetail` as the SDK path. It asserts value, variation index, and reason equality for each key. The passing suite output is recorded in `test-results.md`.
