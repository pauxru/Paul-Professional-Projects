# Strangler Router

A shadow-traffic proxy and semantic response differ for incremental migrations,
built around one argument:

> **Most shadow-traffic programmes fail at the diff, not at the proxy.**
> Comparing two JSON responses byte-for-byte produces so much noise that the
> report becomes unreadable, and the ignore rules people write to fix that are
> the thing that lets real defects through.

Go 1.23, standard library only. `go test ./...` — 67 tests.

---

## The problem, in one screenshot

Two implementations of the same endpoint, both behaving **perfectly correctly**.
A byte comparison, or any structural JSON diff without configuration, reports:

```
$.meta.durationMs           18 != 12
$.meta.generatedAt          "2024-06-12T09:31:45Z" != "2024-06-12T09:31:46Z"
$.meta.requestId            "d708d49e-…" != "2b2ce93e-…"
$.order.lines[0].sku        "SKU-59499" != "SKU-33365"
$.order.lines[0].price      69.25 != 13.07
$.order.lines[2].sku        "SKU-33365" != "SKU-59499"
…
$.order.subtotal            484.31 != 484.310000000001
```

Twelve differences. Zero bugs. A duration, a clock tick, a request ID, a
different `ORDER BY` on the line items, and one float that took a different
addition order.

Run this across a real endpoint and every single response "fails". Nobody reads
the report after week one. That is where these programmes die.

## The fix everyone reaches for, and why it is worse

The obvious response is to add ignore rules until the report goes quiet. It
works immediately, and each rule is individually reasonable:

| ruleset | how a team gets there |
|---|---|
| `exact` | no rules; everything differs |
| `precise` | name each volatile field and constrain it to its *shape* |
| `value-blind` | same fields, but ignore the values instead of shape-checking |
| `subtree-ignored` | "the whole `meta` block is noise, just drop it" |
| `tolerant` | "a penny is just floating point" — blanket 0.01 numeric tolerance |
| `resigned` | plus the field that "kept flapping" and the one that "is just casing" |

Nobody writes `resigned` on purpose. They arrive there one defensible commit at
a time, over about six weeks.

So this project treats a ruleset as **a binary classifier** and scores it
against a corpus with known ground truth — 4,000 response pairs, 812 of which
carry one of ten realistic migration defects, all of them also carrying the
noise above.

```
ruleset               TP      FP      FN      TN  precision    recall       F1 noise flagged
------------------------------------------------------------------------------------------------
exact                812    3188       0       0     0.203     1.000    0.337       100.0%
precise              812       0       0    3188     1.000     1.000    1.000         0.0%
value-blind          650       0     162    3188     1.000     0.800    0.889         0.0%
subtree-ignored      569       0     243    3188     1.000     0.701    0.824         0.0%
tolerant             500       0     312    3188     1.000     0.616    0.762         0.0%
resigned             337       0     475    3188     1.000     0.415    0.587         0.0%
```

**Read the `noise flagged` column.** Every ruleset from `value-blind` down flags
exactly as few clean responses as `precise` does: none. The relaxations are not
buying quiet. They are pure loss.

`precise` — the version that names each field and says what shape it should have
— catches **812 of 812 defects with zero false positives**. Walking down the
list to `resigned` gives up **58 percentage points of detection in exchange for
nothing whatsoever**.

That is the finding. Not "write good rules". *The good rules are not more
expensive.* They are ten minutes of extra typing, once.

## Which defects each ruleset lets through

Aggregate recall hides the shape of the loss. This is the table that changes
minds:

```
defect                                exact        precise    value-blind subtree-ignored       tolerant       resigned
----------------------------------------------------------------------------------------------------------------------
total_off_by_a_penny                 caught         caught         caught         caught   MISSED 29/40   MISSED 29/40
status_case_changed                  caught         caught         caught         caught         caught   MISSED 40/40
discount_field_lost                  caught         caught         caught         caught         caught   MISSED 40/40
customer_tier_changed                caught         caught         caught   MISSED 40/40   MISSED 40/40   MISSED 40/40
currency_changed                     caught         caught         caught         caught         caught         caught
line_dropped                         caught         caught         caught         caught         caught         caught
customer_id_became_string            caught         caught         caught         caught         caught         caught
timestamp_emptied                    caught         caught   MISSED 40/40   MISSED 40/40   MISSED 40/40   MISSED 40/40
request_id_not_a_uuid                caught         caught   MISSED 40/40   MISSED 40/40   MISSED 40/40   MISSED 40/40
placed_at_became_epoch               caught         caught         caught         caught         caught         caught
```

A weak ruleset is not uniformly bad — it is **unevenly** bad, and that is what
makes it hard to notice. `subtree-ignored` still catches a dropped order line
every single time. It has just stopped noticing that every customer became
`standard` tier.

Two rows deserve attention:

- **`timestamp_emptied`** — the modern service returns `""` for `generatedAt`.
  `IgnoreValue` passes it, because it *has* a value. `Format: rfc3339` fails it.
  Same field, same amount of noise absorbed, one config keyword apart.
- **`total_off_by_a_penny`** — an absolute `0.01` tolerance wide enough to
  swallow float noise on a 581.17 total is, necessarily, wide enough to swallow
  a penny. The correct tool is a *relative* tolerance (`1e-9`), which absorbs
  `484.310000000001` and still fails `581.16`.

## The second half: when is it safe to promote?

Comparison tells you whether responses match. It does not tell you whether you
have seen enough of them.

The default gate everywhere is a match rate with a threshold — "promote at
99.5%". Consider two endpoints that have *never* diverged:

```
requests    matched observed     wilson promote at 0.995?
--------------------------------------------------------
11               11   100.0%     0.6237          false
40               40   100.0%     0.8577          false
200             200   100.0%     0.9679          false
800             800   100.0%     0.9918          false
2000           2000   100.0%     0.9967           true
4000           4000   100.0%     0.9983           true
```

Both are at 100%. A raw threshold promotes both. The **Wilson score lower
bound** says 11 clean requests is consistent with a true match rate as low as
62%, and refuses.

This matters because of *which* endpoints have low traffic. It is never the
product listing page. It is the refund path, the B2B invoice run, and the one
admin screen — the endpoints where being wrong is most expensive.

Two further properties fall out of taking the statistics seriously:

**Evidence does not carry across stages.** A spotless shadow record says the
modern read path produces the same bytes. It says nothing about how the service
behaves once it is actually on the hot path, where connection pools, cache
behaviour and timeouts are different. So each stage must earn its own samples.

**Rollback uses a recent window, not the lifetime average.** With 2,500 clean
samples behind it, an endpoint that starts failing *every single request* takes
roughly twelve thousand more failures to drag its average below 99.5%. Rollback
fires on 3 divergences in the last 50, and is evaluated *before* promotion.

A full run, three endpoints, driven through the real proxy:

```
route                  stage     requests   matched   observed    in stage stage wilson
--------------------------------------------------------------------------------------
GET /invoices          halted        4000      2500     62.50%        1497       0.0000
GET /orders           cutover        4000      4000    100.00%        1358       0.9951
GET /refunds           shadow          60        60    100.00%          60       0.9004

GET /orders      shadow   -> canary   after  1321 requests: Wilson lower bound 0.9950 over 1321 requests in shadow
GET /orders      canary   -> cutover  after  2642 requests: Wilson lower bound 0.9950 over 1321 requests at 5% canary
GET /invoices    shadow   -> canary   after  1321 requests: Wilson lower bound 0.9950 over 1321 requests in shadow
GET /invoices    canary   -> halted   after  2503 requests: 3 divergences in the last 50 requests; $.order.currency: value ("GBP" != "USD")
```

`GET /invoices` broke at request 2,500 and was caught **while still serving 5%
of traffic**. `GET /refunds` has never failed and is still shadowing, because 60
requests is not evidence.

## What the proxy refuses to do

```
requests handled            : 90
mirrored to modern          : 20
skipped: unsafe method      : 50
skipped: response too large : 20
dropped: comparison backlog : 0
side effects in modern impl : 0
```

- **Unsafe methods are not mirrored.** Fifty POSTs, zero side effects. Mirroring
  a POST means running the new order-placement code for real. It is opt-in per
  route, because the default has to be the one that cannot charge a customer
  twice.
- **Oversized responses are excluded, and counted.** A shadow proxy that buffers
  whatever it is handed is a memory-exhaustion bug with a nice name.
- **Shadow work is shed under load, and counted.** The client path never waits
  for the comparison.
- **A panic in the modern implementation is a finding**, recorded as a
  divergence, and never reaches the client.

Every exclusion is counted, so *"we compared 100% of traffic"* stays a checkable
claim rather than a hope.

---

## Layout

```
internal/jsondiff/     semantic diff: patterns, specificity resolution, 6 ops, formats
internal/corpus/       deterministic generator: realistic noise + 10 labelled defects
internal/rulesets/     the 6 configurations, with the story of how a team gets to each
internal/score/        precision/recall/F1 and the per-defect breakdown
internal/confidence/   Wilson-bounded promotion state machine
internal/proxy/        the shadow proxy itself
cmd/strangler/         the experiment that produces docs/results.md
```

## Running it

```powershell
.\test.ps1          # go vet + go test ./...
.\demo.ps1          # the full argument, end to end
.\demo.ps1 -Save    # regenerate docs/results.md
```

Everything is deterministic: the corpus is a pure function of its seed, so
`docs/results.md` reproduces byte for byte.

## Design notes

- [ADR 0001 — Rules resolve by specificity, not by order](docs/adr/0001-specificity-not-order.md)
- [ADR 0002 — Six ops instead of one ignore](docs/adr/0002-six-ops-not-one-ignore.md)
- [ADR 0003 — Wilson bounds and per-stage evidence](docs/adr/0003-promotion-policy.md)
- [ADR 0004 — A mislabelled corpus looks exactly like a weak differ](docs/adr/0004-corpus-ground-truth.md)
- [ADR 0005 — The shadow path is allowed to lose work](docs/adr/0005-shedding-not-buffering.md)
- [Known limitations](docs/known-limitations.md)
- [Measured results](docs/results.md)
