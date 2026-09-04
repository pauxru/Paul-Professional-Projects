# Known limitations

Written down because a migration tool whose weaknesses are undocumented is a
migration tool that will be trusted in the one situation it cannot handle.

## The measurement

**The corpus is synthetic.** Ten defect types over one document shape. They were
chosen from the kinds of thing that actually go wrong in a re-implementation — a
rounding difference, a casing change, a dropped field, a type that became a
string — but they are not sampled from a real migration. The *ordering* of the
rulesets is robust and would survive a different corpus. The specific numbers
(812/812, 20 points, 58 points) are properties of this corpus.

**Defect rate is 20%.** Real shadow runs see far less. A lower rate does not
change recall, which is computed only over defective pairs, but it does change
how the precision numbers *feel*: at a 0.1% defect rate, even a small
false-positive rate drowns the signal. The `exact` row is the extreme case of
this and it is why it is included.

**One document shape.** Deeply nested documents, large arrays of arrays, and
polymorphic response bodies are not exercised. The unordered-array matcher in
particular is O(n²) in the array length and pairs by best match, which is fine
for order lines and would not be fine for a 10,000-element result set.

## The differ

**Specificity is CSS's rule, and it is approximate.** `$.a.*.c` and `$.a.b.d`
both score 4; the tie is broken by declaration order. Patterns in a real ruleset
are near-disjoint so this has not arisen, but it is a real ambiguity.

**No schema awareness.** The differ does not know that `$.order.total` is money
and `$.meta.durationMs` is not. Every rule is written per-path by a human. A
schema-driven mode — "all fields of type `money` get a relative tolerance of
1e-9" — would be strictly better and is not implemented.

**JSON only.** No XML, no protobuf, no form-encoded bodies. The `Compare`
signature takes `any`, so a decoder for another format would slot in, but the
format registry and the pattern syntax both assume JSON's data model.

**Streaming and very large responses are excluded, not handled.** Above
`MaxBodyBytes` the response is not compared at all. A streaming comparison that
hashes windows rather than buffering would be the right answer.

## The promotion policy

**Wilson assumes independent Bernoulli trials.** Request outcomes are correlated
in exactly the way that matters: a bad deploy makes every request fail at once.
This makes the promotion bound *conservative* — a burst of failures crushes it,
which is the safe direction — but the confidence level should not be read as a
calibrated probability.

**The rollback threshold has a flapping zone.** Three failures in fifty is a 6%
rate. An endpoint that genuinely diverges on ~1% of traffic will sometimes trip
it and sometimes not, depending on clustering. A CUSUM or an EWMA would degrade
more gracefully than a fixed window; the fixed window was chosen because its
behaviour can be explained to an on-call engineer at 3am in one sentence.

**Halted is sticky and requires human action.** Deliberate, but it means a
transient downstream outage during a canary permanently halts the endpoint until
someone resets it. There is no cool-off.

**Per-stage evidence has a cost.** Requiring a fresh soak at each stage roughly
doubles the requests needed to reach cutover. For a genuinely low-traffic
endpoint this can mean weeks. The alternative — carrying shadow evidence
forward — is worse, but "weeks" is a real operational cost and should be
budgeted, not discovered.

## The proxy

**Dropping comparisons biases the sample.** Under load the proxy compares a
non-random subset of traffic, and load is precisely when implementations
diverge. `DroppedQueue` makes the bias visible. It does not remove it.

**Comparison happens after the client has been served**, so a divergence is
detected strictly after the legacy response has gone out. That is the correct
trade — the alternative is putting the modern implementation's latency on the
client path — but it means the proxy cannot *prevent* anything, only detect.

**No request-body comparison.** Only responses are compared. A modern
implementation that mis-parses a request in a way that happens to produce the
same response is invisible.

**Header comparison is limited to the status code.** `Cache-Control`,
`ETag`, `Location` and `Set-Cookie` differences are all real migration defects
and none of them are detected.

**Single-process, in-memory state.** The confidence tracker does not survive a
restart and is not shared across proxy instances. A real deployment needs the
counters in a store that several instances can agree on, which introduces
questions about consistency that this project does not address.
