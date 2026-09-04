"""The report is a build artefact, and this file treats it as one.

Three properties matter and none of them are about prose:

1. **It is reproducible.** Building the report twice in the same process
   yields identical bytes. No clocks, no unordered iteration, no randomness
   that is not seeded from the corpus.
2. **It matches what is committed.** The checked-in ``docs/results.md`` is
   the output of the checked-in code, so a reader can trust that the numbers
   in the prose came from the library beside them.
3. **Every prediction was resolved.** The report DSL refuses to render an
   open prediction; this asserts the counts a reader can verify by eye.

Property 2 is the one that fails loudly during development, and that is the
point: a library change that moves a number *must* force the report to be
regenerated and re-read, because the prose around that number may no longer
be true. Several sections in this report have had their argument replaced
after exactly that failure.
"""

import hashlib
import pathlib
import re

import pytest

import run_redteam

REPORT = pathlib.Path(__file__).resolve().parents[1] / "docs" / "results.md"

EXPECTED_SHA = "852f3aef4728569a"
EXPECTED_PREDICTIONS = 9
EXPECTED_HELD = 7
EXPECTED_CONTRADICTED = 2


@pytest.fixture(scope="module")
def rendered():
    return run_redteam.build_report().render()


@pytest.fixture(scope="module")
def committed():
    return REPORT.read_text(encoding="utf-8")


def short_sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]


class TestReproducibility:
    def test_two_builds_agree(self, rendered):
        assert run_redteam.build_report().render() == rendered

    def test_the_corpus_digest_is_stable(self):
        from redteam.corpus import load_corpus
        assert load_corpus().digest == load_corpus().digest

    def test_no_timestamp_leaks_into_the_report(self, committed):
        assert not re.search(r"\b20\d\d-\d\d-\d\d[ T]\d\d:", committed)


class TestCommittedArtefact:
    def test_the_report_exists(self):
        assert REPORT.is_file()

    def test_the_committed_report_matches_the_code(self, rendered, committed):
        assert short_sha(committed) == short_sha(rendered), (
            "docs/results.md is stale: re-run `python run_redteam.py`, then "
            "re-read every section whose numbers moved before updating "
            "EXPECTED_SHA")

    def test_the_hash_is_the_pinned_one(self, committed):
        assert short_sha(committed) == EXPECTED_SHA


class TestPredictions:
    def test_prediction_count(self):
        assert run_redteam.build_report().prediction_count == (
            EXPECTED_PREDICTIONS)

    def test_held_count(self):
        assert run_redteam.build_report().held_count == EXPECTED_HELD

    def test_contradicted_count(self):
        assert run_redteam.build_report().contradicted_count == (
            EXPECTED_CONTRADICTED)

    def test_the_counts_are_exhaustive(self):
        report = run_redteam.build_report()
        assert (report.held_count + report.contradicted_count
                == report.prediction_count)

    def test_every_prediction_has_a_result(self, committed):
        assert committed.count("**Predicted.**") == EXPECTED_PREDICTIONS
        assert committed.count("**Found") == EXPECTED_PREDICTIONS

    def test_the_contradictions_are_labelled_in_the_prose(self, committed):
        assert committed.count("prediction wrong") == EXPECTED_CONTRADICTED


class TestStructure:
    def test_all_twelve_sections_are_present(self, committed):
        headings = re.findall(r"^## (\d+)\.", committed, re.M)
        assert [int(h) for h in headings] == list(range(1, 13))

    def test_no_placeholder_text_survived(self, committed):
        for marker in ("TODO", "TBD", "FIXME", "XXX", "lorem"):
            assert marker not in committed

    def test_no_format_specifier_leaked(self, committed):
        assert not re.search(r"\{[a-z_]+(?::[^}]*)?\}", committed)

    def test_percentages_are_rendered_to_one_decimal(self, committed):
        bad = re.findall(r"\d+\.\d{3,}%", committed)
        assert not bad, bad

    def test_the_report_is_pure_ascii_apart_from_dashes(self, committed):
        exotic = {c for c in committed if ord(c) > 127} - {"\u2014"}
        assert not exotic

    def test_every_table_row_has_a_consistent_column_count(self, committed):
        table = []
        for line in committed.splitlines() + [""]:
            if line.startswith("|"):
                table.append(line.count("|"))
                continue
            if table:
                assert len(set(table)) == 1, table
                table = []

