# Results

Ninety days of simulated traffic, 120 requests a day, 6 scenarios, 8 detectors. Every request in every scenario returns HTTP 200 and a normal latency. Nothing in this project detects a failure by observing an error, because there are none.

Thresholds are calibrated to a 1% per-day false positive rate and an alert requires 3 consecutive days over threshold. The first 30 days are the reference window and no detector may alert inside it.

## The scenarios

`material day` is the first of three consecutive days on which mean true quality falls below 0.95. Detection delay is measured from there, not from the onset day: on the onset day the ramp has barely started and no honest detector could fire.

| key | scenario | kind | onset | material day | symptom |
|---|---|---|---|---|---|
| `model-swap` | Silent model swap | regression | 45 | 47 | answers become fluent, shorter and free of specifics |
| `retrieval-decay` | Retrieval decay | regression | 45 | 47 | answers are fluent, confident and about the wrong thing |
| `input-shift` | Input population shift | **not a regression** | 45 | -- | nothing is wrong; the questions changed |
| `refusal-creep` | Refusal creep | regression | 45 | 53 | the assistant declines a growing share of answerable questions |
| `template-regression` | Prompt template regression | regression | 45 | 57 | 8% of traffic gets a truncated answer |
| `healthy` | No degradation (control) | **not a regression** | -- | -- | nothing happens for ninety days |

## Detection matrix

`(+Nd)` is days late. `(Nd early)` means the detector alerted before the regression became material, which is the outcome you actually want and which three detector/scenario pairs achieved. `(FP)` on `input-shift` is a false positive: that scenario is a change in who is asking, not in how well they are served.

| detector | `model-swap` | `retrieval-decay` | `input-shift` | `refusal-creep` | `template-regression` | `healthy` |
|---|---|---|---|---|---|---|
| APM (error rate + p95 latency) | never | never | never | never | never | never |
| PSI on output embeddings | day 47 (+0d) | day 61 (+14d) | day 54 **(FP)** | day 82 (+29d) | never | never |
| PSI on input embeddings | never | never | day 53 **(FP)** | never | never | never |
| MMD on output embeddings | day 48 (+1d) | never | day 56 **(FP)** | day 71 (+18d) | never | never |
| Refusal-rate CUSUM | never | never | never | day 49 (4d early) | never | never |
| Answer self-similarity | day 46 (1d early) | never | never | day 52 (1d early) | day 57 (+0d) | never |
| Sliced PSI (worst topic) | day 59 (+12d) | day 62 (+15d) | never | never | day 57 (+0d) | never |
| Quality canary (golden set) | day 48 (+1d) | day 49 (+2d) | never | day 56 (+3d) | never | never |

## What each detector costs

Across ninety healthy days the entire panel raised **0** alerting days. Three detectors nonetheless alerted on `input-shift`, which is the more expensive kind of wrong: a page, an investigation, and nothing to find.

| detector | model calls/day | false-alarm days (healthy) | alerts on `input-shift` | regressions caught |
|---|---|---|---|---|
| APM (error rate + p95 latency) | 0 | 0 | no | 0 / 4 |
| PSI on output embeddings | 0 | 0 | **yes** | 3 / 4 |
| PSI on input embeddings | 0 | 0 | **yes** | 0 / 4 |
| MMD on output embeddings | 0 | 0 | **yes** | 2 / 4 |
| Refusal-rate CUSUM | 0 | 0 | no | 1 / 4 |
| Answer self-similarity | 0 | 0 | no | 3 / 4 |
| Sliced PSI (worst topic) | 0 | 0 | no | 3 / 4 |
| Quality canary (golden set) | 24 | 0 | no | 3 / 4 |

## The cheapest set that leaves nothing uncovered

The question a monitoring budget actually asks is not which detector is best. It is which *set* covers every failure mode, and what that set costs.

* Answer self-similarity
* Sliced PSI (worst topic)

Total cost: **0 model calls per day**, **0** false positives on the non-regression control, **0** false-alarm days on ninety healthy days.

Greedy set cover is a `ln n` approximation, so the same problem is also solved by exhaustive search over all 255 non-empty subsets. The two agree.

Days of exposure under that set -- how long each regression runs before the first alert:

| regression | days of exposure |
|---|---|
| `model-swap` | 0 (caught 1 days early) |
| `retrieval-decay` | 15 |
| `refusal-creep` | 0 (caught 1 days early) |
| `template-regression` | 0 |

## Predictions: 9 of 16 contradicted

Written down before the panel was run. The count in this heading is computed from the table below rather than typed, so the two cannot drift apart.

| # | prediction | verdict | what the panel measured |
|---|---|---|---|
| P01 | APM -- error rate and p95 latency -- will not catch a single one of the four regressions. | held | APM caught 0 of 4 regressions |
| P02 | Input-side PSI will fire on the population shift and on no genuine regression, because it is watching the wrong side of the system. | held | input PSI fires on the population shift and on nothing else |
| P03 | No detector will alert on `input-shift`, since a change in who is asking is not a change in how well they are served. | **contradicted** | 3 of 8 alerted on the non-regression: PSI on output embeddings, PSI on input embeddings, MMD on output embeddings |
| P04 | Aggregate output PSI will catch the template regression, because a truncated answer is a large change in a small share of traffic. | **contradicted** | aggregate output PSI on the 8%-of-traffic regression: never alerted |
| P05 | Slicing PSI by topic will catch the localised regression that the aggregate misses, and will not alert on the mix shift. | held | slicing by topic caught the localised regression without alerting on the mix shift |
| P06 | Slicing will be *slower* than aggregating on a diffuse regression, because each slice is a twelfth of the sample. | held | on the diffuse regression sliced PSI took 12 days vs aggregate 0 |
| P07 | The golden-set quality canary will be in the cheapest covering set. It is the only detector that looks at quality directly. | **contradicted** | cheapest covering set = Answer self-similarity, Sliced PSI (worst topic) |
| P08 | The cheapest covering set will cost model calls per day, because free detectors will not be enough. | **contradicted** | the covering set costs 0 model calls per day |
| P09 | Answer self-similarity will catch the model swap. | held | self-similarity on the boilerplate collapse: alerted |
| P10 | MMD will beat marginal PSI, because it tests the joint distribution and PSI only tests the margins. | **contradicted** | MMD caught 2 regressions, marginal PSI caught 3 |
| P11 | With every threshold calibrated to a 1% per-day false positive rate, the panel will produce zero alerting days on ninety healthy days. | held | 0 alerting days across the whole panel on ninety healthy days |
| P12 | No detector will alert before the regression becomes material. Detection is a lagging measurement by construction. | **contradicted** | 3 detector/scenario pairs alerted before quality became material |
| P13 | At least one regression will go completely undetected by the whole panel. | **contradicted** | regressions no detector caught: none |
| P14 | Greedy set cover will disagree with exhaustive search on a problem this small. | **contradicted** | greedy set cover agreed with exhaustive search |
| P15 | Retrieval decay -- fluent, confident, wrong -- will be the slowest regression to detect, because nothing about the text looks broken. | held | best delay on retrieval decay was 2 days; best on every other regression was [-4, -1, 0] |
| P16 | Answer self-similarity will *rise* when a share of traffic collapses into boilerplate, because boilerplate answers resemble each other. | **contradicted** | mean pairwise similarity moved from 0.3257 in the healthy window to 0.3208 once 60% of traffic was boilerplate (-0.0049) |

## The five findings worth carrying out of here

**1. Conditioning on the cohort buys sensitivity and specificity at the same time.** Sliced PSI caught the regression that touched 8% of traffic, which every aggregate detector missed entirely, and it was the only embedding detector that did *not* fire on the population shift. Slicing by topic removes exactly the variable `input-shift` moves. The usual sensitivity/specificity trade is a consequence of asking a badly posed question, not a law.

**2. And it is not free.** On the diffuse regression, sliced PSI alerted 12 days later than the aggregate, because each slice is a twelfth of the sample. A panel wants both, which is why the covering set has two members and not one.

**3. The expensive detector did not make the cut.** The golden-set canary costs 24 model calls a day, was among the fastest on three regressions, and is still absent from the cheapest covering set -- because two free detectors between them cover everything it covers and one thing it does not. Its blind spot is not statistical, it is a decision: the golden set was written at launch and covers 4 of 6 topics, and the regression it misses lives in one of the others.

**4. A CUSUM calibrated on its own reference window is guaranteed to false-alarm, and fixing that is a two-step problem.** It is a reflected random walk, so it crosses any fixed threshold given enough days -- the first version alerted on all six scenarios including the healthy control. Simulating the monitoring horizon fixed most of it. What remained was subtler: the live detector estimates its target from thirty days and applies it to sixty fresh ones, and the error in that estimate is itself a drift the CUSUM integrates. Resampling the reference window *and* the horizon separately took the panel to zero false alarms.

**5. The detector I nearly deleted is in the covering set.** Answer self-similarity was written on the theory that boilerplate answers resemble each other. Measured, the statistic moved the other way: a day containing two tight clusters is *less* self-similar than a day containing one, so partial contamination lowers it. As a one-sided detector it found nothing anywhere. Scored two-sided -- which is all the reference window ever licensed -- it catches three of the four regressions for zero model calls. The hypothesis was wrong and the statistic was fine.


---

Generated in 32.2s. This file is `results-stable.md` plus this line; the stable file is hashed by `test.ps1` to prove the run is reproducible.
