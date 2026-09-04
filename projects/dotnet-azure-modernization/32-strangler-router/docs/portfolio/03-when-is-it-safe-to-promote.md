# A hundred per cent is not a number

Every migration dashboard shows a match rate. Nobody looks at the denominator.

```
GET /orders     4000 / 4000    100.0%   ✅ ready to promote
GET /refunds      11 /   11    100.0%   ✅ ready to promote
```

Both rows are true. Only one of them is evidence.

## What the interval says

The Wilson score lower bound answers a different question: *given what I have
seen, how bad could the true rate plausibly be?*

```
requests  matched  observed   wilson(99%)   promote at 0.995?
      11       11    100.0%        0.6237             false
      40       40    100.0%        0.8577             false
     200      200    100.0%        0.9679             false
     800      800    100.0%        0.9918             false
   2,000    2,000    100.0%        0.9967              true
   4,000    4,000    100.0%        0.9983              true
```

Eleven perfect requests are consistent with a true match rate of 62%. The
endpoint is not blocked because it failed. It is blocked because **nobody has
looked at it**.

This matters because of *which* endpoints have low traffic. It is never the
product listing page. It is the refund path, the B2B invoice run, the admin
screen finance uses on the last working day of the month. Low traffic and high
consequence are correlated, and a raw match rate is blindest exactly there.

## Two more things fall out of taking it seriously

**Evidence does not carry across stages.**

A spotless shadow record says the modern read path produces the same bytes when
its results are thrown away. It says nothing about how the service behaves once
it is *serving*, where connection pool limits, cache warmth, downstream timeouts
and write amplification are all different.

So `stageTotal` resets on every promotion. Shadow evidence gets you to canary.
Canary evidence — and only canary evidence — gets you to cutover. It roughly
doubles the time to cut over, and every one of those requests is a request where
the modern implementation was actually in the path.

**Rollback cannot use the average that promoted you.**

This is the part that surprises people. An endpoint with 2,500 clean samples
starts failing *every single request*. How long until a 99.5% threshold on the
lifetime average fires?

About twelve thousand requests.

The number that made cutover safe is the number that makes rollback slow, and it
gets worse the longer the endpoint has been healthy. So rollback watches a recent
window instead: **3 divergences in the last 50**, evaluated *before* promotion,
clearing the window on transition.

## What it looks like running

Three endpoints, driven through the real proxy:

```
route                  stage     requests   matched   observed    in stage stage wilson
--------------------------------------------------------------------------------------
GET /invoices          halted        4000      2500     62.50%        1497       0.0000
GET /orders           cutover        4000      4000    100.00%        1358       0.9951
GET /refunds           shadow          60        60    100.00%          60       0.9004

GET /orders    shadow  -> canary   after 1321 requests: Wilson lower bound 0.9950 over 1321 requests in shadow
GET /orders    canary  -> cutover  after 2642 requests: Wilson lower bound 0.9950 over 1321 requests at 5% canary
GET /invoices  shadow  -> canary   after 1321 requests: Wilson lower bound 0.9950 over 1321 requests in shadow
GET /invoices  canary  -> halted   after 2503 requests: 3 divergences in the last 50; $.order.currency: value ("GBP" != "USD")
```

`GET /invoices` shadowed cleanly for 1,321 requests, was promoted to 5% canary,
and a deploy at request 2,500 changed its currency handling. It was halted three
divergences later — **while still serving 5% of traffic**, not 100%.

`GET /refunds` has never diverged once. It is still shadowing. That is the
system working.

## The honest caveats

Wilson assumes independent Bernoulli trials, and real request outcomes are
correlated — a bad deploy makes every request fail at once. That correlation
pushes the promotion bound in the safe direction, which is why it is used for
promotion; it is why rollback uses a window instead.

Three-in-fifty is a 6% failure rate, so an endpoint that genuinely diverges on
~1% of traffic will flap around the threshold depending on clustering. A CUSUM
would degrade more gracefully. A fixed window was chosen because it can be
explained to an on-call engineer at 3am in one sentence, and at 3am that is
worth more than smoothness.

Both of these are in `docs/known-limitations.md`, because a promotion gate whose
weaknesses are undocumented is a promotion gate that will be trusted in the one
situation it cannot handle.
