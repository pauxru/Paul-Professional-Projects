"""Versioned, content-addressed golden datasets.

An eval set is a measuring instrument, and the one property a measuring
instrument must have is that it does not change without anyone noticing. In
practice eval sets change constantly: someone fixes a typo in an expected
answer, someone adds fifteen examples from last week's incident, someone
removes an item everyone agrees is ambiguous.

Each of those is reasonable. Together they mean that "quality went from 0.82 to
0.86" might be entirely explained by the eval set having changed underneath the
comparison, and there is usually no record of whether it did.

The fix is unglamorous: hash the content, version it explicitly, and refuse to
compare runs made against different datasets unless the caller says so and the
report says so.
"""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass, field, asdict
from typing import Iterable

# Item difficulty tiers. Reporting only an aggregate score hides the case that
# matters most: a change that improves easy items and breaks hard ones nets out
# to "no change" and looks safe.
TIERS = ("easy", "medium", "hard", "adversarial")


@dataclass(frozen=True)
class Item:
    """One eval case."""

    id: str
    prompt: str
    reference: str
    tier: str = "medium"
    tags: tuple[str, ...] = ()

    def __post_init__(self) -> None:
        if not self.id:
            raise ValueError("item needs an id")
        if self.tier not in TIERS:
            raise ValueError(f"unknown tier {self.tier!r}; expected one of {TIERS}")

    def content_hash(self) -> str:
        """Hash of the semantically meaningful fields.

        Tags are included because a tag drives which slice an item lands in,
        and a silently retagged item moves between slices and changes two
        numbers at once.
        """
        payload = json.dumps(
            {"id": self.id, "prompt": self.prompt, "reference": self.reference,
             "tier": self.tier, "tags": sorted(self.tags)},
            sort_keys=True, ensure_ascii=False,
        )
        return hashlib.sha256(payload.encode("utf-8")).hexdigest()


@dataclass(frozen=True)
class Dataset:
    """An immutable, content-addressed collection of eval items."""

    name: str
    version: str
    items: tuple[Item, ...]
    notes: str = ""
    _fingerprint: str = field(default="", repr=False, compare=False)

    def __post_init__(self) -> None:
        if not self.items:
            raise ValueError("an empty dataset measures nothing")
        ids = [i.id for i in self.items]
        dupes = {x for x in ids if ids.count(x) > 1}
        if dupes:
            # A duplicated id is not a cosmetic problem: it silently
            # double-weights an item in every aggregate, and pairing between
            # runs becomes ambiguous.
            raise ValueError(f"duplicate item ids: {sorted(dupes)}")
        object.__setattr__(self, "_fingerprint", self._compute_fingerprint())

    def _compute_fingerprint(self) -> str:
        """Order-independent hash of the dataset's content.

        Sorting the per-item hashes before combining means that reordering the
        file does not change the fingerprint. That is the behaviour you want:
        a diff that only moves lines around has not changed the instrument, and
        a fingerprint that says otherwise trains people to ignore it.
        """
        h = hashlib.sha256()
        h.update(self.name.encode("utf-8"))
        for item_hash in sorted(i.content_hash() for i in self.items):
            h.update(item_hash.encode("ascii"))
        return h.hexdigest()

    @property
    def fingerprint(self) -> str:
        return self._fingerprint

    @property
    def short_fingerprint(self) -> str:
        return self._fingerprint[:12]

    def __len__(self) -> int:
        return len(self.items)

    def ids(self) -> tuple[str, ...]:
        return tuple(i.id for i in self.items)

    def by_tier(self, tier: str) -> tuple[Item, ...]:
        if tier not in TIERS:
            raise ValueError(f"unknown tier {tier!r}")
        return tuple(i for i in self.items if i.tier == tier)

    def by_tag(self, tag: str) -> tuple[Item, ...]:
        return tuple(i for i in self.items if tag in i.tags)

    def tier_counts(self) -> dict[str, int]:
        return {t: len(self.by_tier(t)) for t in TIERS}

    def diff(self, other: "Dataset") -> "DatasetDiff":
        """What changed between two versions of an eval set."""
        mine = {i.id: i.content_hash() for i in self.items}
        theirs = {i.id: i.content_hash() for i in other.items}
        added = tuple(sorted(set(theirs) - set(mine)))
        removed = tuple(sorted(set(mine) - set(theirs)))
        modified = tuple(sorted(
            k for k in set(mine) & set(theirs) if mine[k] != theirs[k]
        ))
        return DatasetDiff(self.version, other.version, added, removed, modified)

    def to_json(self) -> str:
        return json.dumps(
            {"name": self.name, "version": self.version, "notes": self.notes,
             "fingerprint": self.fingerprint,
             "items": [asdict(i) for i in self.items]},
            indent=2, sort_keys=True, ensure_ascii=False,
        )

    @classmethod
    def from_items(cls, name: str, version: str, items: Iterable[Item],
                   notes: str = "") -> "Dataset":
        return cls(name=name, version=version, items=tuple(items), notes=notes)


@dataclass(frozen=True)
class DatasetDiff:
    """The difference between two dataset versions."""

    from_version: str
    to_version: str
    added: tuple[str, ...]
    removed: tuple[str, ...]
    modified: tuple[str, ...]

    @property
    def is_empty(self) -> bool:
        return not (self.added or self.removed or self.modified)

    @property
    def comparable(self) -> bool:
        """Whether scores from the two versions can be compared directly.

        Additions alone are survivable if the comparison is restricted to the
        shared item set. Removals and modifications are not, because a removed
        item was usually removed for being hard and a modified reference
        changes what "correct" means -- both move the score without anything
        about the system having changed.
        """
        return not (self.removed or self.modified)

    def __str__(self) -> str:
        if self.is_empty:
            return f"{self.from_version} -> {self.to_version}: identical"
        return (f"{self.from_version} -> {self.to_version}: "
                f"+{len(self.added)} -{len(self.removed)} ~{len(self.modified)}"
                f"{'' if self.comparable else ' (NOT directly comparable)'}")
