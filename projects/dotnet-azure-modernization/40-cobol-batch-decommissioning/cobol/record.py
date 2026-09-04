"""Decoding and encoding whole records.

The interface deliberately returns more than the values. Decoding produces a
:class:`Record` that carries, alongside each number, *how that number was
written* — which sign nibble, which zone, which byte positions held spaces.

That is the difference between a system that can read the file and a system that
can prove it did not change it. See ADR 0002.

Every cell also carries ``canonical``, and that flag is not a heuristic. It is
computed by actually re-encoding the field the way a naive reimplementation
would and comparing bytes. A rule of thumb about "preferred sign nibbles" would
have been wrong for zoned positives, where 0xF is overwhelmingly common in real
files but 0xC is what an encoder produces from a value alone.
"""

from __future__ import annotations

from dataclasses import dataclass, field as dc_field
from decimal import Decimal
from typing import Any, Dict, List, Optional, Tuple

from . import numeric
from .copybook import Field, _norm
from .ebcdic import to_text, from_text
from .picture import Category, Usage


class RecordError(ValueError):
    pass


@dataclass
class Cell:
    """One decoded elementary field."""

    path: str
    value: Any
    offset: int
    length: int
    raw: bytes
    #: How the sign was written, when the encoding has a choice. ``None`` for
    #: text and for unsigned fields.
    sign_repr: Optional[int] = None
    #: Byte positions inside the field that held EBCDIC space, not a digit.
    space_positions: Tuple[int, ...] = ()
    #: True when re-encoding from the value alone reproduces the original bytes.
    canonical: bool = True
    #: True when this cell came from a REDEFINES overlay and therefore shares
    #: its bytes with another cell.
    overlay: bool = False
    #: The copybook field this cell was decoded from.
    field: Optional[Field] = None

    @property
    def blank_padded(self) -> bool:
        return bool(self.space_positions)


@dataclass
class Record:
    cells: List[Cell] = dc_field(default_factory=list)
    length: int = 0

    def __getitem__(self, path: str) -> Any:
        return self.get_cell(path).value

    def get_cell(self, path: str) -> Cell:
        p = _norm(path)
        for c in self.cells:
            if _norm(c.path) == p or _norm(c.path).endswith("." + p):
                return c
        raise KeyError(path)

    def to_dict(self) -> Dict[str, Any]:
        return {c.path: c.value for c in self.cells}

    def non_canonical(self) -> List[Cell]:
        """Fields whose bytes cannot be reproduced from the value alone."""
        return [c for c in self.cells if not c.canonical]


def _encode_cell(f: Field, cell: Cell, code_page: str, canonical: bool) -> bytes:
    """Produces the bytes for one cell.

    ``canonical=True`` is the naive path: value in, bytes out, no memory of how
    the field was originally written. ``canonical=False`` reproduces the
    original representation.
    """
    pic = f.picture
    assert pic is not None
    if pic.category is Category.ALPHANUMERIC:
        blob = from_text(cell.value, code_page)
        if len(blob) != cell.length:
            raise RecordError(
                f"{cell.path}: {len(blob)} bytes for a {cell.length}-byte field"
            )
        return blob
    if pic.usage is Usage.COMP_3:
        return numeric.encode_packed(
            Decimal(cell.value),
            pic.digits,
            pic.scale,
            pic.signed,
            sign_nibble=None if canonical else cell.sign_repr,
        )
    if pic.usage is Usage.COMP:
        return numeric.encode_binary(
            Decimal(cell.value), cell.length, pic.scale, pic.signed
        )
    return numeric.encode_zoned(
        Decimal(cell.value),
        pic.digits,
        pic.scale,
        pic.signed,
        sign_zone=None if canonical else cell.sign_repr,
        space_positions=() if canonical else cell.space_positions,
    )


def _decode_elementary(
    f: Field, data: bytes, path: str, offset: int, code_page: str
) -> Cell:
    pic = f.picture
    assert pic is not None
    n = pic.storage_bytes()
    raw = data[offset : offset + n]
    if len(raw) != n:
        raise RecordError(
            f"{path}: record ended after {len(raw)} of {n} bytes at offset {offset}"
        )

    if pic.category is Category.ALPHANUMERIC:
        cell = Cell(path, to_text(raw, code_page), offset, n, raw)
    elif pic.usage is Usage.COMP_3:
        p = numeric.decode_packed(raw, pic.scale)
        cell = Cell(path, p.value, offset, n, raw, sign_repr=p.sign_nibble)
    elif pic.usage is Usage.COMP:
        v = numeric.decode_binary(raw, pic.scale, pic.signed)
        cell = Cell(path, v, offset, n, raw)
    else:
        z = numeric.decode_zoned(raw, pic.scale, pic.signed)
        cell = Cell(
            path,
            z.value,
            offset,
            n,
            raw,
            sign_repr=z.sign_zone,
            space_positions=z.space_positions,
        )

    cell.canonical = _encode_cell(f, cell, code_page, canonical=True) == raw
    if _encode_cell(f, cell, code_page, canonical=False) != raw:
        # Not a data problem — a bug in this library. Fail loudly rather than
        # emit a file that differs from its input in a way nobody asked for.
        raise RecordError(
            f"{path}: representation-preserving round trip failed on {raw.hex()}"
        )
    cell.field = f
    return cell


def decode_record(root: Field, data: bytes, code_page: str = "cp037") -> Record:
    rec = Record()
    counts: Dict[str, int] = {}

    def visit(f: Field, prefix: str, offset: int) -> int:
        path = f"{prefix}.{f.name}" if prefix else f.name
        if f.redefines is not None:
            # An overlay reinterprets bytes that a sibling already covers. It is
            # decoded, but it does not advance the cursor and it is never used
            # to write bytes back — see docs/known-limitations.md.
            _visit_once(f, path, f.offset, overlay=True)
            return offset

        times = f.occurs_min
        if f.depending_on:
            ctrl = counts.get(_norm(f.depending_on))
            if ctrl is None:
                raise RecordError(
                    f"{path}: DEPENDING ON {f.depending_on} which has not been read yet"
                )
            times = int(ctrl)
            if not f.occurs_min <= times <= f.occurs_max:
                raise RecordError(
                    f"{path}: OCCURS {f.occurs_min} TO {f.occurs_max} "
                    f"but {f.depending_on} = {times}"
                )

        cursor = offset
        for i in range(times):
            p = f"{path}[{i}]" if (times > 1 or f.occurs_max > 1) else path
            cursor = _visit_once(f, p, cursor)
        return cursor

    def _visit_once(f: Field, path: str, offset: int, overlay: bool = False) -> int:
        if f.picture is not None:
            cell = _decode_elementary(f, data, path, offset, code_page)
            cell.overlay = overlay
            rec.cells.append(cell)
            if f.picture.category is Category.NUMERIC and not overlay:
                counts[_norm(f.name)] = cell.value
            return offset + cell.length
        cursor = offset
        for c in f.children:
            if overlay:
                cursor = _visit_once(c, f"{path}.{c.name}", cursor, overlay=True)
            else:
                cursor = visit(c, path, cursor)
        return cursor

    end = _visit_once(root, root.name, 0)
    rec.length = end
    return rec


def encode_record(
    root: Field, rec: Record, code_page: str = "cp037", canonical_signs: bool = False
) -> bytes:
    """Re-encodes a decoded record.

    With ``canonical_signs=False`` (the default) the original representation is
    reproduced, so ``encode(decode(x)) == x``. Setting it to ``True`` is what a
    naive reimplementation does: it writes each value in the encoder's preferred
    form. Both behaviours exist so the differ can measure the gap between them,
    which is the whole point of the project.
    """
    out = bytearray(b"\x00" * rec.length)
    written = bytearray(rec.length)

    for cell in rec.cells:
        if cell.overlay:
            continue
        f = cell.field if cell.field is not None else _field_for(root, cell.path)
        blob = _encode_cell(f, cell, code_page, canonical=canonical_signs)
        out[cell.offset : cell.offset + cell.length] = blob
        for i in range(cell.offset, cell.offset + cell.length):
            written[i] = 1

    gaps = [i for i, w in enumerate(written) if not w]
    if gaps:
        raise RecordError(
            f"{len(gaps)} byte(s) of the record were not covered by any field "
            f"(first at offset {gaps[0]}); the layout and the data disagree"
        )
    return bytes(out)


_FIELD_CACHE: Dict[Tuple[int, str], Field] = {}


def _field_for(root: Field, path: str) -> Field:
    key = (id(root), path)
    hit = _FIELD_CACHE.get(key)
    if hit is not None:
        return hit
    leaf = path.split(".")[-1]
    if "[" in leaf:
        leaf = leaf[: leaf.index("[")]
    f = root.find(leaf)
    if f is None:
        raise RecordError(f"no field named {leaf!r} in the copybook")
    _FIELD_CACHE[key] = f
    return f
