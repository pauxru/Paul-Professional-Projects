"""The report is a build artefact, and it is checked like one.

``docs/results.md`` is the deliverable. Every number in it comes from a run of
the simulation, which means the file is only trustworthy if regenerating it
produces the same bytes. These tests regenerate it into a temporary directory
and compare.

If a change to the library moves a number, this test fails and the fix is to
regenerate the report -- not to loosen the check. That is the point. A report
whose numbers drift silently is worse than no report, because it looks like
evidence.
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
#: in the same commit that changes the library, and never on its own.
EXPECTED_SHA = "5cbb6688589b598a"


def sha(text: str) -> str:
    return hashlib.sha256(text.encode()).hexdigest()[:16]


@pytest.fixture(scope="module")
def committed() -> str:
    return RESULTS.read_text(encoding="utf-8")


@pytest.fixture(scope="module")
def regenerated(tmp_path_factory) -> str:
    out = tmp_path_factory.mktemp("report") / "results.md"
    proc = subprocess.run(
        [sys.executable, "run_eval.py", "--out", str(out)],
        cwd=ROOT, capture_output=True, text=True, timeout=1800,
    )
    assert proc.returncode == 0, proc.stderr
    return out.read_text(encoding="utf-8")


class TestCommittedReport:
    def test_it_exists(self, committed):
        assert committed.strip()

    def test_hash_matches(self, committed):
        assert sha(committed) == EXPECTED_SHA

    def test_every_prediction_has_a_finding(self, committed):
        """`**Found` rather than `**Found.**` -- a contradicted finding renders
        as `**Found -- prediction wrong.**`, and counting only the clean form
        would silently permit dropping every prediction that failed."""
        assert committed.count("**Predicted.**") == committed.count("**Found")

    def test_there_are_enough_predictions_to_be_a_test(self, committed):
        assert committed.count("**Predicted.**") >= 14

    def test_some_predictions_were_wrong(self, committed):
        """The honesty check, and the one most likely to fail quietly.

        A report where every prediction held is a report where the predictions
        were written after the measurements. Zero contradictions here would
        mean the experiment stopped being an experiment.
        """
        assert committed.count("prediction wrong") >= 2

    def test_not_every_prediction_was_wrong(self, committed):
        assert committed.count("prediction wrong") < committed.count("**Predicted.**")

    def test_no_placeholder_text_survived(self, committed):
        for marker in ("TODO", "FIXME", "TBD", "XXX", "lorem ipsum",
                       "PLACEHOLDER", "<insert", "nan%", "inf%"):
            assert marker.lower() not in committed.lower(), marker

    def test_no_unformatted_python_repr_leaked(self, committed):
        for marker in ("np.float64", "array([", "Interval(", "<Verdict.",
                       "dtype=", "0x0000"):
            assert marker not in committed, marker

    def test_tables_have_consistent_column_counts(self, committed):
        block: list[str] = []
        for line in committed.splitlines() + [""]:
            if line.startswith("|"):
                block.append(line)
                continue
            if block:
                widths = {row.count("|") for row in block}
                assert len(widths) == 1, f"ragged table near: {block[0]}"
                assert len(block) >= 3, f"table with no rows: {block[0]}"
                block = []

    def test_every_table_has_a_separator_row(self, committed):
        block: list[str] = []
        for line in committed.splitlines() + [""]:
            if line.startswith("|"):
                block.append(line)
                continue
            if block:
                assert set(block[1].replace("|", "").replace(" ", "")) <= {"-", ":"}
                block = []

    def test_percentages_are_plausible(self, committed):
        """Not capped at 100: section 6 legitimately reports 107.1% of an
        apparent gain evaporating. Capped at 1000 to catch a divide-by-tiny,
        which is the failure that actually produces garbage here."""
        for match in re.finditer(r"(\d+\.\d+)%", committed):
            assert 0.0 <= float(match.group(1)) <= 1000.0, match.group(0)

    def test_no_nan_or_inf_reached_the_report(self, committed):
        for marker in ("nan", "inf", "-inf", "NaN", "Infinity"):
            assert not re.search(rf"(?<![A-Za-z]){re.escape(marker)}(?![A-Za-z])",
                                 committed), marker

    def test_headings_are_unique(self, committed):
        heads = re.findall(r"^## (.+)$", committed, re.M)
        assert len(heads) == len(set(heads))

    def test_it_records_the_seed_and_environment(self, committed):
        assert "seed" in committed.lower()
        assert "numpy" in committed.lower()

    def test_it_discloses_that_the_systems_are_simulated(self, committed):
        """A report about LLM evaluation that does not say, prominently, that
        no LLM was called is a dishonest report regardless of its statistics.
        """
        head = committed[:6000].lower()
        assert "simulat" in head

    def test_it_links_known_limitations(self, committed):
        assert "known-limitations" in committed


class TestReproducibility:
    def test_regenerating_produces_identical_bytes(self, committed, regenerated):
        assert sha(regenerated) == sha(committed)

    def test_regenerated_report_is_substantial(self, regenerated):
        assert len(regenerated) > 20_000


class TestQuickModeIsOnlyForDevelopment:
    def test_quick_mode_runs(self, tmp_path):
        out = tmp_path / "quick.md"
        proc = subprocess.run(
            [sys.executable, "run_eval.py", "--quick", "--out", str(out)],
            cwd=ROOT, capture_output=True, text=True, timeout=900,
        )
        assert proc.returncode == 0, proc.stderr
        assert out.read_text(encoding="utf-8").strip()

    def test_quick_mode_is_labelled_as_not_the_real_thing(self, tmp_path):
        """Quick mode cuts resamples 20x. Its numbers are close but not equal,
        and a quick report that does not say so will eventually be pasted into
        a document as if it were the full one.
        """
        out = tmp_path / "quick.md"
        subprocess.run(
            [sys.executable, "run_eval.py", "--quick", "--out", str(out)],
            cwd=ROOT, capture_output=True, text=True, timeout=900, check=True,
        )
        assert "quick" in out.read_text(encoding="utf-8").lower()

    def test_quick_mode_differs_from_the_committed_report(self, committed, tmp_path):
        out = tmp_path / "quick.md"
        subprocess.run(
            [sys.executable, "run_eval.py", "--quick", "--out", str(out)],
            cwd=ROOT, capture_output=True, text=True, timeout=900, check=True,
        )
        assert sha(out.read_text(encoding="utf-8")) != EXPECTED_SHA
