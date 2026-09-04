"""The report is a build artefact, and it is checked like one.

``docs/results.md`` is the deliverable. Every number in it comes from a run of
the model, which means the file is only trustworthy if regenerating it
produces the same bytes. These tests regenerate it into a temporary directory
and compare.

If a change to the model moves a number, this test fails and the fix is to
regenerate the report -- not to loosen the check. That is the point.
"""

from __future__ import annotations

import hashlib
import re
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
RESULTS = ROOT / "docs" / "results.md"

#: sha256 of the committed report, truncated to 16 hex characters. Update this
#: in the same commit that changes the model, and never on its own.
EXPECTED_SHA = "aa401bec743fa7fd"


def sha(text: str) -> str:
    return hashlib.sha256(text.encode()).hexdigest()[:16]


@pytest.fixture(scope="module")
def committed() -> str:
    return RESULTS.read_text(encoding="utf-8")


@pytest.fixture(scope="module")
def regenerated(tmp_path_factory) -> str:
    out = tmp_path_factory.mktemp("report") / "results.md"
    proc = subprocess.run(
        [sys.executable, "run_planner.py", "--out", str(out)],
        cwd=ROOT,
        capture_output=True,
        text=True,
        timeout=900,
    )
    assert proc.returncode == 0, proc.stderr
    return out.read_text(encoding="utf-8")


class TestCommittedReport:
    def test_it_exists(self, committed):
        assert committed.strip()

    def test_hash_matches(self, committed):
        assert sha(committed) == EXPECTED_SHA

    def test_every_prediction_has_a_finding(self, committed):
        assert committed.count("**Predicted.**") == committed.count("**Found")

    def test_the_tally_matches_the_body(self, committed):
        m = re.search(
            r"(\d+) predictions were written .*?"
            r"(\d+) held; (\d+) did not",
            committed,
            re.S,
        )
        assert m, "report tail not found"
        total, held, wrong = (int(g) for g in m.groups())
        assert total == committed.count("**Predicted.**")
        assert wrong == committed.count("**Found — prediction wrong.**")
        assert held + wrong == total

    def test_contradictions_are_reported(self, committed):
        """A report where every prediction holds is a report that predicted
        nothing worth predicting."""
        assert committed.count("**Found — prediction wrong.**") >= 3

    def test_no_placeholder_text_survived(self, committed):
        for bad in ("TODO", "FIXME", "XXX", "TBD", "lorem"):
            assert bad not in committed

    def test_no_unformatted_floats(self, committed):
        """Catches an f-string that interpolated a raw float."""
        assert not re.search(r"\d\.\d{8,}", committed)

    def test_no_python_repr_leaked(self, committed):
        for bad in ("dtype=", "np.float", "<wave.", "object at 0x"):
            assert bad not in committed

    def test_currency_is_formatted(self, committed):
        assert "£" in committed
        assert not re.search(r"£\d+\.\d{4}", committed)

    def test_all_sections_present(self, committed):
        for n in range(0, 12):
            assert re.search(rf"^## {n}\. ", committed, re.M), f"section {n} missing"


@pytest.mark.slow
class TestReproducibility:
    def test_regenerating_gives_identical_bytes(self, committed, regenerated):
        assert sha(regenerated) == sha(committed)

    def test_regenerated_matches_the_recorded_hash(self, regenerated):
        assert sha(regenerated) == EXPECTED_SHA
