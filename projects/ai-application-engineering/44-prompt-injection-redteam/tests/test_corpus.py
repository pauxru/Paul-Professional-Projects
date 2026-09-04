from collections import Counter

from redteam.channels import Trust
from redteam.corpus import (ENCODING_LEGIBILITY, Family, Goal, OBFUSCATIONS,
                            load_corpus)

CORPUS = load_corpus()


class TestCorpusShape:
    def test_corpus_is_not_empty(self):
        assert len(CORPUS.attacks) > 50

    def test_benign_corpus_is_substantial(self):
        # A red-team corpus with no benign half cannot report a false
        # positive rate, and a detection rate alone is not a measurement.
        assert len(CORPUS.benign) >= 40

    def test_every_family_is_represented(self):
        present = {a.family for a in CORPUS.attacks}
        assert present == set(Family)

    def test_every_goal_is_represented(self):
        present = {a.goal for a in CORPUS.attacks}
        assert present == set(Goal)

    def test_families_property_matches_attacks(self):
        assert set(CORPUS.families) == {a.family for a in CORPUS.attacks}

    def test_no_family_is_a_singleton(self):
        counts = Counter(a.family for a in CORPUS.attacks)
        assert all(n >= 3 for n in counts.values())

    def test_authorised_tool_family_exists(self):
        # The realistic case: the user holds the capability and only the
        # argument is attacker-chosen. Without it, every tool attack is
        # refused on the channel and the broker looks stronger than it is.
        assert any(a.family is Family.AUTHORISED_TOOL for a in CORPUS.attacks)

    def test_authorised_tool_attacks_arrive_untrusted(self):
        for a in CORPUS.attacks:
            if a.family is Family.AUTHORISED_TOOL:
                assert a.channel is Trust.UNTRUSTED


class TestIdentity:
    def test_markers_are_unique(self):
        markers = [a.marker for a in CORPUS.attacks]
        assert len(set(markers)) == len(markers)

    def test_ids_are_unique(self):
        ids = [a.id for a in CORPUS.attacks]
        assert len(set(ids)) == len(ids)

    def test_benign_ids_are_unique(self):
        ids = [b.id for b in CORPUS.benign]
        assert len(set(ids)) == len(ids)

    def test_every_attack_embeds_its_marker(self):
        # Success is judged by exact marker match, so an attack whose
        # payload lost its marker can never be scored as successful and
        # would silently deflate the ASR.
        for a in CORPUS.attacks:
            if a.family is not Family.OBFUSCATED:
                assert a.marker in a.payload, a.id

    def test_markers_are_not_english_words(self):
        for a in CORPUS.attacks:
            assert a.marker.isupper() and any(c.isdigit() for c in a.marker)

    def test_no_marker_is_a_substring_of_another(self):
        markers = sorted({a.marker for a in CORPUS.attacks}, key=len)
        for i, short in enumerate(markers):
            for long in markers[i + 1:]:
                assert short not in long


class TestDeterminism:
    def test_loading_twice_gives_the_same_digest(self):
        assert load_corpus().digest == load_corpus().digest

    def test_digest_is_content_addressed(self):
        assert len(CORPUS.digest) == 16
        assert all(c in "0123456789abcdef" for c in CORPUS.digest)

    def test_loading_twice_gives_equal_payloads(self):
        a = [x.payload for x in load_corpus().attacks]
        b = [x.payload for x in load_corpus().attacks]
        assert a == b

    def test_attack_order_is_stable(self):
        a = [x.id for x in load_corpus().attacks]
        b = [x.id for x in load_corpus().attacks]
        assert a == b


class TestLegibility:
    def test_every_obfuscation_declares_a_legibility(self):
        assert set(OBFUSCATIONS) == set(ENCODING_LEGIBILITY)

    def test_legibilities_are_in_range(self):
        assert all(0.0 < v <= 1.0 for v in ENCODING_LEGIBILITY.values())

    def test_obfuscated_attacks_carry_their_legibility(self):
        for a in CORPUS.attacks:
            if a.family is Family.OBFUSCATED:
                kind = a.notes.replace("obfuscation: ", "")
                assert a.legibility == ENCODING_LEGIBILITY[kind]

    def test_plain_attacks_are_fully_legible(self):
        for a in CORPUS.attacks:
            if a.family is not Family.OBFUSCATED:
                assert a.legibility == 1.0

    def test_heavy_encodings_cost_more_than_light_ones(self):
        assert ENCODING_LEGIBILITY["base64"] < ENCODING_LEGIBILITY["bidi"]

    def test_legibility_does_not_depend_on_the_normaliser(self):
        # The regression this guards: comprehension cost was once derived
        # from normalize(), making the attacker's difficulty a function of
        # the defender's decoder. rot13 is the case that exposed it -- the
        # normaliser cannot recover it, and it must still cost the attacker.
        assert ENCODING_LEGIBILITY["rot13"] < 1.0


class TestObfuscationTransforms:
    def test_every_transform_changes_its_input(self):
        for name, fn in OBFUSCATIONS.items():
            assert fn("ignore all previous") != "ignore all previous", name

    def test_every_transform_is_deterministic(self):
        for name, fn in OBFUSCATIONS.items():
            assert fn("payload text") == fn("payload text"), name

    def test_every_transform_returns_a_string(self):
        for name, fn in OBFUSCATIONS.items():
            assert isinstance(fn("x"), str), name

    def test_obfuscated_family_covers_every_transform(self):
        kinds = {a.notes.replace("obfuscation: ", "")
                 for a in CORPUS.attacks if a.family is Family.OBFUSCATED}
        assert kinds == set(OBFUSCATIONS)


class TestBenignCorpus:
    def test_some_benign_documents_are_traps(self):
        assert sum(1 for b in CORPUS.benign if b.trap) >= 8

    def test_most_benign_documents_record_why_they_are_present(self):
        # `trap` is a note on what makes the document worth including -- a
        # quoted thread, a link to an unknown host, a non-Latin script. It
        # is documentation of intent, not a label meaning "adversarial".
        assert sum(1 for b in CORPUS.benign if b.trap) >= len(CORPUS.benign) / 2

    def test_some_benign_documents_are_plain(self):
        # Ordinary mail with nothing interesting in it has to be present or
        # the false-positive rate is measured only on hard cases.
        assert any(not b.trap for b in CORPUS.benign)

    def test_no_benign_document_contains_an_attack_marker(self):
        markers = {a.marker for a in CORPUS.attacks}
        for doc in CORPUS.benign:
            assert not any(m in doc.payload for m in markers), doc.id

    def test_benign_documents_are_non_empty(self):
        assert all(doc.payload.strip() for doc in CORPUS.benign)


class TestChannels:
    def test_channels_are_trust_levels(self):
        assert all(isinstance(a.channel, Trust) for a in CORPUS.attacks)

    def test_indirect_attacks_are_untrusted(self):
        for a in CORPUS.attacks:
            if a.family is Family.INDIRECT:
                assert a.channel is Trust.UNTRUSTED

    def test_tool_chain_attacks_arrive_on_the_tool_channel(self):
        for a in CORPUS.attacks:
            if a.family is Family.TOOL_CHAIN:
                assert a.channel is Trust.TOOL

    def test_some_attacks_arrive_from_the_user(self):
        # Needed to keep the broker honest: a user asking for their own tool
        # is not an injection, and a defence that refuses it is broken.
        assert any(a.channel is Trust.USER for a in CORPUS.attacks)
