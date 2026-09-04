"""Tests for the copybook, numeric and record layers.

The tests that matter here are the ones with hand-written hex in them. A test
that encodes a value and decodes it again proves the two functions agree with
each other; it does not prove either agrees with a mainframe. Where the
expected bytes are known from the format definition, they are written out.
"""

import unittest
from decimal import Decimal

from cobol import numeric
from cobol.copybook import CopybookError, parse_copybook, record_length
from cobol.ebcdic import code_pages_differ_on, from_text, to_text
from cobol.picture import Category, Usage, parse_picture
from cobol.record import RecordError, decode_record, encode_record


class TestPicture(unittest.TestCase):
    def test_simple_text(self):
        p = parse_picture("X(20)")
        self.assertIs(p.category, Category.ALPHANUMERIC)
        self.assertEqual(p.digits, 20)
        self.assertEqual(p.storage_bytes(), 20)

    def test_repeated_symbols_without_parens(self):
        self.assertEqual(parse_picture("XXX").digits, 3)
        self.assertEqual(parse_picture("999").digits, 3)

    def test_implied_decimal_takes_no_storage(self):
        p = parse_picture("S9(7)V99")
        self.assertEqual(p.digits, 9)
        self.assertEqual(p.scale, 2)
        self.assertEqual(p.integer_digits, 7)
        self.assertTrue(p.signed)
        # Nine digit positions, nine bytes. The V is free.
        self.assertEqual(p.storage_bytes(), 9)

    def test_comp3_storage(self):
        self.assertEqual(parse_picture("S9(7)V99", Usage.COMP_3).storage_bytes(), 5)
        self.assertEqual(parse_picture("S9(5)V99", Usage.COMP_3).storage_bytes(), 4)

    def test_comp_storage_bands(self):
        self.assertEqual(parse_picture("9(4)", Usage.COMP).storage_bytes(), 2)
        self.assertEqual(parse_picture("9(5)", Usage.COMP).storage_bytes(), 4)
        self.assertEqual(parse_picture("9(10)", Usage.COMP).storage_bytes(), 8)

    def test_rejects_mixed_categories(self):
        with self.assertRaises(ValueError):
            parse_picture("X(3)9(2)")

    def test_rejects_scaling_position(self):
        with self.assertRaisesRegex(ValueError, "scaling position"):
            parse_picture("9(3)PPP")

    def test_rejects_sign_on_text(self):
        with self.assertRaises(ValueError):
            parse_picture("SX(4)")

    def test_accepts_pic_prefix_and_is(self):
        self.assertEqual(parse_picture("PIC IS 9(3)").digits, 3)
        self.assertEqual(parse_picture("PICTURE X(3)").digits, 3)


class TestPacked(unittest.TestCase):
    def test_length_formula(self):
        # digits // 2 + 1: an odd digit count exactly fills its last byte.
        self.assertEqual(numeric.packed_length(1), 1)
        self.assertEqual(numeric.packed_length(5), 3)
        self.assertEqual(numeric.packed_length(9), 5)
        self.assertEqual(numeric.packed_length(10), 6)

    def test_known_encoding(self):
        # 123.45 as S9(3)V99 COMP-3 is three bytes: 12 34 5C.
        self.assertEqual(
            numeric.encode_packed(Decimal("123.45"), 5, 2), bytes.fromhex("12345c")
        )
        self.assertEqual(
            numeric.encode_packed(Decimal("-123.45"), 5, 2), bytes.fromhex("12345d")
        )

    def test_even_digits_pad_a_leading_nibble(self):
        self.assertEqual(numeric.encode_packed(Decimal("1234"), 4), bytes.fromhex("01234c"))

    def test_unsigned_uses_f(self):
        self.assertEqual(numeric.encode_packed(Decimal("7"), 1, 0, signed=False), b"\x7f")

    def test_alternate_sign_nibbles_are_valid(self):
        for nibble, negative in ((0xA, False), (0xB, True), (0xE, False)):
            p = numeric.decode_packed(bytes([0x12, 0x30 | nibble]))
            self.assertEqual(abs(p.value), Decimal(123))
            self.assertEqual(p.is_negative, negative)
            self.assertFalse(p.is_canonical)

    def test_negative_zero_is_a_distinct_encoding(self):
        neg = numeric.decode_packed(b"\x00\x0d")
        pos = numeric.decode_packed(b"\x00\x0c")
        self.assertEqual(neg.value, pos.value)
        self.assertNotEqual(neg.sign_nibble, pos.sign_nibble)
        # Equal as numbers, different as bytes. Anything that stores only the
        # value cannot tell these apart, and re-emits the wrong one.
        self.assertEqual(
            numeric.encode_packed(neg.value, 3, 0, True, neg.sign_nibble), b"\x00\x0d"
        )
        self.assertEqual(numeric.encode_packed(neg.value, 3, 0, True), b"\x00\x0c")

    def test_rejects_non_digit_nibble(self):
        with self.assertRaises(numeric.DecodeError):
            numeric.decode_packed(b"\x1a\x2c")

    def test_rejects_bad_sign(self):
        with self.assertRaises(numeric.DecodeError):
            numeric.decode_packed(b"\x12\x31")

    def test_overflow_is_an_error_not_a_truncation(self):
        with self.assertRaises(ValueError):
            numeric.encode_packed(Decimal("1234"), 3)


class TestZoned(unittest.TestCase):
    def test_known_encoding(self):
        self.assertEqual(numeric.encode_zoned(Decimal("123"), 3, 0, False), b"\xf1\xf2\xf3")
        self.assertEqual(numeric.encode_zoned(Decimal("-123"), 3, 0, True), b"\xf1\xf2\xd3")
        self.assertEqual(numeric.encode_zoned(Decimal("123"), 3, 0, True), b"\xf1\xf2\xc3")

    def test_f_zone_positive_is_common_but_not_canonical(self):
        z = numeric.decode_zoned(b"\xf1\xf2\xf3", 0, True)
        self.assertEqual(z.value, Decimal(123))
        self.assertEqual(z.sign_zone, 0xF)
        # Re-encoding from the value alone yields 0xC3, not 0xF3.
        self.assertNotEqual(numeric.encode_zoned(z.value, 3, 0, True), b"\xf1\xf2\xf3")

    def test_spaces_decode_as_zero_and_are_remembered(self):
        z = numeric.decode_zoned(b"\x40\x40\xf5", 0, True)
        self.assertEqual(z.value, Decimal(5))
        self.assertEqual(z.space_positions, (0, 1))
        self.assertTrue(z.padded_with_spaces)
        self.assertEqual(
            numeric.encode_zoned(z.value, 3, 0, True, z.sign_zone, z.space_positions),
            b"\x40\x40\xf5",
        )

    def test_all_spaces_round_trip(self):
        raw = b"\x40" * 4
        z = numeric.decode_zoned(raw, 0, True)
        self.assertEqual(z.value, Decimal(0))
        self.assertIsNone(z.sign_zone)
        self.assertEqual(
            numeric.encode_zoned(z.value, 4, 0, True, z.sign_zone, z.space_positions), raw
        )

    def test_overpunch_round_trip(self):
        self.assertEqual(numeric.overpunch_char(4, negative=True), "M")
        self.assertEqual(numeric.overpunch_char(4, negative=False), "D")
        self.assertEqual(numeric.overpunch_char(0, negative=True), "}")
        self.assertEqual(numeric.parse_overpunch("M"), (4, True))
        self.assertEqual(numeric.parse_overpunch("{"), (0, False))
        self.assertEqual(numeric.parse_overpunch("7"), (7, False))

    def test_rejects_non_digit(self):
        with self.assertRaises(numeric.DecodeError):
            numeric.decode_zoned(b"\xf1\xfa", 0, False)


class TestBinary(unittest.TestCase):
    def test_big_endian_signed(self):
        self.assertEqual(numeric.encode_binary(Decimal(-2), 2), b"\xff\xfe")
        self.assertEqual(numeric.decode_binary(b"\xff\xfe", 0, True), Decimal(-2))
        self.assertEqual(numeric.decode_binary(b"\xff\xfe", 0, False), Decimal(65534))

    def test_scale_is_implied_not_stored(self):
        self.assertEqual(numeric.encode_binary(Decimal("12.34"), 2, 2), b"\x04\xd2")
        self.assertEqual(numeric.decode_binary(b"\x04\xd2", 2, True), Decimal("12.34"))


class TestEbcdic(unittest.TestCase):
    def test_round_trip(self):
        for s in ("ACME LTD", "abc 123", "A[B]C!"):
            self.assertEqual(to_text(from_text(s, "cp037"), "cp037"), s)

    def test_known_bytes(self):
        self.assertEqual(from_text("A", "cp037"), b"\xc1")
        self.assertEqual(from_text(" ", "cp037"), b"\x40")
        self.assertEqual(from_text("0", "cp037"), b"\xf0")

    def test_code_pages_disagree_silently(self):
        differ = code_pages_differ_on("cp037", "cp500")
        rendered = "".join(differ.values())
        self.assertIn("[", rendered)
        self.assertIn("]", rendered)
        # Both decode without error, to different text. Nothing raises.
        raw = from_text("A[B", "cp037")
        self.assertEqual(to_text(raw, "cp037"), "A[B")
        self.assertNotEqual(to_text(raw, "cp500"), "A[B")

    def test_letters_and_digits_agree(self):
        for ch in "ABCXYZabcxyz0123456789 ":
            self.assertEqual(
                from_text(ch, "cp037"),
                from_text(ch, "cp500"),
                f"{ch!r} should encode identically in both code pages",
            )


SIMPLE = """
       01  REC.
           05  R-ID    PIC 9(4).
           05  R-NAME  PIC X(6).
           05  R-AMT   PIC S9(5)V99 COMP-3.
"""


class TestCopybook(unittest.TestCase):
    def test_offsets_and_length(self):
        root = parse_copybook(SIMPLE)
        self.assertEqual(record_length(root), 4 + 6 + 4)
        self.assertEqual(root.find("R-AMT").offset, 10)

    def test_comment_lines_are_ignored(self):
        with_comment = SIMPLE.replace(
            "           05  R-NAME  PIC X(6).",
            "      * R-NAME WAS WIDENED IN 2001\n           05  R-NAME  PIC X(6).",
        )
        self.assertEqual(record_length(parse_copybook(with_comment)), 14)

    def test_sequence_numbered_comment_in_column_seven(self):
        text = (
            "000100 01  REC.\n"
            "000200*    05  R-DEAD  PIC X(9).\n"
            "000300     05  R-ID    PIC 9(4).\n"
        )
        root = parse_copybook(text)
        # If column 7 were ignored, R-DEAD would be live and R-ID would sit at
        # offset 9 instead of 0 — every downstream offset silently wrong.
        self.assertEqual(record_length(root), 4)
        self.assertEqual(root.find("R-ID").offset, 0)

    def test_redefines_overlays_and_adds_no_length(self):
        text = """
       01  REC.
           05  R-KEY.
               10  R-A  PIC 9(3).
               10  R-B  PIC 9(3).
           05  R-KEY-TEXT REDEFINES R-KEY PIC X(6).
           05  R-TAIL PIC X(2).
        """
        root = parse_copybook(text)
        self.assertEqual(record_length(root), 8)
        self.assertEqual(root.find("R-KEY-TEXT").offset, 0)
        self.assertEqual(root.find("R-TAIL").offset, 6)

    def test_occurs_fixed(self):
        text = """
       01  REC.
           05  R-N PIC 9(2).
           05  R-T OCCURS 3 TIMES PIC X(4).
        """
        self.assertEqual(record_length(parse_copybook(text)), 2 + 12)

    def test_odo_length_depends_on_data(self):
        text = """
       01  REC.
           05  R-N PIC 9(2).
           05  R-T OCCURS 0 TO 4 TIMES DEPENDING ON R-N PIC X(5).
        """
        root = parse_copybook(text)
        self.assertEqual(record_length(root), 2)
        self.assertEqual(record_length(root, {"R-N": 3}), 17)
        with self.assertRaises(CopybookError):
            record_length(root, {"R-N": 9})

    def test_odo_control_must_precede_the_array(self):
        text = """
       01  REC.
           05  R-T OCCURS 0 TO 4 TIMES DEPENDING ON R-N PIC X(5).
           05  R-N PIC 9(2).
        """
        with self.assertRaisesRegex(CopybookError, "unknowable"):
            parse_copybook(text)

    def test_duplicate_names_rejected(self):
        text = """
       01  REC.
           05  R-A PIC X(1).
           05  R-A PIC X(1).
        """
        with self.assertRaisesRegex(CopybookError, "duplicate"):
            parse_copybook(text)

    def test_filler_may_repeat(self):
        text = """
       01  REC.
           05  FILLER PIC X(1).
           05  R-A    PIC X(1).
           05  FILLER PIC X(1).
        """
        self.assertEqual(record_length(parse_copybook(text)), 3)

    def test_odo_on_unknown_field_rejected(self):
        text = """
       01  REC.
           05  R-N PIC 9(2).
           05  R-T OCCURS 0 TO 4 TIMES DEPENDING ON R-NOPE PIC X(5).
        """
        with self.assertRaises(CopybookError):
            parse_copybook(text)

    def test_odo_without_range_rejected(self):
        text = """
       01  REC.
           05  R-N PIC 9(2).
           05  R-T OCCURS 4 TIMES DEPENDING ON R-N PIC X(5).
        """
        with self.assertRaisesRegex(CopybookError, "TO range"):
            parse_copybook(text)


class TestRecord(unittest.TestCase):
    def setUp(self):
        self.root = parse_copybook(SIMPLE)

    def _raw(self, amt_bytes):
        return from_text("0042", "cp037") + from_text("ACME  ", "cp037") + amt_bytes

    def test_decode(self):
        rec = decode_record(self.root, self._raw(bytes.fromhex("0123456c")))
        self.assertEqual(rec["R-ID"], Decimal(42))
        self.assertEqual(rec["R-NAME"], "ACME  ")
        self.assertEqual(rec["R-AMT"], Decimal("1234.56"))
        self.assertEqual(rec.length, 14)

    def test_round_trip_preserves_alternate_sign(self):
        raw = self._raw(bytes.fromhex("0123456a"))
        rec = decode_record(self.root, raw)
        self.assertFalse(rec.get_cell("R-AMT").canonical)
        self.assertEqual(encode_record(self.root, rec), raw)
        self.assertNotEqual(encode_record(self.root, rec, canonical_signs=True), raw)

    def test_short_record_is_an_error(self):
        with self.assertRaisesRegex(RecordError, "record ended"):
            decode_record(self.root, self._raw(bytes.fromhex("0123")))

    def test_non_canonical_listing(self):
        rec = decode_record(self.root, self._raw(bytes.fromhex("0123456e")))
        self.assertEqual([c.path for c in rec.non_canonical()], ["REC.R-AMT"])


if __name__ == "__main__":
    unittest.main()
