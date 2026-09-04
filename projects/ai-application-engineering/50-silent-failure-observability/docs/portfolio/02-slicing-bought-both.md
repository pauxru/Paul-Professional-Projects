# Slicing bought sensitivity and specificity at the same time

Almost every knob in detection is a trade. Lower the threshold and you catch more
regressions and more noise. Add a detector and you cover more failure modes and page more
often. The shape of the problem is a curve, and you pick a point on it.

One change on this panel did not behave that way, and it took me a while to believe it.

---

**The problem.** `template-regression` breaks 8% of traffic — one topic out of six,
returning truncated boilerplate. Aggregate output PSI never alerts. Neither does MMD.
Neither does the quality canary. The regression is real, it is severe for the customers who
hit it, and four detectors watching the output distribution see nothing, because 8% of a
distribution moving a long way looks like 100% of a distribution moving a short way, and a
short way is inside the noise band.

**The other problem.** `input-shift` changes the traffic mix — more billing questions,
fewer warranty ones — with no change to the model and no change to answer quality. Output
PSI fires. Input PSI fires. MMD fires. Three of the most sensitive detectors on the panel
page you for a marketing campaign.

These read like the two ends of one dial. Make the distributional detectors more sensitive
and they will catch the 8% regression *and* fire harder on the benign shift.

**What actually happened.** `sliced_output_psi` computes PSI within each topic and takes
the worst slice. It catches `template-regression` — one of only two detectors that do, and
it lands exactly on the material day. And it does *not* fire on `input-shift`. At all.

More sensitive to the thing I care about, and blind to the thing I do not.

---

The reason is that the aggregate statistic was measuring a mixture, and both problems were
mixture artefacts pointing in opposite directions.

Write the output distribution as a mixture over topics: `P(answer) = Σ_t w_t · P(answer | t)`.
There are exactly two ways for it to move — the weights `w_t` change, or some component
`P(answer | t)` changes.

- `template-regression` changes one component and leaves the weights alone. In the
  aggregate it is scaled by `w_warranty ≈ 0.08`, which is the dilution.
- `input-shift` changes the weights and leaves every component alone. In the aggregate it
  is not scaled by anything, which is why it looks big.

Aggregate PSI adds these together and cannot tell them apart. Conditioning on topic
separates them by construction: within a slice the weight is 1 by definition, so weight
changes vanish, and component changes arrive undiluted.

It is not a clever trick. It is that the aggregate was answering a question nobody asked —
"has the marginal distribution of answers moved?" — when the question was "has the
conditional distribution of answers moved, for any topic?" The second question is better on
both axes because it is the *right* question, and the first one's sensitivity and its false
positives were the same defect seen from two sides.

---

It is not free. Each slice sees roughly a twelfth of the sample, so on the two *diffuse*
regressions — where every topic degrades together and the aggregate has no dilution problem
— sliced PSI is slower: 12 days behind aggregate PSI on `model-swap` (day 59 vs day 47),
and one day behind on `retrieval-decay` (day 62 vs day 61). That is the real
trade, and it is a trade between *kinds of regression*, not between sensitivity and
specificity. Concentrated failures want conditioning; diffuse failures want pooling. On this
panel the pair that covers everything is sliced PSI plus self-similarity, and greedy
set-cover picks it because it is the pair, not because it is the best single detector.

There is a floor, too: `MIN_SLICE = 8` samples in both windows, or the slice is skipped. On
low-traffic products the slices never fill and the detector degrades to "no opinion" —
silently, because a skipped slice is not an alert. That failure mode is in
`docs/known-limitations.md` and is monitored by nothing here, which is itself the kind of
thing this project exists to point at.

---

The generalisable version: **before tuning a threshold, check whether the statistic is
aggregating over a variable you know about.** If it is, conditioning on that variable is
usually not a point on the sensitivity/specificity curve — it is a different curve. Sliced
PSI does not beat aggregate PSI by being tuned better. It beats it by measuring something
else.
