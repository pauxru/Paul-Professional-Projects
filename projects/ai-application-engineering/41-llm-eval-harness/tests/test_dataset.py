"""Tests for versioned, content-addressed datasets and the corpus generator."""

from __future__ import annotations

import json

import pytest

from evalharness.corpus import make_dataset
from evalharness.dataset import Dataset, Item, TIERS


def item(i: str = "a", **kw) -> Item:
    kw.setdefault("prompt", "p")
    kw.setdefault("reference", "r")
    return Item(id=i, **kw)


# --- items -----------------------------------------------------------------


def test_item_requires_an_id():
    with pytest.raises(ValueError):
        item("")


def test_item_rejects_unknown_tier():
    with pytest.raises(ValueError):
        item("a", tier="impossible")


@pytest.mark.parametrize("tier", TIERS)
def test_item_accepts_every_declared_tier(tier):
    assert item("a", tier=tier).tier == tier


def test_item_is_frozen():
    i = item()
    with pytest.raises(Exception):
        i.id = "b"


def test_content_hash_is_stable():
    assert item().content_hash() == item().content_hash()


@pytest.mark.parametrize("field,value", [
    ("prompt", "different"), ("reference", "different"), ("tier", "hard"),
])
def test_content_hash_changes_with_every_meaningful_field(field, value):
    assert item().content_hash() != item(**{field: value}).content_hash()


def test_content_hash_changes_with_id():
    assert item("a").content_hash() != item("b").content_hash()


def test_content_hash_includes_tags():
    # Tags drive slice membership, so a silent retag moves an item between
    # slices and changes two numbers at once.
    assert item("a", tags=("x",)).content_hash() != item("a", tags=("y",)).content_hash()


def test_content_hash_is_insensitive_to_tag_order():
    assert (item("a", tags=("x", "y")).content_hash()
            == item("a", tags=("y", "x")).content_hash())


# --- datasets --------------------------------------------------------------


def test_empty_dataset_is_rejected():
    with pytest.raises(ValueError):
        Dataset.from_items("d", "v1", [])


def test_duplicate_ids_are_rejected():
    with pytest.raises(ValueError) as e:
        Dataset.from_items("d", "v1", [item("a"), item("a")])
    assert "duplicate" in str(e.value)


def test_duplicate_id_error_names_the_offender():
    with pytest.raises(ValueError) as e:
        Dataset.from_items("d", "v1", [item("a"), item("b"), item("a")])
    assert "'a'" in str(e.value)


def test_fingerprint_is_deterministic():
    a = Dataset.from_items("d", "v1", [item("a"), item("b")])
    b = Dataset.from_items("d", "v1", [item("a"), item("b")])
    assert a.fingerprint == b.fingerprint


def test_fingerprint_is_order_independent():
    # Reordering a file has not changed the instrument, and a fingerprint that
    # says otherwise trains people to ignore it.
    a = Dataset.from_items("d", "v1", [item("a"), item("b"), item("c")])
    b = Dataset.from_items("d", "v1", [item("c"), item("a"), item("b")])
    assert a.fingerprint == b.fingerprint


def test_fingerprint_changes_when_content_changes():
    a = Dataset.from_items("d", "v1", [item("a"), item("b")])
    b = Dataset.from_items("d", "v1", [item("a"), item("b", prompt="edited")])
    assert a.fingerprint != b.fingerprint


def test_fingerprint_changes_with_dataset_name():
    a = Dataset.from_items("one", "v1", [item("a")])
    b = Dataset.from_items("two", "v1", [item("a")])
    assert a.fingerprint != b.fingerprint


def test_fingerprint_ignores_version_label():
    # The version is a label; the content is the identity. Bumping a version
    # without changing anything must not invalidate a comparison.
    a = Dataset.from_items("d", "v1", [item("a")])
    b = Dataset.from_items("d", "v9", [item("a")])
    assert a.fingerprint == b.fingerprint


def test_short_fingerprint_is_a_prefix():
    d = Dataset.from_items("d", "v1", [item("a")])
    assert d.fingerprint.startswith(d.short_fingerprint)
    assert len(d.short_fingerprint) == 12


def test_len_and_ids():
    d = Dataset.from_items("d", "v1", [item("a"), item("b")])
    assert len(d) == 2
    assert d.ids() == ("a", "b")


def test_by_tier_filters():
    d = Dataset.from_items("d", "v1", [item("a", tier="easy"), item("b", tier="hard")])
    assert [i.id for i in d.by_tier("easy")] == ["a"]
    assert [i.id for i in d.by_tier("hard")] == ["b"]
    assert d.by_tier("adversarial") == ()


def test_by_tier_rejects_unknown_tier():
    d = Dataset.from_items("d", "v1", [item("a")])
    with pytest.raises(ValueError):
        d.by_tier("nonsense")


def test_by_tag_filters():
    d = Dataset.from_items("d", "v1", [item("a", tags=("x",)), item("b", tags=("y",))])
    assert [i.id for i in d.by_tag("x")] == ["a"]


def test_tier_counts_covers_all_tiers():
    d = Dataset.from_items("d", "v1", [item("a", tier="easy")])
    counts = d.tier_counts()
    assert set(counts) == set(TIERS)
    assert counts["easy"] == 1
    assert sum(counts.values()) == len(d)


def test_to_json_roundtrips_the_fingerprint():
    d = Dataset.from_items("d", "v1", [item("a"), item("b")])
    payload = json.loads(d.to_json())
    assert payload["fingerprint"] == d.fingerprint
    assert len(payload["items"]) == 2


# --- diffs -----------------------------------------------------------------


def test_identical_datasets_diff_to_nothing():
    a = Dataset.from_items("d", "v1", [item("a")])
    b = Dataset.from_items("d", "v2", [item("a")])
    diff = a.diff(b)
    assert diff.is_empty
    assert diff.comparable


def test_diff_detects_additions():
    a = Dataset.from_items("d", "v1", [item("a")])
    b = Dataset.from_items("d", "v2", [item("a"), item("b")])
    diff = a.diff(b)
    assert diff.added == ("b",)
    assert diff.removed == ()
    assert diff.modified == ()


def test_additions_alone_remain_comparable():
    a = Dataset.from_items("d", "v1", [item("a")])
    b = Dataset.from_items("d", "v2", [item("a"), item("b")])
    assert a.diff(b).comparable


def test_removals_break_comparability():
    a = Dataset.from_items("d", "v1", [item("a"), item("b")])
    b = Dataset.from_items("d", "v2", [item("a")])
    diff = a.diff(b)
    assert diff.removed == ("b",)
    assert not diff.comparable


def test_modifications_break_comparability():
    a = Dataset.from_items("d", "v1", [item("a")])
    b = Dataset.from_items("d", "v2", [item("a", reference="rewritten")])
    diff = a.diff(b)
    assert diff.modified == ("a",)
    assert not diff.comparable


def test_a_retagged_item_counts_as_modified():
    # It moved between slices. The id is the same and the item is not.
    a = Dataset.from_items("d", "v1", [item("a", tags=("billing",))])
    b = Dataset.from_items("d", "v2", [item("a", tags=("auth",))])
    assert a.diff(b).modified == ("a",)


def test_diff_str_reports_incomparability():
    a = Dataset.from_items("d", "v1", [item("a"), item("b")])
    b = Dataset.from_items("d", "v2", [item("a")])
    assert "NOT directly comparable" in str(a.diff(b))


def test_diff_str_reports_identity():
    a = Dataset.from_items("d", "v1", [item("a")])
    assert "identical" in str(a.diff(a))


def test_diff_is_directional():
    a = Dataset.from_items("d", "v1", [item("a")])
    b = Dataset.from_items("d", "v2", [item("a"), item("b")])
    assert a.diff(b).added == ("b",)
    assert b.diff(a).removed == ("b",)


# --- corpus generator ------------------------------------------------------


@pytest.mark.parametrize("n", [1, 7, 50, 51, 200, 999])
def test_counts_sum_exactly_to_n(n):
    # Rounding each proportion independently leaves the total off by a few,
    # which quietly makes a "50-item set" be 48 and every reported n wrong.
    assert len(make_dataset("d", "v1", n)) == n


def test_composition_is_respected():
    d = make_dataset("d", "v1", 100,
                     composition={"easy": 0.5, "medium": 0.5, "hard": 0.0,
                                  "adversarial": 0.0})
    counts = d.tier_counts()
    assert counts["easy"] == 50
    assert counts["medium"] == 50
    assert counts["hard"] == 0


def test_composition_must_sum_to_one():
    with pytest.raises(ValueError):
        make_dataset("d", "v1", 10, composition={"easy": 0.5, "medium": 0.2,
                                                 "hard": 0.0, "adversarial": 0.0})


def test_composition_rejects_unknown_tiers():
    with pytest.raises(ValueError):
        make_dataset("d", "v1", 10, composition={"easy": 1.0, "nonsense": 0.0})


def test_generator_is_deterministic():
    assert (make_dataset("d", "v1", 40).fingerprint
            == make_dataset("d", "v1", 40).fingerprint)


def test_item_ids_include_the_dataset_name():
    # Ids are the key everything downstream is seeded and paired on. Ids that
    # are unique only within a dataset make two different datasets the same
    # one -- which silently turned a held-out eval set into the selection set.
    a = make_dataset("selection", "v1", 20)
    b = make_dataset("holdout", "v1", 20)
    assert set(a.ids()).isdisjoint(set(b.ids()))


def test_two_named_datasets_are_never_accidentally_identical():
    a = make_dataset("selection", "v1", 20)
    b = make_dataset("holdout", "v1", 20)
    assert a.fingerprint != b.fingerprint
    diff = a.diff(b)
    assert set(diff.removed) == set(a.ids())
    assert set(diff.added) == set(b.ids())
    assert not diff.comparable


def test_same_name_different_versions_share_ids():
    # The drift experiment depends on this: v1 and v2 of the same eval set
    # must be diffable, not disjoint.
    a = make_dataset("drift", "v1", 40)
    b = make_dataset("drift", "v2", 40)
    assert set(a.ids()) == set(b.ids())


def test_largest_remainder_is_stable_across_equal_remainders():
    # Ties are broken by tier name so the result cannot depend on dict order.
    first = make_dataset("d", "v1", 10, composition={
        "easy": 0.25, "medium": 0.25, "hard": 0.25, "adversarial": 0.25})
    second = make_dataset("d", "v1", 10, composition={
        "adversarial": 0.25, "hard": 0.25, "medium": 0.25, "easy": 0.25})
    assert first.tier_counts() == second.tier_counts()
    assert first.fingerprint == second.fingerprint


def test_generated_items_carry_a_tag():
    for i in make_dataset("d", "v1", 20).items:
        assert len(i.tags) == 1
