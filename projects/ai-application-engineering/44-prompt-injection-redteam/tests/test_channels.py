import pytest

from redteam.channels import Capability, Span, Tainted, Trust, join


class TestTrustLattice:
    def test_ordering_is_total_and_descending(self):
        assert Trust.SYSTEM > Trust.OPERATOR > Trust.USER
        assert Trust.USER > Trust.TOOL > Trust.UNTRUSTED

    def test_join_takes_the_minimum(self):
        assert join(Trust.SYSTEM, Trust.UNTRUSTED) is Trust.UNTRUSTED
        assert join(Trust.USER, Trust.OPERATOR) is Trust.USER

    def test_join_of_one_is_identity(self):
        assert join(Trust.TOOL) is Trust.TOOL

    def test_join_of_nothing_is_the_top(self):
        # Vacuous truth: nothing untrusted has been mixed in yet.
        assert join() is Trust.SYSTEM

    def test_join_is_commutative(self):
        for a in Trust:
            for b in Trust:
                assert join(a, b) is join(b, a)

    def test_join_is_associative(self):
        levels = list(Trust)
        for a in levels:
            for b in levels:
                for c in levels:
                    assert join(join(a, b), c) is join(a, join(b, c))

    def test_join_is_idempotent(self):
        for a in Trust:
            assert join(a, a) is a

    def test_every_level_has_a_label(self):
        for level in Trust:
            assert level.label and level.label.islower()

    def test_labels_are_unique(self):
        assert len({t.label for t in Trust}) == len(list(Trust))


class TestTaintedConstruction:
    def test_of_records_text_and_trust(self):
        t = Tainted.of("hello", Trust.USER, "u")
        assert t.text == "hello"
        assert t.min_trust is Trust.USER

    def test_constructors_set_expected_levels(self):
        assert Tainted.system("a").min_trust is Trust.SYSTEM
        assert Tainted.user("a").min_trust is Trust.USER
        assert Tainted.untrusted("a").min_trust is Trust.UNTRUSTED

    def test_empty_is_falsy_length(self):
        assert len(Tainted()) == 0

    def test_len_matches_text(self):
        t = Tainted.user("abcdef")
        assert len(t) == len(t.text) == 6

    def test_str_is_the_text(self):
        assert str(Tainted.user("payload")) == "payload"

    def test_repr_mentions_trust(self):
        assert "user" in repr(Tainted.user("x"))

    def test_min_trust_of_empty_is_top(self):
        assert Tainted().min_trust is Trust.SYSTEM


class TestTaintedConcatenation:
    def test_concatenation_preserves_both_texts(self):
        joined = Tainted.system("A") + Tainted.untrusted("B")
        assert joined.text == "AB"

    def test_concatenation_min_trust_is_the_lower(self):
        joined = Tainted.system("A") + Tainted.untrusted("B")
        assert joined.min_trust is Trust.UNTRUSTED

    def test_concatenating_a_bare_string_raises(self):
        # The whole point of the type is that untracked text cannot enter by
        # accident. A str has no provenance, so there is no correct level to
        # assign it and silently picking one would be the bug.
        with pytest.raises(TypeError):
            Tainted.system("A") + "B"

    def test_concatenation_is_associative_in_text(self):
        a, b, c = (Tainted.system("a"), Tainted.user("b"),
                   Tainted.untrusted("c"))
        assert ((a + b) + c).text == (a + (b + c)).text

    def test_concatenation_is_associative_in_trust(self):
        a, b, c = (Tainted.system("a"), Tainted.user("b"),
                   Tainted.untrusted("c"))
        assert ((a + b) + c).min_trust is (a + (b + c)).min_trust

    def test_adjacent_spans_of_equal_trust_merge(self):
        joined = Tainted.user("ab") + Tainted.user("cd")
        assert len(joined.spans) == 1

    def test_adjacent_spans_of_different_trust_do_not_merge(self):
        joined = Tainted.user("ab") + Tainted.untrusted("cd")
        assert len(joined.spans) == 2

    def test_empty_operand_is_a_no_op(self):
        base = Tainted.user("abc")
        assert (base + Tainted()).text == "abc"


class TestCharacterGranularProvenance:
    def build(self):
        return (Tainted.system("SYS:") + Tainted.untrusted("BAD")
                + Tainted.user("USR"))

    def test_slice_inside_system_span(self):
        assert self.build().slice_trust(0, 4) is Trust.SYSTEM

    def test_slice_inside_untrusted_span(self):
        assert self.build().slice_trust(4, 7) is Trust.UNTRUSTED

    def test_slice_inside_user_span(self):
        assert self.build().slice_trust(7, 10) is Trust.USER

    def test_slice_spanning_a_boundary_takes_the_minimum(self):
        assert self.build().slice_trust(0, 7) is Trust.UNTRUSTED

    def test_slice_spanning_all_takes_the_minimum(self):
        assert self.build().slice_trust(0, 10) is Trust.UNTRUSTED

    def test_empty_slice_is_the_top(self):
        assert self.build().slice_trust(3, 3) is Trust.SYSTEM

    def test_substring_lookup_finds_untrusted_text(self):
        assert self.build().trust_of_substring("BAD") is Trust.UNTRUSTED

    def test_substring_lookup_finds_system_text(self):
        assert self.build().trust_of_substring("SYS:") is Trust.SYSTEM

    def test_substring_lookup_returns_none_when_absent(self):
        # None means "no evidence", which is a different answer from any
        # trust level and is what the broker's fourth rule keys on.
        assert self.build().trust_of_substring("nowhere") is None

    def test_substring_lookup_of_empty_string_is_none(self):
        assert self.build().trust_of_substring("") is None

    def test_substring_spanning_a_boundary_takes_the_minimum(self):
        assert self.build().trust_of_substring("SYS:BAD") is Trust.UNTRUSTED

    def test_origins_at_or_below_untrusted(self):
        t = Tainted.of("x", Trust.UNTRUSTED, "email-1")
        assert "email-1" in t.origins_at_or_below(Trust.UNTRUSTED)

    def test_origins_excludes_higher_trust(self):
        t = Tainted.system("a", "sys") + Tainted.untrusted("b", "doc")
        origins = t.origins_at_or_below(Trust.UNTRUSTED)
        assert "doc" in origins and "sys" not in origins

    def test_origins_at_user_includes_untrusted(self):
        t = Tainted.system("a", "sys") + Tainted.untrusted("b", "doc")
        assert "doc" in t.origins_at_or_below(Trust.USER)

    def test_iterating_yields_spans(self):
        spans = list(self.build())
        assert len(spans) == 3
        assert all(isinstance(s, Span) for s in spans)

    def test_span_length_matches_its_text(self):
        for span in self.build():
            assert len(span) == len(span.text)

    def test_spans_tile_the_text_without_gaps(self):
        t = self.build()
        assert "".join(s.text for s in t.spans) == t.text


class TestTaintedEquality:
    def test_equal_when_text_and_trust_match(self):
        assert Tainted.user("a") == Tainted.user("a")

    def test_unequal_when_trust_differs(self):
        assert Tainted.user("a") != Tainted.untrusted("a")

    def test_unequal_when_text_differs(self):
        assert Tainted.user("a") != Tainted.user("b")

    def test_not_equal_to_a_bare_string(self):
        assert Tainted.user("a") != "a"

    def test_hashable_and_consistent_with_equality(self):
        assert hash(Tainted.user("a")) == hash(Tainted.user("a"))

    def test_usable_in_a_set(self):
        assert len({Tainted.user("a"), Tainted.user("a")}) == 1


class TestCapability:
    def test_requires_clean_for_listed_argument(self):
        cap = Capability("send_email", Trust.USER, frozenset({"to"}))
        assert cap.requires_clean("to")

    def test_does_not_require_clean_for_other_arguments(self):
        cap = Capability("send_email", Trust.USER, frozenset({"to"}))
        assert not cap.requires_clean("body")

    def test_no_sensitive_arguments_by_default(self):
        cap = Capability("summarise", Trust.UNTRUSTED)
        assert not cap.requires_clean("anything")
