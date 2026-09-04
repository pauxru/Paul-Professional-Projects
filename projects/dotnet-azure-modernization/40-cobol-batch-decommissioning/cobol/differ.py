"""Comparing two versions of the same file.

A decommissioning project lives or dies on one question: *is the new output the
same as the old output?* There are two honest answers to that and they are not
the same answer.

**Field equivalence** — every field decodes to the same value. This is what a
reimplementation is usually tested against, because it is what the business
means by "the same".

**Byte equivalence** — the files are identical. This is what the *downstream*
systems mean by "the same", because several of them do not use your copybook.
They index into the record at hard-coded offsets, or they take a checksum, or
they were written in 1998 by someone who assumed the sign nibble was always 0xC.

The gap between those two answers is the interesting part of this project, and
the differ exists to measure it rather than argue about it.
"""

from __future__ import annotations

from dataclasses import dataclass, field as dc_field
from typing import Dict, List, Optional, Tuple

from .copybook import Field
from .record import Record, RecordError, decode_record


@dataclass
class FieldDiff:
    record_index: int
    path: str
    left: object
    right: object
    left_bytes: bytes
    right_bytes: bytes

    @property
    def value_changed(self) -> bool:
        return self.left != self.right

    def __str__(self) -> str:
        kind = "value" if self.value_changed else "representation"
        return (
            f"record {self.record_index} {self.path}: {kind} "
            f"{self.left!r} [{self.left_bytes.hex()}] -> "
            f"{self.right!r} [{self.right_bytes.hex()}]"
        )


@dataclass
class DiffReport:
    records: int = 0
    byte_identical_records: int = 0
    field_identical_records: int = 0
    diffs: List[FieldDiff] = dc_field(default_factory=list)
    length_mismatch: Optional[Tuple[int, int, int]] = None
    decode_error: Optional[str] = None
    truncated: bool = False

    @property
    def value_diffs(self) -> List[FieldDiff]:
        return [d for d in self.diffs if d.value_changed]

    @property
    def representation_diffs(self) -> List[FieldDiff]:
        """Fields that decode to the same value but are written differently.

        This is the number that surprises people. It is zero in every unit test
        anyone writes, because unit tests compare values.
        """
        return [d for d in self.diffs if not d.value_changed]

    @property
    def byte_equivalent(self) -> bool:
        return (
            self.length_mismatch is None
            and self.decode_error is None
            and self.byte_identical_records == self.records
            and not self.truncated
        )

    @property
    def field_equivalent(self) -> bool:
        return (
            self.length_mismatch is None
            and self.decode_error is None
            and not self.value_diffs
            and not self.truncated
        )

    def summary(self) -> str:
        lines = [
            f"records compared        : {self.records}",
            f"byte-identical records  : {self.byte_identical_records}",
            f"field-identical records : {self.field_identical_records}",
            f"value differences       : {len(self.value_diffs)}",
            f"representation-only     : {len(self.representation_diffs)}",
        ]
        if self.length_mismatch:
            i, a, b = self.length_mismatch
            lines.append(f"record {i} length differs: {a} vs {b}")
        if self.decode_error:
            lines.append(f"decode stopped: {self.decode_error}")
        lines.append(
            "verdict                 : "
            + (
                "byte-equivalent"
                if self.byte_equivalent
                else "field-equivalent but NOT byte-equivalent"
                if self.field_equivalent
                else "NOT equivalent"
            )
        )
        return "\n".join(lines)

    def by_field(self) -> Dict[str, int]:
        counts: Dict[str, int] = {}
        for d in self.diffs:
            key = d.path.split("[")[0]
            counts[key] = counts.get(key, 0) + 1
        return dict(sorted(counts.items(), key=lambda kv: -kv[1]))


def compare(
    root: Field,
    left: bytes,
    right: bytes,
    code_page: str = "cp037",
    max_diffs: int = 200,
) -> DiffReport:
    """Compares two files record by record.

    Records are walked with the copybook rather than sliced at a fixed length,
    because with OCCURS DEPENDING ON there is no fixed length. If the two files
    disagree about a record's length the comparison stops there: every
    subsequent record would be misaligned and the diff would be noise.
    """
    rep = DiffReport()
    lp = rp = 0
    while lp < len(left) and rp < len(right):
        try:
            lrec = decode_record(root, left[lp:], code_page)
            rrec = decode_record(root, right[rp:], code_page)
        except RecordError as exc:
            # A file that ends mid-record, or whose bytes stop matching the
            # copybook, is a real outcome and the differ has to report it rather
            # than propagate. Everything after this point is unreadable anyway.
            rep.decode_error = f"record {rep.records}: {exc}"
            rep.length_mismatch = (rep.records, len(left) - lp, len(right) - rp)
            return rep
        if lrec.length != rrec.length:
            rep.length_mismatch = (rep.records, lrec.length, rrec.length)
            return rep

        lbytes = left[lp : lp + lrec.length]
        rbytes = right[rp : rp + rrec.length]
        if lbytes == rbytes:
            rep.byte_identical_records += 1
            rep.field_identical_records += 1
        else:
            changed = _diff_cells(rep, lrec, rrec, max_diffs)
            if not changed:
                # The bytes differ but no field does. Only possible if some part
                # of the record is not described by the copybook.
                rep.field_identical_records += 1
                rep.diffs.append(
                    FieldDiff(rep.records, "<undescribed bytes>", None, None, lbytes, rbytes)
                )
            elif not any(c.value_changed for c in changed):
                rep.field_identical_records += 1

        rep.records += 1
        lp += lrec.length
        rp += rrec.length
        if len(rep.diffs) >= max_diffs:
            rep.truncated = True
            break

    if not rep.truncated and (lp < len(left)) != (rp < len(right)):
        rep.length_mismatch = (rep.records, len(left) - lp, len(right) - rp)
    return rep


def _diff_cells(
    rep: DiffReport, lrec: Record, rrec: Record, max_diffs: int
) -> List[FieldDiff]:
    found: List[FieldDiff] = []
    rcells = {c.path: c for c in rrec.cells}
    for lc in lrec.cells:
        rc = rcells.get(lc.path)
        if rc is None:
            continue
        if lc.raw == rc.raw:
            continue
        d = FieldDiff(rep.records, lc.path, lc.value, rc.value, lc.raw, rc.raw)
        found.append(d)
        if len(rep.diffs) < max_diffs:
            rep.diffs.append(d)
    return found
