"""Runs the whole argument end to end and prints the numbers.

Output is deterministic — every corpus is a pure function of its seed — so the
figures in docs/results.md are reproducible by running this file.
"""

from __future__ import annotations

import sys
import time
from decimal import Decimal
from pathlib import Path

from cobol.copybook import describe, parse_copybook, record_length
from cobol.differ import compare
from cobol.generate import Corruption, generate_file, split_records
from cobol.pipeline import Rounding, bill, compare_modes, run_batch
from cobol.record import decode_record, encode_record

HERE = Path(__file__).resolve().parent
RECORDS = 5000
SEED = 20240612


def rule(title: str) -> None:
    print()
    print("=" * 78)
    print(title)
    print("=" * 78)


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except (AttributeError, OSError):
        pass
    root = parse_copybook((HERE / "copybooks" / "custmast.cpy").read_text())

    rule("1. THE LAYOUT")
    print(describe(root))
    print()
    print(f"minimum record length : {record_length(root)} bytes")
    print(f"maximum record length : {record_length(root, {'CM-TXN-COUNT': 5})} bytes")
    print(
        "Because CM-TXN-TABLE is OCCURS DEPENDING ON, the file has no fixed\n"
        "record length. It cannot be seeked into, split across workers, or\n"
        "loaded with a fixed-width reader. Record n+1 starts wherever record n\n"
        "happens to end, and you only know that after decoding record n."
    )

    rule("2. A CORPUS THAT LOOKS LIKE A REAL FILE")
    t0 = time.perf_counter()
    data, _ = generate_file(root, RECORDS, seed=SEED)
    gen_ms = (time.perf_counter() - t0) * 1000
    records = split_records(root, data)
    lengths = sorted({len(r) for r in records})
    print(f"records              : {len(records)}")
    print(f"bytes                : {len(data)}")
    print(f"distinct lengths     : {lengths}")
    print(f"generation time      : {gen_ms:.0f} ms")

    t0 = time.perf_counter()
    decoded = [decode_record(root, r) for r in records]
    decode_ms = (time.perf_counter() - t0) * 1000
    total_cells = sum(len(d.cells) for d in decoded)
    non_canon_cells = sum(len(d.non_canonical()) for d in decoded)
    dirty_records = sum(1 for d in decoded if d.non_canonical())
    print(f"decode time          : {decode_ms:.0f} ms "
          f"({decode_ms / len(records) * 1000:.0f} us/record)")
    print(f"fields decoded       : {total_cells}")
    print(
        f"fields whose bytes cannot be reproduced from their value alone: "
        f"{non_canon_cells} ({non_canon_cells / total_cells:.1%})"
    )
    print(
        f"records containing at least one such field: "
        f"{dirty_records} ({dirty_records / len(records):.1%})"
    )

    rule("3. READ AND WRITE BACK, TWO WAYS")
    faithful = b"".join(encode_record(root, d) for d in decoded)
    naive = b"".join(encode_record(root, d, canonical_signs=True) for d in decoded)
    print(f"representation-preserving re-emission is byte-identical : "
          f"{faithful == data}")
    print(f"naive re-emission is byte-identical                     : "
          f"{naive == data}")

    rep = compare(root, data, naive, max_diffs=10**9)
    print()
    print(rep.summary())
    print()
    print("differences by field:")
    for path, n in rep.by_field().items():
        print(f"  {path:<40} {n}")
    print()
    print(
        "Read that verdict again. Not one number changed. The reimplementation\n"
        "is correct by every test anyone would write, and it rewrote "
        f"{rep.records - rep.byte_identical_records} of\n"
        f"{rep.records} records."
    )

    overlay = [d for d in rep.value_diffs if d.path.endswith("CM-KEY-ALT")]
    if overlay:
        print()
        print(
            f"And {len(overlay)} of those representation changes are visible as a\n"
            "VALUE change through the CM-KEY-ALT redefinition, which the archive\n"
            "job reads as text:"
        )
        for d in overlay[:3]:
            print(f"  record {d.record_index}: {d.left!r} -> {d.right!r}")

    rule("4. THE ARITHMETIC")
    print(
        "CM-BALANCE is PIC S9(7)V99. COBOL stores a computed value by "
        "truncating\ntoward zero; there is no ROUNDED in this program. Three "
        "plausible modern\nversions of the same rule:\n"
    )
    result = compare_modes(root, data)
    header = (
        f"{'mode':<10} {'differs':>9} {'rate':>8} {'on +ve':>8} {'on -ve':>8} "
        f"{'drift':>16}"
    )
    print(header)
    print("-" * len(header))
    for mode in (Rounding.HALF_UP, Rounding.FLOOR, Rounding.FLOAT):
        r = result[mode]
        print(
            f"{mode:<10} {r['differing']:>9} {r['rate']:>7.2%} "
            f"{r['differing_positive']:>8} {r['differing_negative']:>8} "
            f"{str(r['drift']):>16}"
        )
    neg = result[Rounding.FLOOR]["negative_records"]
    print()
    print(f"(the corpus contains {neg} records with a negative balance, "
          f"{neg / RECORDS:.1%})")

    rule("5. WHY THE TEST EXTRACT PASSES")
    extract = b"".join(
        r for r, d in zip(records, decoded) if Decimal(d["CM-BALANCE"]) >= 0
    )
    n_extract = sum(1 for d in decoded if Decimal(d["CM-BALANCE"]) >= 0)
    ex = compare_modes(root, extract)
    print(
        f"A tester pulls {n_extract} records from a quiet week — no refunds, no\n"
        "credit balances — and runs all three implementations against them:\n"
    )
    print(f"{'mode':<10} {'differs':>9} {'rate':>8}")
    print("-" * 29)
    for mode in (Rounding.HALF_UP, Rounding.FLOOR, Rounding.FLOAT):
        r = ex[mode]
        print(f"{mode:<10} {r['differing']:>9} {r['rate']:>7.2%}")
    print()
    print(
        "ROUND_FLOOR passes cleanly. It is the implementation you get from\n"
        "`int()`, `//` or `math.floor`, it is wrong on every negative amount,\n"
        "and it is invisible until the first refund run.\n\n"
        "ROUND_HALF_UP — the version everyone would call correct — fails "
        "loudly\nand immediately, which is why it never reaches production."
    )

    rule("6. SIZE ERRORS NOBODY DECLARED")
    out, stats = run_batch(root, data, Rounding.MAINFRAME)
    print(stats)
    print(
        f"\n{stats.size_errors} records produced a balance too large for "
        "PIC S9(7)V99.\nThe COBOL has no ON SIZE ERROR clause, so the mainframe "
        "silently drops\nthe high-order digits. This harness reproduces that "
        "behaviour on purpose,\nand counts it, because the alternative is a "
        "reimplementation that raises\nan exception at 03:00 on a night nobody "
        "is watching."
    )

    rule("7. THE BATCH JOB, DONE PROPERLY")
    final = compare(root, data, out, max_diffs=10**9)
    print(final.summary())
    print()
    print("differences by field:")
    for path, n in final.by_field().items():
        print(f"  {path:<40} {n}")
    print(
        "\nOne field changed, because one field was supposed to change. "
        "Everything\nelse in the file is byte-for-byte what it was this "
        "morning."
    )

    rule("8. A CLEAN SYNTHETIC FILE PROVES NOTHING")
    clean, _ = generate_file(root, 1000, seed=7, corruption=Corruption.none())
    clean_naive = b"".join(
        encode_record(root, decode_record(root, r), canonical_signs=True)
        for r in split_records(root, clean)
    )
    print(
        f"Same naive re-emission, run over 1000 records generated with no odd\n"
        f"sign nibbles and no blank padding: byte-identical = "
        f"{clean_naive == clean}.\n\n"
        "That is the test file a migration team writes for itself, and it is\n"
        "why the bug ships."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
