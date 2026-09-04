# The problem

A migration is agreed. Old system on the left, new system on the right, cut over
one endpoint at a time. Everyone has read the strangler-fig article. The proxy
gets built in a fortnight and it works: traffic is mirrored, both
implementations are called, responses are compared, a report comes out.

The report says every single response is different.

Not because the new implementation is wrong. Because two correct implementations
of the same endpoint do not produce identical bytes:

```
$.meta.durationMs      18 != 12
$.meta.generatedAt     "2024-06-12T09:31:45Z" != "…46Z"
$.meta.requestId       "d708d49e-…" != "2b2ce93e-…"
$.order.lines[0].sku   "SKU-59499" != "SKU-33365"
$.order.subtotal       484.31 != 484.310000000001
```

A duration. A clock tick. A fresh UUID. A different `ORDER BY` on the line items
because the new query planner chose a different index. One float that took a
different addition order.

Twelve differences on a perfectly correct response. Multiply by every response
on every endpoint.

## The report dies in week three

The response is always the same, and it is always reasonable. Someone adds an
ignore rule for `requestId`. Then `generatedAt`. Then `durationMs`. Then someone
says "honestly the whole `meta` block is noise" and ignores the subtree — which
is one line shorter and reads as a tidy-up.

Then a rounding difference shows up. `581.17` versus `581.16`. Someone
investigates for an afternoon, decides it is floating point, and adds a blanket
`0.01` tolerance on every number in the document. That is defensible: 0.01 *is*
about the size of a float artefact on a number that big.

Then a field starts flapping and gets ignored. Then `status` comes back
lowercase from the new service and someone says "that's just casing, the client
uppercases it anyway".

Six weeks later the dashboard is green, the report is quiet, and the diff is
comparing about forty per cent of what matters. Nobody made a bad decision.
Every rule was added by a reasonable person during a busy week.

## Then the promotion gate

Even with a working diff, someone has to decide when to cut over. The gate is
always the same: a match rate and a threshold. *Promote at 99.5%.*

Two endpoints. Both have matched on every request they have ever seen. One has
seen 4,000 requests. The other has seen 11.

Both report 100%. Both get promoted.

The endpoint with 11 requests is not the product listing page. Low-traffic
endpoints are low-traffic for structural reasons: the refund path, the B2B
invoice run, the admin screen that finance uses on the last day of the month.
They are the endpoints where being wrong is most expensive and takes longest to
notice.

## What this project is

Two claims, both measured rather than asserted.

**A ruleset is a classifier, so score it like one.** Six rulesets — the six a
team actually walks through — evaluated against 4,000 response pairs with known
ground truth. Precision, recall, and a per-defect breakdown of exactly what each
one stops seeing.

**A match rate is not a confidence level.** The promotion gate runs on the
Wilson score lower bound, each stage requires its own evidence, and rollback
watches a recent window rather than a lifetime average.

The result is uncomfortable and specific: the careful ruleset costs nothing.
It is not slower, it is not noisier, it does not produce more false positives.
It is ten minutes more typing, once, and it is worth 58 percentage points of
detection.
