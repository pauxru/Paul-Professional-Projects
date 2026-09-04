"""Deterministic generation of realistic legacy files.

Synthetic data is only useful here if it contains the things that break
migrations. A generator that emits well-formed canonical records proves the
decoder handles the easy case, which nobody doubted.

So this deliberately produces, at controlled rates:

- non-preferred COMP-3 sign nibbles (0xA, 0xB, 0xE) — legal, and emitted by some
  compilers and by hand-written assembler
- unsigned COMP-3 holding 0xF, which is a different byte from signed positive
- zoned fields blank-padded with EBCDIC 0x40, from uninitialised working storage
- negative zero, which reads as ``0`` and is not ``0x0C``
- variable-length records via OCCURS DEPENDING ON

The generator builds bytes directly rather than calling
:func:`cobol.record.encode_record`. That is deliberate: if it used the encoder, a
round-trip test would only prove the encoder is self-consistent with itself.
Here the generator and the decoder are independent implementations of the same
layout, so agreement between them is evidence.
"""

from __future__ import annotations

from decimal import Decimal
from typing import Dict, List, Tuple

from . import numeric
from .copybook import Field, _norm, record_length
from .ebcdic import EBCDIC_SPACE, from_text
from .picture import Category, Usage


class Rng:
    """SplitMix64, so a generated corpus is a pure function of its seed."""

    def __init__(self, seed: int) -> None:
        self.s = seed & 0xFFFF_FFFF_FFFF_FFFF

    def next(self) -> int:
        self.s = (self.s + 0x9E3779B97F4A7C15) & 0xFFFF_FFFF_FFFF_FFFF
        z = self.s
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & 0xFFFF_FFFF_FFFF_FFFF
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & 0xFFFF_FFFF_FFFF_FFFF
        return z ^ (z >> 31)

    def below(self, n: int) -> int:
        return self.next() % n

    def between(self, lo: int, hi: int) -> int:
        return lo + self.below(hi - lo + 1)

    def chance(self, p: float) -> bool:
        return self.below(1_000_000) < int(p * 1_000_000)

    def choice(self, xs):
        return xs[self.below(len(xs))]


_NAMES = [
    "ACME HOLDINGS LTD",
    "BRIDGEWATER SUPPLY",
    "CALDER AND SONS",
    "DUNMORE LOGISTICS",
    "EASTGATE TRADING",
    "FERNBANK MUTUAL",
    "GRANTHAM PARTNERS",
    "HOLLOWAY FREIGHT",
]


class Corruption:
    """Rates at which the generator emits the awkward-but-legal cases."""

    def __init__(
        self,
        odd_signs: float = 0.12,
        blank_fields: float = 0.05,
        negative_zero: float = 0.04,
    ) -> None:
        self.odd_signs = odd_signs
        self.blank_fields = blank_fields
        self.negative_zero = negative_zero

    @staticmethod
    def none() -> "Corruption":
        return Corruption(0.0, 0.0, 0.0)


def _text(rng: Rng, n: int) -> str:
    return rng.choice(_NAMES)[:n].ljust(n)


def _elementary(f: Field, rng: Rng, c: Corruption, code_page: str) -> bytes:
    pic = f.picture
    assert pic is not None

    if pic.category is Category.ALPHANUMERIC:
        return from_text(_text(rng, pic.digits), code_page)

    limit = 10 ** min(pic.integer_digits, 7) - 1
    whole = rng.between(0, max(1, limit))
    frac = rng.below(10**pic.scale) if pic.scale else 0
    value = Decimal(whole)
    if pic.scale:
        value += Decimal(frac).scaleb(-pic.scale)
    if pic.signed and rng.chance(0.35):
        value = -value
    if pic.signed and rng.chance(c.negative_zero):
        value = Decimal(0)
        negative = True
    else:
        negative = value < 0
    if not pic.signed:
        value = abs(value)

    if pic.usage is Usage.COMP_3:
        if not pic.signed:
            sign = numeric.PREFERRED_UNSIGNED
        elif rng.chance(c.odd_signs):
            sign = 0xB if negative else rng.choice([0xA, 0xE])
        else:
            sign = numeric.PREFERRED_NEGATIVE if negative else numeric.PREFERRED_POSITIVE
        return numeric.encode_packed(value, pic.digits, pic.scale, pic.signed, sign)

    if pic.usage is Usage.COMP:
        return numeric.encode_binary(value, pic.storage_bytes(), pic.scale, pic.signed)

    if rng.chance(c.blank_fields):
        return bytes([EBCDIC_SPACE]) * pic.storage_bytes()
    zone = None
    if pic.signed and rng.chance(c.odd_signs):
        # 0xF as a "positive" zone is extremely common in files written by
        # programs that MOVEd an unsigned value into a signed field.
        zone = 0xD if negative else 0xF
    return numeric.encode_zoned(value, pic.digits, pic.scale, pic.signed, zone)


def generate_record(
    root: Field,
    rng: Rng,
    corruption: Corruption | None = None,
    code_page: str = "cp037",
) -> Tuple[bytes, Dict[str, int]]:
    c = corruption or Corruption()
    odo: Dict[str, int] = {}
    for f in root.walk():
        if f.depending_on:
            odo[_norm(f.depending_on)] = rng.between(f.occurs_min, f.occurs_max)

    chunks: List[bytes] = []

    def emit(f: Field) -> None:
        times = f.occurs_min
        if f.depending_on:
            times = odo[_norm(f.depending_on)]
        for _ in range(times):
            if f.picture is not None:
                if _norm(f.name) in odo:
                    # This field controls a variable array; it must hold the
                    # count we actually emit, or the record is unreadable.
                    n = Decimal(odo[_norm(f.name)])
                    if f.picture.usage is Usage.COMP_3:
                        chunks.append(
                            numeric.encode_packed(
                                n, f.picture.digits, f.picture.scale, f.picture.signed
                            )
                        )
                    elif f.picture.usage is Usage.COMP:
                        chunks.append(
                            numeric.encode_binary(
                                n,
                                f.picture.storage_bytes(),
                                f.picture.scale,
                                f.picture.signed,
                            )
                        )
                    else:
                        chunks.append(
                            numeric.encode_zoned(
                                n, f.picture.digits, f.picture.scale, f.picture.signed
                            )
                        )
                else:
                    chunks.append(_elementary(f, rng, c, code_page))
                continue
            for child in f.children:
                if child.redefines is None:
                    emit(child)

    for child in root.children:
        if child.redefines is None:
            emit(child)

    blob = b"".join(chunks)
    expected = record_length(root, odo)
    if len(blob) != expected:
        raise AssertionError(
            f"generator produced {len(blob)} bytes, the layout engine says "
            f"{expected}; one of them is wrong and it is not safe to guess which"
        )
    return blob, odo


def generate_file(
    root: Field,
    count: int,
    seed: int = 1,
    corruption: Corruption | None = None,
    code_page: str = "cp037",
) -> Tuple[bytes, List[Dict[str, int]]]:
    rng = Rng(seed)
    out = bytearray()
    odos: List[Dict[str, int]] = []
    for _ in range(count):
        blob, odo = generate_record(root, rng, corruption, code_page)
        out += blob
        odos.append(odo)
    return bytes(out), odos


def split_records(root: Field, data: bytes) -> List[bytes]:
    """Splits a flat file into records.

    With a fixed-length layout this is division. With OCCURS DEPENDING ON it is
    not: the length of record *n* is only knowable after decoding part of it,
    so the file cannot be indexed, seeked into, or split across workers without
    a sequential pass. That property is what makes ODO files expensive to
    migrate, and it is worth stating out loud rather than discovering in week
    six.
    """
    from .record import decode_record

    out: List[bytes] = []
    pos = 0
    while pos < len(data):
        rec = decode_record(root, data[pos:], "cp037")
        out.append(data[pos : pos + rec.length])
        pos += rec.length
    return out
