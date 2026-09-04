# The number that changes the conversation

Arguing about ignore rules is unproductive. Both sides are reasoning from
plausibility, and both positions are plausible: "we need to ignore this or the
report is useless" and "if we ignore too much we'll miss things" are each
obviously true.

The argument only ends when someone puts a number on it.

## Build a ruler first

To measure a detector you need to know the right answers. So: 4,000 response
pairs, generated deterministically. Every pair carries realistic noise —
reordered line items, fresh UUIDs, clock drift, float artefacts. 812 of them
also carry exactly one of ten labelled defects drawn from the things that
actually break in a re-implementation:

```
total_off_by_a_penny         status_case_changed        discount_field_lost
customer_tier_changed        currency_changed           line_dropped
customer_id_became_string    timestamp_emptied          request_id_not_a_uuid
placed_at_became_epoch
```

Now every ruleset is a binary classifier with a confusion matrix.

## The result

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

The expected shape was a trade-off curve: tighter rules catch more bugs and
generate more noise, and the job is to pick a point on the curve.

**There is no curve.** Look at the last column. `precise` flags zero clean
responses. So does `value-blind`. So does `resigned`. Every ruleset below the
top row is buying **nothing** — not a single false positive avoided — in
exchange for up to 58 percentage points of detection.

This inverts the argument. The question was never "how much noise can we
tolerate in exchange for coverage". The relaxations were never about noise at
all. They were about how long it takes to type the rule.

## The specific keystrokes

The difference between `precise` and `value-blind` is two lines of config:

```go
{Pattern: "$.meta.requestId",  Op: OpFormat, Arg: "uuid"},      // vs OpIgnoreValue
{Pattern: "$.meta.generatedAt", Op: OpFormat, Arg: "rfc3339"},  // vs OpIgnoreValue
```

Both absorb the noise perfectly. `IgnoreValue` also absorbs `generatedAt: ""`
and `requestId: "null"` — a timestamp the new code forgot to set, and an ID that
came back as the literal string `null`. That is 162 defects, 20 percentage
points, for two words.

The `tolerant` row is the same shape. An absolute `0.01` tolerance is wide
enough to absorb float noise on a 581.17 total — and therefore, by construction,
wide enough to absorb a missing penny. A *relative* tolerance of `1e-9` absorbs
`484.310000000001` and still fails `581.16`. One keyword.

## Aggregate recall hides the shape

"70% recall" sounds survivable until you see which 30%:

```
defect                        subtree-ignored       tolerant       resigned
---------------------------------------------------------------------------
total_off_by_a_penny                   caught   MISSED 29/40   MISSED 29/40
status_case_changed                    caught         caught   MISSED 40/40
discount_field_lost                    caught         caught   MISSED 40/40
customer_tier_changed            MISSED 40/40   MISSED 40/40   MISSED 40/40
line_dropped                           caught         caught         caught
customer_id_became_string              caught         caught         caught
timestamp_emptied                MISSED 40/40   MISSED 40/40   MISSED 40/40
```

A weak ruleset is not uniformly degraded. It is **unevenly** degraded, and that
is precisely what makes it hard to notice. `subtree-ignored` still catches a
dropped order line every single time — the dashboard keeps finding bugs, so it
keeps looking trustworthy. It has simply stopped noticing that every customer
became `standard` tier, which is the one that ends up in a billing dispute.

## What this is worth in a review

"We should use tighter ignore rules" is an opinion, and it loses to "we don't
have time" every time.

"Our current ruleset misses 30% of injected defects including every case where a
timestamp comes back empty, and the fix is two config keywords and no additional
false positives" is not an opinion. It ends the meeting.
