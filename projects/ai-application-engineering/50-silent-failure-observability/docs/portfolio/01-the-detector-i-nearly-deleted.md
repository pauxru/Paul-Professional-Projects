# The detector I nearly deleted

I added answer self-similarity because of a hypothesis I was sure about. If a model starts
falling back on boilerplate — the same hedge, the same apology, the same "I'd recommend
contacting support" — then the answers it produces on a given day will look more like each
other than they used to. So: embed every answer in a day, take the mean pairwise cosine,
and watch it rise.

I wrote it, ran the panel, and it detected nothing. Not on the model swap, not on the
retrieval decay, not on the template regression it was practically designed for. Flat lines
in six scenarios. I assumed I had a bug, spent an hour failing to find one, and put it on
the list to delete.

Then I printed the raw numbers instead of the alerts. On `template-regression` the mean
pairwise cosine went from 0.3257 to 0.3208.

It moved. It moved *down*.

---

The hypothesis was backwards, and the reason is a fact about mixtures that I think is worth
carrying around.

A healthy day is one loose cluster: sixty answers about six topics, all drawn from the same
generative process, spread fairly evenly through the space. Mean pairwise cosine measures
the average tightness of that cloud.

`template-regression` replaces 8% of answers — the `warranty` ones — with near-identical
boilerplate. Intuitively that adds a very tight cluster, and a very tight cluster is very
self-similar, so the mean should rise.

But the mean is over *pairs*, and 8% of the answers generate 0.64% of the within-group
pairs. The other 99.36% of pairs are either healthy-to-healthy — unchanged — or
healthy-to-boilerplate. And that last group is the one that dominates: 15% of all pairs,
each one comparing a normal answer to a piece of text that has been pulled *away* from the
centre of the distribution into its own corner. Cross-cluster pairs are less similar than
the healthy pairs they replaced. The tiny gain from the tight cluster is swamped by the
larger loss from the cross terms.

A mixture of two tight clusters is *less* self-similar than one loose cluster. It is obvious
once you write down which pairs there are, and I had not written down which pairs there
were.

---

The fix was one line: make the test two-sided. Distance from the reference mean in either
direction, scaled by the reference standard deviation, rather than a rise above a
one-sided threshold.

Two-sided, the detector catches three of the four regressions. It is one day *early* on
`model-swap` and one day early on `refusal-creep` — it alerts before mean quality has
materially moved, because a mixture pulling apart is visible before the average of a bounded
score leaves its noise band. On `template-regression` it lands exactly on the material day
and is one of only two detectors on the panel that see the regression at all. It is in the
minimum covering set. It costs zero model calls per day.

The detector I was about to delete is half of the recommended configuration.

---

What I take from this is narrower than "test your assumptions", which is advice nobody can
act on. It is: **when a detector reports nothing, you have learned nothing yet.** A null
result from a monitor has at least four causes — the effect is absent, the statistic is
blind to it, the threshold is wrong, or the sign is wrong — and the alert output cannot
distinguish them. Only the raw statistic can. So the debugging move is not to stare at the
code, it is to print the number the code is thresholding and ask whether it moved at all.

Mine had moved by 1.5%, in the direction I had ruled out by assumption, and the assumption
was doing the ruling out before the data ever got a vote.

There is a mutant for it now, `self-similarity-one-sided`, which reverts the fix. It is
killed by a test asserting that a day of half-boilerplate scores *below* the healthy mean.
That test is the sentence I could not have written when I started, which is a reasonable
definition of what the exercise was for.
