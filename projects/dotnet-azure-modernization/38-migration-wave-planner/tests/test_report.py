"""The report DSL, and the discipline it enforces.

Every number in ``docs/results.md`` is produced by a run of the model rather
than typed into prose, and every measurement has to be preceded by a written
prediction. That is not decoration: five factual errors in this project were
caught because a hardcoded sentence went stale while the number next to it
moved. These tests pin the enforcement so it cannot quietly stop working.
"""

from __future__ import annotations

import pytest

from wave.report import Report, _wrap, num, pct, signed


def r() -> Report:
    return Report(title="T")


class TestPredictionDiscipline:
    def test_a_measurement_needs_a_prediction(self):
        with pytest.raises(AssertionError, match="measurement with no prediction"):
            r().found("something")

    def test_a_prediction_cannot_be_left_open(self):
        rep = r()
        rep.expect("a")
        with pytest.raises(AssertionError, match="prediction still open"):
            rep.expect("b")

    def test_render_refuses_an_open_prediction(self):
        rep = r()
        rep.expect("a")
        with pytest.raises(AssertionError, match="open prediction"):
            rep.render()

    def test_the_error_names_the_open_prediction(self):
        rep = r()
        rep.expect("the annealer will win")
        with pytest.raises(AssertionError, match="annealer will win"):
            rep.render()

    def test_a_closed_prediction_lets_the_next_one_open(self):
        rep = r()
        rep.expect("a")
        rep.found("b")
        rep.expect("c")
        rep.found("d")
        assert rep.n_predictions == 2

    def test_counters_start_at_zero(self):
        rep = r()
        assert (rep.n_predictions, rep.n_confirmed, rep.n_contradicted) == (0, 0, 0)

    def test_confirmed_increments(self):
        rep = r()
        rep.expect("a")
        rep.found("b")
        assert (rep.n_confirmed, rep.n_contradicted) == (1, 0)

    def test_contradicted_increments(self):
        rep = r()
        rep.expect("a")
        rep.found("b", contradicted=True)
        assert (rep.n_confirmed, rep.n_contradicted) == (0, 1)

    def test_confirmed_plus_contradicted_equals_predictions(self):
        rep = r()
        for i in range(5):
            rep.expect(f"p{i}")
            rep.found(f"f{i}", contradicted=i % 2 == 0)
        assert rep.n_confirmed + rep.n_contradicted == rep.n_predictions

    def test_a_contradiction_is_labelled_in_the_output(self):
        rep = r()
        rep.expect("a")
        rep.found("b", contradicted=True)
        assert "prediction wrong" in rep.render()

    def test_a_confirmation_is_not_labelled_as_wrong(self):
        rep = r()
        rep.expect("a")
        rep.found("b")
        assert "prediction wrong" not in rep.render()

    def test_the_tail_reports_the_tally(self):
        rep = r()
        rep.expect("a")
        rep.found("b", contradicted=True)
        tail = rep.render()
        assert "1 predictions" in tail
        assert "0 held" in tail
        assert "1 did not" in tail


class TestStructure:
    def test_title_is_the_first_heading(self):
        assert r().render().startswith("# T\n")

    def test_h2_and_h3(self):
        rep = r()
        rep.h2("two")
        rep.h3("three")
        out = rep.render()
        assert "## two" in out and "### three" in out

    def test_bullets(self):
        rep = r()
        rep.bullets(["one", "two"])
        assert "- one" in rep.render()

    def test_note_is_a_blockquote(self):
        rep = r()
        rep.note("careful")
        assert "> careful" in rep.render()

    def test_code_fences_carry_the_language(self):
        rep = r()
        rep.code("x = 1", "python")
        assert "```python\nx = 1\n```" in rep.render()

    def test_a_found_block_can_hold_several_paragraphs(self):
        rep = r()
        rep.expect("a")
        rep.found("first\n\nsecond")
        out = rep.render()
        assert "first" in out and "second" in out


class TestTable:
    def test_columns_are_padded_to_the_widest_cell(self):
        rep = r()
        rep.table(["h", "hh"], [["longer", "x"]])
        rows = [l for l in rep.render().splitlines() if l.startswith("|")]
        assert len({len(l) for l in rows}) == 1

    def test_separator_row_is_present(self):
        rep = r()
        rep.table(["a"], [["b"]])
        assert any(
            set(l) <= {"|", "-"} for l in rep.render().splitlines() if l.startswith("|")
        )

    def test_every_row_appears(self):
        rep = r()
        rep.table(["a"], [["one"], ["two"], ["three"]])
        out = rep.render()
        assert all(v in out for v in ("one", "two", "three"))

    def test_headers_appear(self):
        rep = r()
        rep.table(["alpha", "beta"], [["1", "2"]])
        assert "alpha" in rep.render() and "beta" in rep.render()

    def test_a_table_with_no_rows_still_renders_a_header(self):
        rep = r()
        rep.table(["a", "b"], [])
        assert "| a | b |" in rep.render()


class TestWrap:
    def test_short_text_is_untouched(self):
        assert _wrap("hello there") == "hello there"

    def test_long_text_is_broken(self):
        assert "\n" in _wrap("word " * 40)

    def test_no_line_exceeds_the_width(self):
        for line in _wrap("word " * 60, width=40).splitlines():
            assert len(line) <= 40

    def test_a_single_long_word_is_not_broken(self):
        long = "x" * 120
        assert _wrap(long) == long

    def test_whitespace_is_normalised(self):
        assert _wrap("a   b\n\tc") == "a b c"

    def test_no_words_are_lost(self):
        text = " ".join(f"w{i}" for i in range(200))
        assert _wrap(text, width=30).split() == text.split()

    def test_deterministic(self):
        text = "the quick brown fox " * 20
        assert _wrap(text) == _wrap(text)

    def test_indent_is_applied_to_every_line(self):
        for line in _wrap("word " * 40, width=40, indent=4).splitlines():
            assert line.startswith("    ")

    def test_prefix_is_applied_to_every_line(self):
        for line in _wrap("word " * 40, width=40, prefix="> ").splitlines():
            assert line.startswith("> ")

    def test_empty_text(self):
        assert _wrap("") == ""


class TestFormatting:
    def test_pct_scales_by_a_hundred(self):
        assert pct(0.1234) == "12.3%"

    def test_pct_places(self):
        assert pct(0.1234, 2) == "12.34%"

    def test_num_places(self):
        assert num(1.23456, 2) == "1.23"

    def test_num_default_places(self):
        assert num(1.0) == "1.000"

    def test_num_suppresses_negative_zero(self):
        """A difference that is exactly zero must not render as "-0.000".

        It reads as a small negative effect, and in a report whose whole point
        is that the sign of an effect is the finding, that is not cosmetic.
        """
        assert num(-0.0001, 2) == "0.00"
        assert not num(-1e-12).startswith("-")

    def test_num_keeps_a_real_negative(self):
        assert num(-1.5, 1) == "-1.5"

    def test_signed_marks_positives(self):
        assert signed(1.5, 1) == "+1.5"

    def test_signed_keeps_negatives(self):
        assert signed(-1.5, 1) == "-1.5"

    def test_signed_zero_is_positive_not_negative(self):
        assert signed(-0.0001, 2) == "+0.00"


class TestDeterminism:
    def build(self) -> str:
        rep = Report(title="Determinism")
        rep.h2("section")
        rep.expect("this will hold")
        rep.table(["a", "b"], [["1", "2"], ["30", "40"]])
        rep.found("it held")
        rep.bullets(["x", "y"])
        rep.note("aside")
        rep.code("print(1)", "python")
        return rep.render()

    def test_same_input_same_bytes(self):
        assert self.build() == self.build()

    def test_render_is_repeatable_on_one_instance(self):
        rep = Report(title="T")
        rep.h2("s")
        assert rep.render() == rep.render()
