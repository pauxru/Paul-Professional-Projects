"""The business logic, three ways.

The batch job being decommissioned is ``BILLRUN``: it reads the customer master
file, applies a service charge and VAT, subtracts any credit, and writes the
file back. Two hundred lines of COBOL, run nightly since 1994, and the only
place the charging rules are written down.

Reimplementing it is easy. Reimplementing it *identically* is the job, and the
part that is easy to get wrong is not the formula — it is what happens to the
third decimal place.

COBOL stores a computed value into a ``PIC S9(7)V99`` field by **truncating**
toward zero. There is no rounding unless the program says ``ROUNDED``. This
module implements the reference semantics and three plausible modern versions
that each get it wrong in a different way:

``ROUND_HALF_UP``
    What you get from ``Decimal.quantize`` with the obvious argument, and what
    anyone would call "correct". Differs from the mainframe on roughly half of
    all records, by a penny.

``ROUND_FLOOR``
    What you get from ``//``, ``math.floor``, or ``int()`` on a scaled integer.
    Agrees with the mainframe on every positive amount and disagrees on every
    negative one. This is the dangerous one: a test extract that happens to
    contain no refunds will pass.

``float``
    What you get if the ported code uses ``float`` for money. Agrees almost
    always. "Almost" is doing a lot of work in that sentence.

The point of laying them out side by side is that they are ordered by how often
they fail, and inversely ordered by how likely they are to survive testing.
"""

from __future__ import annotations

from dataclasses import dataclass
from decimal import ROUND_DOWN, ROUND_FLOOR, ROUND_HALF_UP, Decimal
from typing import Dict, List

from .copybook import Field
from .record import Record, decode_record, encode_record

SERVICE_CHARGE = Decimal("2.50")
VAT_RATE = Decimal("0.175")
CENT = Decimal("0.01")
#: ``CM-BALANCE`` is ``PIC S9(7)V99``: seven digits before the implied point.
BALANCE_LIMIT = Decimal(10) ** 7


class Rounding:
    MAINFRAME = "truncate"
    HALF_UP = "half-up"
    FLOOR = "floor"
    FLOAT = "float"


def _store(value: Decimal, mode: str) -> Decimal:
    """Stores an intermediate result into a two-decimal COBOL field."""
    if mode == Rounding.MAINFRAME:
        return value.quantize(CENT, rounding=ROUND_DOWN)
    if mode == Rounding.HALF_UP:
        return value.quantize(CENT, rounding=ROUND_HALF_UP)
    if mode == Rounding.FLOOR:
        return value.quantize(CENT, rounding=ROUND_FLOOR)
    if mode == Rounding.FLOAT:
        return Decimal(repr(round(float(value), 2)))
    raise ValueError(f"unknown rounding mode {mode!r}")


@dataclass(frozen=True)
class Billed:
    value: Decimal
    size_error: bool
    """True if the result did not fit in ``PIC S9(7)V99``.

    The COBOL has no ``ON SIZE ERROR`` clause, so the mainframe silently drops
    the high-order digits and carries on: a balance of 10,766,540.84 is stored
    as 766,540.84 and the customer is seven million pounds better off. A Python
    reimplementation that uses unbounded integers raises instead — or worse,
    doesn't, and writes a number the file cannot hold.

    Neither behaviour is obviously right, and that is the point: the question
    has to be asked before the cutover, not after.
    """


def bill_detail(balance: Decimal, credit: Decimal, mode: str) -> Billed:
    """The charging rule.

    The COBOL is::

        COMPUTE WS-GROSS = CM-BALANCE + WS-SERVICE-CHARGE
        COMPUTE WS-VAT   = WS-GROSS * WS-VAT-RATE
        COMPUTE CM-BALANCE = WS-GROSS + WS-VAT - CM-CREDIT

    Each ``COMPUTE`` stores into a ``PIC S9(7)V99`` working field, so each one
    truncates. Doing the whole expression at full precision and rounding once at
    the end is a different answer — which is why the intermediate ``_store``
    calls are here and not folded away.
    """
    gross = _store(balance + SERVICE_CHARGE, mode)
    vat = _store(gross * VAT_RATE, mode)
    exact = _store(gross + vat - credit, mode)
    if abs(exact) < BALANCE_LIMIT:
        return Billed(exact, False)
    sign = -1 if exact < 0 else 1
    return Billed(sign * (abs(exact) % BALANCE_LIMIT), True)


def bill(balance: Decimal, credit: Decimal, mode: str) -> Decimal:
    return bill_detail(balance, credit, mode).value


@dataclass
class RunStats:
    records: int = 0
    changed_fields: int = 0
    size_errors: int = 0

    def __str__(self) -> str:
        s = f"{self.records} records, {self.changed_fields} balances updated"
        if self.size_errors:
            s += f", {self.size_errors} silent size errors"
        return s


def run_batch(
    root: Field,
    data: bytes,
    mode: str = Rounding.MAINFRAME,
    code_page: str = "cp037",
    canonical_signs: bool = False,
) -> tuple[bytes, RunStats]:
    """Reads a file, applies the charging rule, writes the file back.

    ``canonical_signs`` is the knob that separates "reimplemented the logic"
    from "reimplemented the file". With it on, every field is rewritten in the
    encoder's preferred representation even when the value did not change.
    """
    out = bytearray()
    stats = RunStats()
    pos = 0
    while pos < len(data):
        rec = decode_record(root, data[pos:], code_page)
        cell = rec.get_cell("CM-BALANCE")
        credit = Decimal(rec["CM-CREDIT"])
        result = bill_detail(Decimal(cell.value), credit, mode)
        if result.size_error:
            stats.size_errors += 1
        if result.value != cell.value:
            stats.changed_fields += 1
        cell.value = result.value
        # The value changed, so its original sign representation no longer
        # describes it. Recompute the sign, keep everything else.
        cell.sign_repr = None
        out += encode_record(root, rec, code_page, canonical_signs=canonical_signs)
        pos += rec.length
        stats.records += 1
    return bytes(out), stats


def compare_modes(
    root: Field, data: bytes, code_page: str = "cp037"
) -> Dict[str, Dict[str, object]]:
    """Runs every rounding mode and reports how each differs from the mainframe.

    Reported per mode: how many records get a different balance, the total
    signed drift in currency, and — the number that matters for test design —
    how the disagreements split between positive and negative starting
    balances.
    """
    records: List[Record] = []
    pos = 0
    while pos < len(data):
        rec = decode_record(root, data[pos:], code_page)
        records.append(rec)
        pos += rec.length

    reference = [
        bill(Decimal(r["CM-BALANCE"]), Decimal(r["CM-CREDIT"]), Rounding.MAINFRAME)
        for r in records
    ]

    out: Dict[str, Dict[str, object]] = {}
    for mode in (Rounding.HALF_UP, Rounding.FLOOR, Rounding.FLOAT):
        diffs = 0
        drift = Decimal(0)
        neg_diffs = 0
        neg_total = 0
        for rec, ref in zip(records, reference):
            bal = Decimal(rec["CM-BALANCE"])
            got = bill(bal, Decimal(rec["CM-CREDIT"]), mode)
            negative = bal < 0
            neg_total += 1 if negative else 0
            if got != ref:
                diffs += 1
                drift += got - ref
                neg_diffs += 1 if negative else 0
        out[mode] = {
            "records": len(records),
            "differing": diffs,
            "rate": diffs / len(records) if records else 0.0,
            "drift": drift,
            "differing_negative": neg_diffs,
            "negative_records": neg_total,
            "differing_positive": diffs - neg_diffs,
        }
    return out
