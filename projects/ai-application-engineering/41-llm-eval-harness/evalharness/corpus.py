"""Synthetic eval sets with controlled composition.

Real eval sets are assembled by hand and their tier composition is an accident
of who wrote them. That accident matters: an eval set that is 70% easy items
has a lower variance of paired differences than one that is 25% adversarial,
and variance is the only thing that decides how much statistical power you
have. Two teams with the same eval set size can have wildly different ability
to detect the same regression, and neither will know.

Being able to build sets with a specified composition is what makes that
claim measurable rather than assertable.
"""

from __future__ import annotations

from .dataset import Dataset, Item, TIERS

_TOPICS = ("billing", "auth", "search", "reporting", "onboarding",
           "permissions", "export", "notifications")


def make_dataset(
    name: str,
    version: str,
    n: int,
    *,
    composition: dict[str, float] | None = None,
    notes: str = "",
) -> Dataset:
    """Build a dataset of `n` items with the given tier proportions.

    Proportions are resolved by largest-remainder so the counts sum to exactly
    `n` and are a deterministic function of the inputs. Rounding each
    independently would leave the total off by a few, which is the sort of
    thing that makes a "50-item set" quietly be 48 and every reported n wrong.
    """
    composition = composition or {"easy": 0.25, "medium": 0.35, "hard": 0.25,
                                  "adversarial": 0.15}
    unknown = set(composition) - set(TIERS)
    if unknown:
        raise ValueError(f"unknown tiers in composition: {sorted(unknown)}")
    total = sum(composition.values())
    if abs(total - 1.0) > 1e-9:
        raise ValueError(f"composition must sum to 1.0, got {total}")

    exact = {t: composition.get(t, 0.0) * n for t in TIERS}
    counts = {t: int(exact[t]) for t in TIERS}
    shortfall = n - sum(counts.values())
    for t in sorted(TIERS, key=lambda t: (-(exact[t] - counts[t]), t))[:shortfall]:
        counts[t] += 1

    items: list[Item] = []
    k = 0
    for tier in TIERS:
        for j in range(counts[tier]):
            topic = _TOPICS[k % len(_TOPICS)]
            items.append(Item(
                # The dataset name is part of the item id, and that is not
                # cosmetic. Item ids are the key everything downstream is
                # seeded and paired on, so ids that are unique only *within* a
                # dataset silently make two different datasets the same one.
                # The first version of this function used `f"{tier}-{j:03d}"`,
                # which made a held-out eval set byte-identical to the set used
                # to select on -- and the winner's-curse experiment in section
                # 6 duly reported that zero percent of the apparent gain
                # evaporated on held-out data. A perfect result, from measuring
                # the same data twice.
                id=f"{name}:{tier[:3]}-{j:03d}",
                prompt=f"[{tier}/{topic}] question {j} about {topic}",
                reference=f"expected answer {j} for {topic}",
                tier=tier,
                tags=(topic,),
            ))
            k += 1
    return Dataset.from_items(name, version, items, notes=notes)
