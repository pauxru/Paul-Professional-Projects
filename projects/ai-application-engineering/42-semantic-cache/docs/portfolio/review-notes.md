# What I would ask about this in review

Written as the reviewer, not the author. The point of the document is that the
weakest parts should be the easiest to find.

---

### "Your encoder is TF-IDF. Isn't the whole result just 'bag of words is bad'?"

The fair challenge, and the answer is in two parts.

**Section 1's identity is not about TF-IDF.** For any L2-normalised pooled
vector, `cos(a,b) = Σ_shared sqrt(w_a · w_b)`. Mean-pooled sentence-transformer
embeddings are L2-normalised pooled vectors. The dilution mechanism — one
token's contribution to a mean shrinks as the document grows — is a property of
pooling, and it is why "shipping to Germany" and "shipping to France" score high
under *any* pooled encoder. A better model narrows the gap. It cannot remove it,
because it is not modelling the wrong thing; it is averaging.

**Every absolute number would move**, and known-limitations says so in a table
with a row per finding. A transformer would raise paraphrase scores and lower
confusable ones. The interleaving would shrink. The claim "no threshold
separates these populations" would weaken to "the threshold is narrower than you
think".

What would *not* change: sections 2, 3b and 5 are about metrics and system
structure, not about the model. A perfect encoder does not make a false-hit rate
independent of traffic mix, does not give an IDF distribution a tunable range,
and does not let cache precision see coalescing errors.

---

### "170 queries is nothing."

Correct, and it has one specific consequence the report leans on: section 3's
finding that every IDF percentile from the 10th to the 90th returns the same cut
is partly a corpus-size artefact. The report says so at the point of the claim.

It is kept because it is also what happens to real teams who compute IDF over
their own document set instead of a background corpus, and because the *fix* —
a top-K budget instead of a threshold — is the right answer regardless of corpus
size. K is insensitive to the shape of the IDF distribution, which is precisely
why it survives the criticism that killed the threshold.

The confusable families are hand-designed to isolate different *kinds* of
decisive token — proper noun, plan name, polarity, direction, numeric code — so
that a finding holding across all of them is about pooling rather than about one
quirk. That is what the corpus buys, and no public dataset provides it, because
public intent datasets are built to be separable.

---

### "You wrote the lexicon by hand. Isn't the result circular?"

The sharpest question, and the reason two of the repository's most important
tests are in `internal/lexicon`.

A hand-written table that mapped `germany → country-de` and
`france → country-fr` would separate the shipping family *by hand*, and section
1 would be a description of the author's table.

`TestNoLexiconEntryTouchesAnIrreducibleToken` forbids any entry, on either side,
mentioning an entity, plan name, HTTP status code, tenant or polarity particle.
`TestFamilySiblingsNeverCollapseToTheSameConcept` forbids collapsing two members
of a confusable family — and **found a real bug on its first run**
(`remove → disable`, merging the teammate family into the 2FA family).

Neither proves the absence of subtler bias. The table is ~120 lines and
readable; the honest answer is "read it", and the README says so.

---

### "Your best guard setting comes from a grid search on the same data you report."

Yes, and there are three mitigations, none of which fully answers it.

The sweep is scored on **correct answers served from cache**, which cannot be
won by moving the threshold to an extreme — hit rate is maximised by admitting
everything, precision by admitting nothing, the product by being right.

The full sweep is printed, not just the best cell, so a reader can see whether
the winner is a plateau or a spike. It is a plateau: K=3 and K=4 at J≥0.67 all
reach 0.0% confusable error.

And section 3b prices the same setting on a **held-out population split** —
paraphrase pairs versus confusable pairs, constructed from labels rather than
from the sweep — which is where its true cost (81.4% of paraphrase hits) shows
up. That number is not flattering, and it is the one that changed the
recommendation.

What is still missing: a genuine train/test split of the corpus. With 170
queries it would be noise. Stated in known-limitations.

---

### "The gateway is a simulation. Why should I believe any of the latency claims?"

You should not, and the report makes none. There is no latency claim anywhere in
`docs/results.md` — no p99, no throughput, no saving in milliseconds. Backend
latency exists only to give coalescing windows a width.

What the simulation buys is **attribution**: the backend is an oracle that always
answers the question asked, so every wrong answer is caused by the cache or the
coalescer and nothing else. That is what makes precision measurable at all. In a
real system a wrong answer could be the model's fault, and the entire study would
be unattributable.

---

### "`Precision()` returns 1 when there are no hits. That's a bug."

It is a documented trade, and it is in known-limitations under the report's own
thesis. Vacuous truth is standard; returning 0 would misreport a genuinely
error-free run as a failure. A configuration that never hits displaying as
flawless is exactly the kind of metric this project is about, so it is pinned by
a test and called out rather than quietly changed.

If I were shipping this as a library, `Precision()` would return
`(float64, bool)` or a `*float64`. In a report generator where the hit count is
always printed alongside, the ceremony is not worth it.

---

### "You have no `-race`. Is `internal/flight` correct?"

**Unverified, and the README says so.** The race detector needs cgo and a C
toolchain; there is no gcc, clang, mingw or msys on this machine, so `-race`
cannot be run at all.

The compensations are real but weaker: a `-count=50` stress run at
`GOMAXPROCS=8`, plus tests asserting invariants a data race would eventually
violate — exactly one leader, every follower receiving the identical byte
sequence, a late joiner receiving the full prefix, the channel closed once.
Repeated execution is not a happens-before proof.

The mitigating structural fact is that the group owns the subscriber list under
one lock, so there is no window in which a joiner is neither registered nor
served a finished result. That is a design argument, not evidence.
`known-limitations.md` says to run `-race` elsewhere before believing it.

---

### "Section 3b contradicts section 3. Which is it?"

Both. Section 3 measures the veto on aggregate traffic and finds it costs one
point of hit rate. Section 3b measures it on the paraphrase population and finds
it rejects 81.4%. The numbers are consistent because 81.8% of hits are verbatim
repeats that the veto passes for free.

The apparent contradiction *is* the finding, and it is the same error section 2
is about — a rate averaged over traffic you do not control measures the traffic.
Section 3b exists because a unit test asserted the comfortable version and
failed. Had I not written that test, the report would have shipped with a
recommendation I now believe is wrong for half its readers.

---

### "What is the single weakest claim here?"

Section 1's conclusion that "the populations interleave, so no threshold works",
generalised beyond TF-IDF.

The *identity* is solid and exactly verified. The *interleaving* is measured on
this corpus with this encoder, and a strong sentence encoder would separate the
populations better than TF-IDF does. I believe the qualitative claim survives —
because dilution is a property of pooling, and because the confusable families
are designed so the distinguishing token is always exactly one word — but I have
not demonstrated it, and no amount of care with TF-IDF would.

The claim I would defend hardest is section 2's: a false-hit rate measured on
production traffic is a measurement of that traffic. That one needs no encoder
at all.
