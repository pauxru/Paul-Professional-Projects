"""Corpus-level tests: round-tripping, differing, and the arithmetic experiment.

These are the tests that would have caught the failure this project exists to
prevent. The unit tests above check that each encoding is handled; these check
that a whole file survives a full read-transform-write cycle byte for byte, over
a corpus that deliberately contains the awkward cases.
"""

import unittest
from decimal import Decimal
from pathlib import Path

from cobol.copybook import parse_copybook, record_length
from cobol.differ import compare
from cobol.generate import Corruption, generate_file, split_records
from cobol.pipeline import Rounding, bill, compare_modes, run_batch
from cobol.record import decode_record, encode_record

COPYBOOK = Path(__file__).resolve().parents[1] / "copybooks" / "custmast.cpy"


def load_root():
    return parse_copybook(COPYBOOK.read_text())


class TestCorpusRoundTrip(unittest.TestCase):
    def setUp(self):
        self.root = load_root()

    def test_generated_length_matches_the_layout_engine(self):
        # The generator computes lengths by laying out bytes; record_length
        # computes them from the copybook. They are separate code paths, so
        # agreement is evidence rather than tautology.
        data, odos = generate_file(self.root, 200, seed=3)
        pos = 0
        for odo in odos:
            expected = record_length(self.root, odo)
            rec = decode_record(self.root, data[pos:])
            self.assertEqual(rec.length, expected)
            pos += rec.length
        self.assertEqual(pos, len(data))

    def test_byte_exact_round_trip_over_a_dirty_corpus(self):
        data, _ = generate_file(self.root, 500, seed=11)
        rebuilt = bytearray()
        for raw in split_records(self.root, data):
            rec = decode_record(self.root, raw)
            rebuilt += encode_record(self.root, rec)
        self.assertEqual(bytes(rebuilt), data)

    def test_canonical_reemission_changes_the_file(self):
        data, _ = generate_file(self.root, 500, seed=11)
        rebuilt = bytearray()
        for raw in split_records(self.root, data):
            rec = decode_record(self.root, raw)
            rebuilt += encode_record(self.root, rec, canonical_signs=True)
        self.assertNotEqual(bytes(rebuilt), data)
        rep = compare(self.root, data, bytes(rebuilt), max_diffs=100000)
        self.assertFalse(rep.byte_equivalent)
        self.assertGreater(len(rep.representation_diffs), 0)

        # Every field the copybook calls numeric still holds the same number.
        numeric_diffs = [
            d for d in rep.value_diffs if not d.path.endswith("CM-KEY-ALT")
        ]
        self.assertEqual(numeric_diffs, [])

    def test_representation_change_becomes_a_value_change_through_a_redefines(self):
        # CM-KEY-ALT reads CM-BRANCH and CM-ACCOUNT as text. Rewriting a
        # blank-padded numeric field in canonical form turns EBCDIC spaces into
        # EBCDIC zeroes: the number is unchanged, the *text* is not.
        #
        # The archive job uses CM-KEY-ALT. It does not have our copybook, it has
        # its own, and it will start seeing keys it has never seen before. This
        # is precisely what a field-by-field comparison fails to catch.
        data, _ = generate_file(self.root, 500, seed=11)
        rebuilt = b"".join(
            encode_record(self.root, decode_record(self.root, r), canonical_signs=True)
            for r in split_records(self.root, data)
        )
        rep = compare(self.root, data, rebuilt, max_diffs=100000)
        overlay = [d for d in rep.value_diffs if d.path.endswith("CM-KEY-ALT")]
        self.assertGreater(len(overlay), 0)
        left, right = overlay[0].left, overlay[0].right
        self.assertIn(" ", left)
        self.assertNotIn(" ", right)

    def test_clean_corpus_survives_canonical_reemission(self):
        # With no odd signs and no blank padding, the naive path is correct.
        # This is exactly why a synthetic test file proves nothing.
        data, _ = generate_file(self.root, 200, seed=5, corruption=Corruption.none())
        rebuilt = bytearray()
        for raw in split_records(self.root, data):
            rec = decode_record(self.root, raw)
            rebuilt += encode_record(self.root, rec, canonical_signs=True)
        self.assertEqual(bytes(rebuilt), data)

    def test_generation_is_deterministic(self):
        a, _ = generate_file(self.root, 50, seed=99)
        b, _ = generate_file(self.root, 50, seed=99)
        c, _ = generate_file(self.root, 50, seed=100)
        self.assertEqual(a, b)
        self.assertNotEqual(a, c)

    def test_variable_records_really_do_vary(self):
        data, odos = generate_file(self.root, 100, seed=13)
        lengths = {len(r) for r in split_records(self.root, data)}
        self.assertGreater(len(lengths), 1)


class TestDiffer(unittest.TestCase):
    def setUp(self):
        self.root = load_root()
        self.data, _ = generate_file(self.root, 100, seed=21)

    def test_identical_files(self):
        rep = compare(self.root, self.data, self.data)
        self.assertTrue(rep.byte_equivalent)
        self.assertTrue(rep.field_equivalent)
        self.assertEqual(rep.records, 100)

    def test_value_change_is_reported_as_a_value_change(self):
        recs = split_records(self.root, self.data)
        rec = decode_record(self.root, recs[0])
        rec.get_cell("CM-NAME").value = "CHANGED" .ljust(24)
        mutated = encode_record(self.root, rec) + b"".join(recs[1:])
        rep = compare(self.root, self.data, mutated)
        self.assertFalse(rep.field_equivalent)
        self.assertEqual(len(rep.value_diffs), 1)
        self.assertIn("CM-NAME", rep.value_diffs[0].path)

    def test_length_mismatch_stops_the_comparison(self):
        rep = compare(self.root, self.data, self.data[:-5])
        self.assertIsNotNone(rep.length_mismatch)
        self.assertIsNotNone(rep.decode_error)
        self.assertFalse(rep.field_equivalent)
        self.assertIn("decode stopped", rep.summary())

    def test_by_field_groups_array_elements(self):
        rebuilt = b"".join(
            encode_record(self.root, decode_record(self.root, r), canonical_signs=True)
            for r in split_records(self.root, self.data)
        )
        rep = compare(self.root, self.data, rebuilt, max_diffs=100000)
        keys = rep.by_field()
        self.assertTrue(all("[" not in k for k in keys))


class TestArithmetic(unittest.TestCase):
    def test_mainframe_truncates_toward_zero(self):
        # 100.00 + 2.50 = 102.50; VAT 17.5% = 17.9375 -> stored as 17.93.
        self.assertEqual(bill(Decimal("100.00"), Decimal("0.00"), Rounding.MAINFRAME),
                         Decimal("120.43"))
        # Half-up would store 17.94 and carry the extra penny through.
        self.assertEqual(bill(Decimal("100.00"), Decimal("0.00"), Rounding.HALF_UP),
                         Decimal("120.44"))

    def test_floor_agrees_on_positives_and_differs_on_negatives(self):
        pos = Decimal("100.00")
        self.assertEqual(
            bill(pos, Decimal("0.00"), Rounding.FLOOR),
            bill(pos, Decimal("0.00"), Rounding.MAINFRAME),
        )
        neg = Decimal("-100.00")
        self.assertNotEqual(
            bill(neg, Decimal("0.00"), Rounding.FLOOR),
            bill(neg, Decimal("0.00"), Rounding.MAINFRAME),
        )

    def test_experiment_shape(self):
        root = load_root()
        data, _ = generate_file(root, 400, seed=31)
        result = compare_modes(root, data)
        floor = result[Rounding.FLOOR]
        half = result[Rounding.HALF_UP]
        # The floor bug fires only on negative balances. That is what makes it
        # survive a test extract taken from a quiet week.
        self.assertEqual(floor["differing_positive"], 0)
        self.assertGreater(floor["differing_negative"], 0)
        # Half-up fires everywhere, so it is caught on day one.
        self.assertGreater(half["rate"], floor["rate"])

    def test_batch_preserves_untouched_fields_byte_for_byte(self):
        root = load_root()
        data, _ = generate_file(root, 200, seed=41)
        out, stats = run_batch(root, data, Rounding.MAINFRAME)
        self.assertEqual(stats.records, 200)
        rep = compare(root, data, out, max_diffs=100000)
        changed = {d.path.split(".")[-1] for d in rep.diffs}
        self.assertEqual(changed, {"CM-BALANCE"})

    def test_batch_with_canonical_signs_damages_unrelated_fields(self):
        root = load_root()
        data, _ = generate_file(root, 200, seed=41)
        out, _ = run_batch(root, data, Rounding.MAINFRAME, canonical_signs=True)
        rep = compare(root, data, out, max_diffs=100000)
        changed = {d.path.split(".")[-1] for d in rep.diffs}
        self.assertIn("CM-BALANCE", changed)
        self.assertGreater(len(changed), 1)
        # Everything except the overlay changed representation only, so a
        # field-by-field comparison of the numeric fields reports success.
        extra = [
            d
            for d in rep.diffs
            if not d.path.endswith("CM-BALANCE") and not d.path.endswith("CM-KEY-ALT")
        ]
        self.assertTrue(extra)
        self.assertTrue(all(not d.value_changed for d in extra))


if __name__ == "__main__":
    unittest.main()
