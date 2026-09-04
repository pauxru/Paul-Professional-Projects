"""PICTURE clause parsing.

A PIC clause is a tiny language and every COBOL shop uses a different dialect of
it. This handles the subset that appears in fixed-width interchange records,
which is the subset that matters for decommissioning a batch job:

    PIC X(20)                 20 bytes of text
    PIC 9(7)                  7 digits, unsigned display
    PIC S9(7)                 7 digits, signed display (trailing overpunch)
    PIC S9(7)V99              9 digits, 2 implied decimal places
    PIC S9(7)V99 COMP-3       the same value packed into 5 bytes
    PIC 9(4) COMP             4 digits in a 2-byte big-endian integer
    PIC S9(4)V9(2) COMP-3     mixed shorthand and repeat counts

The ``V`` is the one that catches people. It occupies no storage. It says
"there is a decimal point here" and nothing in the file records that fact, so a
field read without its copybook is off by a factor of 100 and looks entirely
plausible.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from enum import Enum


class Usage(Enum):
    DISPLAY = "DISPLAY"
    COMP_3 = "COMP-3"
    COMP = "COMP"

    @classmethod
    def parse(cls, text: str) -> "Usage":
        t = text.upper().replace("USAGE", "").replace("IS", "").strip()
        if t in ("COMP-3", "COMPUTATIONAL-3", "PACKED-DECIMAL"):
            return cls.COMP_3
        if t in ("COMP", "COMP-4", "COMPUTATIONAL", "COMPUTATIONAL-4", "BINARY"):
            return cls.COMP
        if t in ("DISPLAY", ""):
            return cls.DISPLAY
        raise ValueError(f"unsupported USAGE {text!r}")


class Category(Enum):
    ALPHANUMERIC = "X"
    NUMERIC = "9"


@dataclass(frozen=True)
class Picture:
    category: Category
    digits: int
    """Total digit positions, including those after the implied decimal point."""
    scale: int
    """Digits after the implied decimal point (the ``V``)."""
    signed: bool
    usage: Usage
    raw: str

    @property
    def integer_digits(self) -> int:
        return self.digits - self.scale

    def storage_bytes(self) -> int:
        from .numeric import binary_length, packed_length

        if self.category is Category.ALPHANUMERIC:
            return self.digits
        if self.usage is Usage.COMP_3:
            return packed_length(self.digits)
        if self.usage is Usage.COMP:
            return binary_length(self.digits)
        return self.digits

    def __str__(self) -> str:
        return self.raw


_TOKEN = re.compile(r"([X9SVP])(?:\((\d+)\))?", re.IGNORECASE)


def parse_picture(pic: str, usage: Usage = Usage.DISPLAY) -> Picture:
    text = pic.strip().upper()
    if text.startswith("PIC"):
        text = text[3:].lstrip()
        if text.startswith("TURE"):
            text = text[4:].lstrip()
        if text.startswith("IS"):
            text = text[2:].lstrip()

    pos = 0
    digits = 0
    scale = 0
    signed = False
    seen_v = False
    category: Category | None = None

    while pos < len(text):
        m = _TOKEN.match(text, pos)
        if not m:
            raise ValueError(f"cannot parse PICTURE {pic!r} at offset {pos}: {text[pos:]!r}")
        sym, count = m.group(1), m.group(2)
        n = int(count) if count else 1
        pos = m.end()
        # A bare repeated symbol, e.g. "XXX" or "999".
        while pos < len(text) and text[pos] == sym:
            n += 1
            pos += 1

        if sym == "S":
            if signed:
                raise ValueError(f"PICTURE {pic!r} has more than one S")
            if digits or category:
                raise ValueError(f"PICTURE {pic!r}: S must come first")
            signed = True
        elif sym == "V":
            if seen_v:
                raise ValueError(f"PICTURE {pic!r} has more than one V")
            seen_v = True
        elif sym == "P":
            # A scaling position: contributes to scale but occupies no storage
            # and holds no digit. Rare, and wrong often enough to reject.
            raise ValueError(
                f"PICTURE {pic!r} uses P (scaling position), which is not supported; "
                "see docs/known-limitations.md"
            )
        elif sym == "X":
            if category is Category.NUMERIC:
                raise ValueError(f"PICTURE {pic!r} mixes X and 9")
            category = Category.ALPHANUMERIC
            digits += n
        elif sym == "9":
            if category is Category.ALPHANUMERIC:
                raise ValueError(f"PICTURE {pic!r} mixes X and 9")
            category = Category.NUMERIC
            digits += n
            if seen_v:
                scale += n

    if category is None:
        raise ValueError(f"PICTURE {pic!r} declares no storage")
    if category is Category.ALPHANUMERIC and (signed or seen_v):
        raise ValueError(f"PICTURE {pic!r}: S and V are not valid on an X field")
    if category is Category.NUMERIC and digits == 0:
        raise ValueError(f"PICTURE {pic!r} has no digit positions")
    if category is Category.ALPHANUMERIC and usage is not Usage.DISPLAY:
        raise ValueError(f"PICTURE {pic!r} cannot have USAGE {usage.value}")

    return Picture(category, digits, scale, signed, usage, pic.strip())
