"""Reading, verifying and decommissioning a fixed-width mainframe batch file.

Public surface:

``parse_copybook`` / ``describe`` / ``record_length``
    Turn a COBOL copybook into a layout.

``decode_record`` / ``encode_record``
    Turn bytes into values and back, preserving how the values were written.

``generate_file``
    Deterministic synthetic corpora containing the awkward-but-legal cases that
    real files contain.

``compare``
    Byte-level and field-level equivalence between two versions of a file.

``run_batch`` / ``compare_modes``
    The business logic being decommissioned, and the arithmetic experiment.
"""

from .copybook import CopybookError, Field, describe, parse_copybook, record_length
from .differ import DiffReport, FieldDiff, compare
from .ebcdic import code_pages_differ_on, from_text, to_text
from .generate import Corruption, Rng, generate_file, generate_record, split_records
from .numeric import (
    DecodeError,
    Packed,
    Zoned,
    binary_length,
    decode_binary,
    decode_packed,
    decode_zoned,
    encode_binary,
    encode_packed,
    encode_zoned,
    overpunch_char,
    packed_length,
    parse_overpunch,
)
from .picture import Category, Picture, Usage, parse_picture
from .pipeline import Billed, Rounding, bill, bill_detail, compare_modes, run_batch
from .record import Cell, Record, RecordError, decode_record, encode_record

__all__ = [
    "Billed",
    "Category",
    "Cell",
    "CopybookError",
    "Corruption",
    "DecodeError",
    "DiffReport",
    "Field",
    "FieldDiff",
    "Packed",
    "Picture",
    "Record",
    "RecordError",
    "Rng",
    "Rounding",
    "Usage",
    "Zoned",
    "bill",
    "bill_detail",
    "binary_length",
    "code_pages_differ_on",
    "compare",
    "compare_modes",
    "decode_binary",
    "decode_packed",
    "decode_record",
    "decode_zoned",
    "describe",
    "encode_binary",
    "encode_packed",
    "encode_record",
    "encode_zoned",
    "from_text",
    "generate_file",
    "generate_record",
    "overpunch_char",
    "packed_length",
    "parse_copybook",
    "parse_overpunch",
    "parse_picture",
    "record_length",
    "run_batch",
    "split_records",
    "to_text",
]
