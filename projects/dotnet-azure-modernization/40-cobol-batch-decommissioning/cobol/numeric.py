"""Numeric encodings: packed decimal (COMP-3), zoned decimal, and binary.

This is where the money lives, literally. Every one of these formats has a case
that decodes to the right number and re-encodes to different bytes, and a
migration that changes bytes in a file the business has trusted since 1994 is
the failure everyone is afraid of.

The design decision that runs through the whole module: **decoding returns the
value and the sign representation, not just the value.** ``Decimal("0")`` is not
enough information to reproduce the original bytes, because COMP-3 has three
distinct encodings that all mean zero. See ADR 0002.
"""

from __future__ import annotations

from dataclasses import dataclass
from decimal import Decimal
from typing import Optional

from .ebcdic import EBCDIC_SPACE, EBCDIC_ZERO


class DecodeError(ValueError):
    """Raised when bytes cannot be a valid instance of the declared type."""


# --- packed decimal (COMP-3) ------------------------------------------------
#
# Two digits per byte, low nibble of the last byte is the sign.
#   0xC  positive (preferred)
#   0xD  negative (preferred)
#   0xF  unsigned  (preferred for a PIC without S)
#   0xA, 0xE  positive (alternate)
#   0xB       negative (alternate)
# The alternates are rare but real: some compilers and some hand-written
# assembler emit them, and a reader that rejects them rejects valid production
# data.

_POSITIVE_SIGNS = {0xA, 0xC, 0xE, 0xF}
_NEGATIVE_SIGNS = {0xB, 0xD}
PREFERRED_POSITIVE = 0xC
PREFERRED_NEGATIVE = 0xD
PREFERRED_UNSIGNED = 0xF


@dataclass(frozen=True)
class Packed:
    """A decoded COMP-3 field, including how its sign was written."""

    value: Decimal
    sign_nibble: int

    @property
    def is_negative(self) -> bool:
        return self.sign_nibble in _NEGATIVE_SIGNS

    @property
    def is_canonical(self) -> bool:
        """True if re-encoding with preferred signs reproduces the input."""
        return self.sign_nibble in (
            PREFERRED_POSITIVE,
            PREFERRED_NEGATIVE,
            PREFERRED_UNSIGNED,
        )


def packed_length(digits: int) -> int:
    """Bytes required for ``digits`` digits of COMP-3.

    The formula people write from memory is ``digits // 2 + 1``. It is right,
    but only because the sign nibble always takes half a byte: an odd digit
    count fills its last byte exactly, an even one wastes a leading nibble.
    """
    if digits < 1:
        raise ValueError("a COMP-3 field needs at least one digit")
    return digits // 2 + 1


def decode_packed(data: bytes, scale: int = 0) -> Packed:
    if not data:
        raise DecodeError("empty COMP-3 field")
    digits = []
    for i, byte in enumerate(data):
        hi, lo = byte >> 4, byte & 0xF
        if hi > 9:
            raise DecodeError(
                f"COMP-3 byte {i} high nibble 0x{hi:X} is not a digit "
                f"(field is {data.hex()})"
            )
        digits.append(hi)
        if i < len(data) - 1:
            if lo > 9:
                raise DecodeError(
                    f"COMP-3 byte {i} low nibble 0x{lo:X} is not a digit "
                    f"(field is {data.hex()})"
                )
            digits.append(lo)
    sign = data[-1] & 0xF
    if sign not in _POSITIVE_SIGNS and sign not in _NEGATIVE_SIGNS:
        raise DecodeError(
            f"COMP-3 sign nibble 0x{sign:X} is not a valid sign (field is {data.hex()})"
        )
    unsigned = Decimal("".join(str(d) for d in digits))
    if scale:
        unsigned = unsigned.scaleb(-scale)
    if sign in _NEGATIVE_SIGNS:
        unsigned = -unsigned
    return Packed(unsigned, sign)


def encode_packed(
    value: Decimal,
    digits: int,
    scale: int = 0,
    signed: bool = True,
    sign_nibble: Optional[int] = None,
) -> bytes:
    """Encodes ``value`` as COMP-3.

    ``sign_nibble`` overrides the sign representation. That parameter exists so
    a round trip can reproduce non-preferred signs byte-for-byte; without it,
    re-encoding a file silently rewrites 0xA to 0xC and the output stops
    matching. See ADR 0002.
    """
    scaled = (value.scaleb(scale)).to_integral_value(rounding="ROUND_HALF_UP")
    negative = scaled < 0
    # format(..., "f") not str(): Decimal.scaleb produces exponent notation
    # ("1.000E+5"), and str() of that is not a run of digits.
    body = format(abs(scaled), "f")
    if len(body) > digits:
        raise ValueError(
            f"{value} needs {len(body)} digits but the field holds {digits}"
        )
    body = body.rjust(digits, "0")
    if sign_nibble is None:
        if not signed:
            sign_nibble = PREFERRED_UNSIGNED
        else:
            sign_nibble = PREFERRED_NEGATIVE if negative else PREFERRED_POSITIVE
    nibbles = [int(c) for c in body] + [sign_nibble]
    if len(nibbles) % 2:
        nibbles.insert(0, 0)
    return bytes(
        (nibbles[i] << 4) | nibbles[i + 1] for i in range(0, len(nibbles), 2)
    )


# --- zoned decimal (DISPLAY) ------------------------------------------------
#
# One digit per byte, EBCDIC 0xF0-0xF9. A signed field carries the sign in the
# zone nibble of the last byte (trailing sign, the default) or the first
# (SIGN LEADING): 0xC positive, 0xD negative.
#
# In ASCII listings this is the famous "overpunch": PIC S9(4) holding -1234 is
# written "123M", and +1234 is "123D". Reports have looked like that since the
# 1960s and business users read them fluently.

_OVERPUNCH_POSITIVE = "{ABCDEFGHI"
_OVERPUNCH_NEGATIVE = "}JKLMNOPQR"


@dataclass(frozen=True)
class Zoned:
    value: Decimal
    sign_zone: Optional[int]
    """Zone nibble of the signed byte, or None for an unsigned field."""

    space_positions: tuple = ()
    """Byte offsets within the field that held EBCDIC space instead of a digit.

    Real files do this: an uninitialised working-storage field written to disk
    is 0x40 repeated, and a short numeric MOVEd into a longer field can leave
    leading spaces. COBOL treats those positions as zero, and so do we — but we
    record *which* positions, because a system that stores 0 and writes back
    0xF0 has changed a file the business believes is unchanged.
    """

    @property
    def padded_with_spaces(self) -> bool:
        return bool(self.space_positions)


def decode_zoned(data: bytes, scale: int = 0, signed: bool = True) -> Zoned:
    if not data:
        raise DecodeError("empty zoned field")
    digits = []
    spaces = []
    sign_zone = None
    for i, byte in enumerate(data):
        zone, digit = byte >> 4, byte & 0xF
        if byte == EBCDIC_SPACE:
            spaces.append(i)
            digits.append(0)
            continue
        if digit > 9:
            raise DecodeError(
                f"zoned byte {i} = 0x{byte:02X} has non-digit low nibble "
                f"(field is {data.hex()})"
            )
        if signed and i == len(data) - 1:
            sign_zone = zone
        elif zone != 0xF and zone not in (0xC, 0xD):
            raise DecodeError(
                f"zoned byte {i} = 0x{byte:02X} has unexpected zone 0x{zone:X} "
                f"(field is {data.hex()})"
            )
        digits.append(digit)
    unsigned = Decimal("".join(str(d) for d in digits))
    if scale:
        unsigned = unsigned.scaleb(-scale)
    if sign_zone == 0xD:
        unsigned = -unsigned
    return Zoned(unsigned, sign_zone, tuple(spaces))


def encode_zoned(
    value: Decimal,
    digits: int,
    scale: int = 0,
    signed: bool = True,
    sign_zone: Optional[int] = None,
    space_positions: tuple = (),
) -> bytes:
    scaled = (value.scaleb(scale)).to_integral_value(rounding="ROUND_HALF_UP")
    negative = scaled < 0
    # format(..., "f") not str(): Decimal.scaleb produces exponent notation
    # ("1.000E+5"), and str() of that is not a run of digits.
    body = format(abs(scaled), "f")
    if len(body) > digits:
        raise ValueError(
            f"{value} needs {len(body)} digits but the field holds {digits}"
        )
    body = body.rjust(digits, "0")
    out = bytearray(EBCDIC_ZERO + int(c) for c in body)
    if signed:
        if sign_zone is None:
            sign_zone = 0xD if negative else 0xC
        out[-1] = (sign_zone << 4) | (out[-1] & 0xF)
    for i in space_positions:
        if 0 <= i < len(out):
            out[i] = EBCDIC_SPACE
    return bytes(out)


def overpunch_char(digit: int, negative: bool) -> str:
    """The printable overpunch character for a signed trailing digit."""
    if not 0 <= digit <= 9:
        raise ValueError("digit out of range")
    return (_OVERPUNCH_NEGATIVE if negative else _OVERPUNCH_POSITIVE)[digit]


def parse_overpunch(ch: str) -> tuple[int, bool]:
    """Inverse of :func:`overpunch_char`. Returns ``(digit, negative)``."""
    if ch in _OVERPUNCH_POSITIVE:
        return _OVERPUNCH_POSITIVE.index(ch), False
    if ch in _OVERPUNCH_NEGATIVE:
        return _OVERPUNCH_NEGATIVE.index(ch), True
    if ch.isdigit():
        return int(ch), False
    raise DecodeError(f"{ch!r} is not an overpunched digit")


# --- binary (COMP / COMP-4 / BINARY) ----------------------------------------


def binary_length(digits: int) -> int:
    """IBM COBOL storage for ``PIC 9(n) COMP``."""
    if digits <= 4:
        return 2
    if digits <= 9:
        return 4
    if digits <= 18:
        return 8
    raise ValueError(f"COMP fields hold at most 18 digits, got {digits}")


def decode_binary(data: bytes, scale: int = 0, signed: bool = True) -> Decimal:
    v = int.from_bytes(data, "big", signed=signed)
    d = Decimal(v)
    return d.scaleb(-scale) if scale else d


def encode_binary(
    value: Decimal, length: int, scale: int = 0, signed: bool = True
) -> bytes:
    scaled = int((value.scaleb(scale)).to_integral_value(rounding="ROUND_HALF_UP"))
    return scaled.to_bytes(length, "big", signed=signed)
