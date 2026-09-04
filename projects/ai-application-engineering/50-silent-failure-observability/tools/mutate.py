"""Mutation testing: does the suite actually notice when the load-bearing logic breaks?

A passing test suite proves the tests pass. It does not prove they would fail. Each mutant
below is a single edit to a line the project's conclusions rest on -- the persistence rule,
the one-sidedness of the CUSUM, the sign hash in the embedding, the estimation-error
resampling in the threshold. If the suite still passes with one of them applied, the
corresponding test is decorative and the number it protects is unverified.

The guard that matters is `_ran`. In an earlier project this harness reported a perfect
score while every mutant run had died on an import error before executing a single test:
the runs failed, the harness counted "failed" as "killed", and the metric read 8/8. A
quality metric that fails in the direction that looks like success is worse than no metric.
So a run only counts if pytest's own summary line proves it collected and ran tests.
"""

from __future__ import annotations

import re
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SUMMARY = re.compile(r"(\d+) (?:passed|failed)")


@dataclass(frozen=True)
class Mutant:
    name: str
    path: str
    #: Exact source text to replace. Multi-line anchors are newline-normalised first.
    find: str
    replace: str
    breaks: str


MUTANTS: tuple[Mutant, ...] = (
    Mutant(
        name="persistence-disabled",
        path="src/detectors.py",
        find="            if run >= persistence:\n                return day - persistence + 1",
        replace="            if run >= 1:\n                return day - 1 + 1",
        breaks="one day over threshold becomes an alert; every blip is a page",
    ),
    Mutant(
        name="alerts-inside-reference-window",
        path="src/detectors.py",
        find="    start_day: int = REFERENCE_DAYS,\n) -> int:\n    \"\"\"The first day of the first run",
        replace="    start_day: int = 0,\n) -> int:\n    \"\"\"The first day of the first run",
        breaks="a detector may alert inside the window it calibrated itself on",
    ),
    Mutant(
        name="cusum-two-sided",
        path="src/detectors.py",
        find="        s = max(0.0, s + (x - target - slack))",
        replace="        s = s + (x - target - slack)",
        breaks="the CUSUM accumulates downward drift and stops being a one-sided test",
    ),
    Mutant(
        name="calibrate-on-the-whole-series",
        path="src/detectors.py",
        find="    window = [s for s in scores[:REFERENCE_DAYS]]",
        replace="    window = [s for s in scores]",
        breaks="the threshold sees the degraded days and calibrates itself out of alerting",
    ),
    Mutant(
        name="no-probability-floor",
        path="src/detectors.py",
        find="    return np.clip(p, 1e-6, None)",
        replace="    return p",
        breaks="an empty bin sends PSI to infinity and it becomes a report about sample size",
    ),
    Mutant(
        name="bootstrap-ignores-estimation-error",
        path="src/detectors.py",
        find="        ref = rng.choice(rates, size=rates.size, replace=True)",
        replace="        ref = rates",
        breaks="the threshold stops accounting for error in its own reference mean",
    ),
    Mutant(
        name="self-similarity-one-sided",
        path="src/detectors.py",
        find="    scores = [abs(s - baseline) for s in similarity]",
        replace="    scores = [s - baseline for s in similarity]",
        breaks="the detector reverts to the directional hypothesis the measurements refuted",
    ),
    Mutant(
        name="unsigned-hash-columns",
        path="src/embedding.py",
        find="        sign = 1.0 if (h >> 32) & 1 else -1.0",
        replace="        sign = 1.0",
        breaks="hash collisions reinforce instead of cancelling and long texts all look alike",
    ),
    Mutant(
        name="empty-text-embeds-to-a-real-vector",
        path="src/embedding.py",
        find="    if not text.strip():",
        replace="    if False:",
        breaks="every empty answer embeds to the same arbitrary unit vector",
    ),
    Mutant(
        name="false-positives-are-free",
        path="src/evaluate.py",
        find="        1 if panel.input_shift_false_positive.get(detector, False) else 0,",
        replace="        0,",
        breaks="alerting on the non-regression control stops counting against a detector",
    ),
    Mutant(
        name="material-day-without-persistence",
        path="src/stream.py",
        find="            run += 1\n            if run >= persistence:\n                return day - persistence + 1",
        replace="            run += 1\n            if run >= 1:\n                return day - 1 + 1",
        breaks="one noisy day decides the ground truth every detection delay is measured from",
    ),
    Mutant(
        name="psi-reference-positional-misalignment",
        path="src/detectors.py",
        find="        for slot, d in enumerate(self._kept):",
        replace="        for slot, d in enumerate(range(len(self._kept))):",
        breaks="a dropped degenerate dimension silently misaligns every dimension after it",
    ),
)


def _ran(stdout: str) -> bool:
    """Did pytest actually collect and run tests, or did it fall over first?"""
    return bool(SUMMARY.search(stdout))


def _run(cwd: Path) -> tuple[bool, str]:
    proc = subprocess.run(
        [sys.executable, "-m", "pytest", "tests/", "-q", "-x", "--no-header"],
        cwd=cwd,
        capture_output=True,
        text=True,
    )
    return proc.returncode == 0, proc.stdout + proc.stderr


def main() -> int:
    print(f"mutation testing: {len(MUTANTS)} mutants\n")
    killed, survived, invalid = 0, [], []

    for mutant in MUTANTS:
        with tempfile.TemporaryDirectory() as tmp:
            work = Path(tmp) / "work"
            shutil.copytree(
                ROOT,
                work,
                ignore=shutil.ignore_patterns("__pycache__", ".pytest_cache", "docs", ".git"),
            )
            target = work / mutant.path
            source = target.read_text(encoding="utf-8").replace("\r\n", "\n")
            if mutant.find not in source:
                # Almost always a line-ending problem or a refactor that moved the anchor.
                # Either way the mutant tested nothing, and silently scoring it as "not
                # applicable" is how a mutation score becomes a decoration.
                invalid.append(mutant)
                print(f"  INVALID  {mutant.name}: anchor not found in {mutant.path}")
                continue
            target.write_text(source.replace(mutant.find, mutant.replace, 1), encoding="utf-8")

            passed, output = _run(work)
            if not _ran(output):
                invalid.append(mutant)
                print(f"  INVALID  {mutant.name}: the suite never ran")
                print("           " + output.strip().splitlines()[-1][:120])
            elif passed:
                survived.append(mutant)
                print(f"  SURVIVED {mutant.name} -- {mutant.breaks}")
            else:
                killed += 1
                print(f"  killed   {mutant.name}")

    total = len(MUTANTS)
    print(f"\n{killed}/{total} mutants killed")
    if invalid:
        print(f"{len(invalid)} mutants were invalid and prove nothing:")
        for m in invalid:
            print(f"  - {m.name}")
    if survived:
        print(f"{len(survived)} mutants survived -- the following are unprotected:")
        for m in survived:
            print(f"  - {m.name}: {m.breaks}")

    return 0 if killed == total else 1


if __name__ == "__main__":
    sys.exit(main())
