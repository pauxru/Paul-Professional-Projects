package report

import (
	"math"
	"strings"
	"testing"
)

func TestExpectBeforeFound(t *testing.T) {
	r := New("t")
	s := r.Section("a")
	defer func() {
		if recover() == nil {
			t.Fatal("recording a finding with no prediction must panic")
		}
	}()
	s.Found(true, "measured")
}

func TestDoublePredictionPanics(t *testing.T) {
	r := New("t")
	s := r.Section("a").Expect("one")
	defer func() {
		if recover() == nil {
			t.Fatal("a second prediction in one section must panic")
		}
	}()
	s.Expect("two")
}

func TestDoubleFoundPanics(t *testing.T) {
	r := New("t")
	s := r.Section("a").Expect("one").Found(true, "measured")
	defer func() {
		if recover() == nil {
			t.Fatal("closing a prediction twice must panic")
		}
	}()
	s.Found(false, "again")
}

func TestOpenPredictionBlocksTheNextSection(t *testing.T) {
	r := New("t")
	r.Section("a").Expect("something")
	defer func() {
		if recover() == nil {
			t.Fatal("opening a new section over an unclosed prediction must panic")
		}
	}()
	r.Section("b")
}

// The core guarantee. A section that predicts and never measures is exactly
// the failure this harness exists to prevent, so it must be impossible to
// render one.
func TestRenderRefusesAnOpenPrediction(t *testing.T) {
	r := New("t")
	r.Section("a").Expect("something")
	if _, err := r.Render(); err == nil {
		t.Fatal("rendered a report with an open prediction")
	}
}

func TestRenderIncludesBothSides(t *testing.T) {
	r := New("t")
	r.Section("a").
		Text("context").
		Expect("the ratio exceeds 100").
		Found(false, "it was 41.2")
	out, err := r.Render()
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{
		"Expected (written first)", "the ratio exceeds 100",
		"CONTRADICTED", "it was 41.2", "1. a",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("output missing %q", want)
		}
	}
}

func TestHeldAndContradictedTags(t *testing.T) {
	r := New("t")
	r.Section("a").Expect("x").Found(true, "y")
	r.Section("b").Expect("x").Found(false, "y")
	out, _ := r.Render()
	if !strings.Contains(out, "Found — HELD") {
		t.Error("no HELD tag")
	}
	if !strings.Contains(out, "Found — CONTRADICTED") {
		t.Error("no CONTRADICTED tag")
	}
}

func TestTally(t *testing.T) {
	r := New("t")
	r.Section("a").Expect("x").Found(true, "y")
	r.Section("b").Expect("x").Found(false, "y")
	r.Section("c").Expect("x").Found(false, "y")
	r.Section("d").Text("no prediction here")
	total, held, contra := r.Tally()
	if total != 3 || held != 1 || contra != 2 {
		t.Fatalf("tally = %d/%d/%d", total, held, contra)
	}
}

func TestSectionNumbering(t *testing.T) {
	r := New("t")
	r.Section("a").Expect("x").Found(true, "y")
	r.Section("b").Expect("x").Found(true, "y")
	r.Section("c")
	out, _ := r.Render()
	for _, want := range []string{"## 1. a", "## 2. b", "## 3. c"} {
		if !strings.Contains(out, want) {
			t.Errorf("missing heading %q", want)
		}
	}
}

func TestFindingsIndex(t *testing.T) {
	r := New("t")
	r.Section("first").Expect("x").Found(true, "y")
	r.Section("second").Expect("x").Found(false, "y")
	out, _ := r.Render()
	i := strings.Index(out, "Findings index")
	if i < 0 {
		t.Fatal("no findings index")
	}
	tail := out[i:]
	if !strings.Contains(tail, "| 1 | first | HELD |") {
		t.Errorf("index row missing:\n%s", tail)
	}
	if !strings.Contains(tail, "| 2 | second | CONTRADICTED |") {
		t.Errorf("index row missing:\n%s", tail)
	}
}

func TestByteStability(t *testing.T) {
	// The report is compared by hash between runs; anything varying here is a
	// flaky test in the making.
	build := func() string {
		r := New("Schema evolution")
		r.Intro("A long introductory paragraph that will certainly need to be wrapped by the renderer because it is comfortably wider than seventy-eight columns.")
		r.Section("one").
			Text("Some prose.").
			Table([]string{"a", "b"}, [][]string{{"1", "2"}, {"3", "4"}}).
			Code("sql", "ALTER TABLE t ADD COLUMN x int;").
			Expect("a prediction").
			Found(true, "a measurement")
		out, err := r.Render()
		if err != nil {
			t.Fatal(err)
		}
		return out
	}
	first := build()
	for i := 0; i < 50; i++ {
		if build() != first {
			t.Fatalf("run %d differs", i)
		}
	}
}

func TestWrapDoesNotMangleTables(t *testing.T) {
	long := "| a very long table row that goes well past the wrap column and must not be broken | b |"
	if wrap(long, 40) != long {
		t.Fatal("wrapped a table row")
	}
	list := "- a list item that is also comfortably longer than the wrap column and should survive"
	if wrap(list, 40) != list {
		t.Fatal("wrapped a list item")
	}
	multi := "line one\nline two"
	if wrap(multi, 5) != multi {
		t.Fatal("wrapped pre-formatted text")
	}
}

func TestWrapRespectsWidth(t *testing.T) {
	in := strings.Repeat("word ", 60)
	out := wrap(in, 40)
	for _, line := range strings.Split(out, "\n") {
		if len(line) > 40 {
			t.Fatalf("line of %d chars: %q", len(line), line)
		}
	}
	if strings.Join(strings.Fields(out), " ") != strings.TrimSpace(in) {
		t.Fatal("wrapping lost or reordered words")
	}
}

func TestWrapEmpty(t *testing.T) {
	if wrap("", 10) != "" || wrap("   ", 10) != "" {
		t.Fatal("empty input should wrap to empty")
	}
}

func TestWrapSingleOverlongWord(t *testing.T) {
	w := strings.Repeat("x", 100)
	if wrap(w, 10) != w {
		t.Fatal("an unbreakable word must be emitted whole, not truncated")
	}
}

func TestNumSuppressesSignedZero(t *testing.T) {
	// A "-0.00" in a hash-compared artefact is the classic intermittent
	// diff: it appears only when a subtraction happens to land on the
	// negative side of zero.
	if got := Num(-0.0001, 2); got != "0.00" {
		t.Fatalf("Num(-0.0001, 2) = %q", got)
	}
	if got := Num(math.Copysign(0, -1), 3); got != "0.000" {
		t.Fatalf("negative zero rendered as %q", got)
	}
	if got := Num(-1.5, 1); got != "-1.5" {
		t.Fatalf("Num ate a real negative sign: %q", got)
	}
	if got := Num(0.0, 2); got != "0.00" {
		t.Fatalf("Num(0) = %q", got)
	}
}

func TestPct(t *testing.T) {
	if got := Pct(0.1234, 1); got != "12.3%" {
		t.Fatalf("Pct = %q", got)
	}
	if got := Pct(-0.000001, 2); got != "0.00%" {
		t.Fatalf("Pct signed zero = %q", got)
	}
}

func TestRatioGuard(t *testing.T) {
	if !math.IsNaN(Ratio(1, 0)) {
		t.Fatal("Ratio(1, 0) should be NaN, not an infinity or a panic")
	}
	if Ratio(6, 3) != 2 {
		t.Fatal("Ratio is wrong")
	}
}

func TestShaIsStableAndSized(t *testing.T) {
	a := Sha("hello")
	if len(a) != 16 {
		t.Fatalf("sha length %d", len(a))
	}
	if a != Sha("hello") {
		t.Fatal("Sha is not deterministic")
	}
	if a == Sha("hellp") {
		t.Fatal("Sha collided on a one-character change")
	}
}

func TestSortedKeys(t *testing.T) {
	m := map[string]int{"c": 1, "a": 2, "b": 3}
	got := SortedKeys(m)
	if strings.Join(got, "") != "abc" {
		t.Fatalf("SortedKeys = %v", got)
	}
}

func TestTableRendering(t *testing.T) {
	r := New("t")
	r.Section("a").Table([]string{"x", "y"}, [][]string{{"1", "2"}})
	out, _ := r.Render()
	if !strings.Contains(out, "| x | y |") {
		t.Errorf("no header row:\n%s", out)
	}
	if !strings.Contains(out, "| --- | --- |") {
		t.Errorf("no separator row:\n%s", out)
	}
	if !strings.Contains(out, "| 1 | 2 |") {
		t.Errorf("no body row:\n%s", out)
	}
}

func TestCodeFence(t *testing.T) {
	r := New("t")
	r.Section("a").Code("sql", "SELECT 1;\nSELECT 2;")
	out, _ := r.Render()
	if !strings.Contains(out, "```sql\nSELECT 1;\nSELECT 2;\n```") {
		t.Errorf("code fence malformed:\n%s", out)
	}
}

func TestPredictionCountInHeader(t *testing.T) {
	r := New("t")
	r.Section("a").Expect("x").Found(true, "y")
	r.Section("b").Expect("x").Found(false, "y")
	out, _ := r.Render()
	if !strings.Contains(out, "2 predictions recorded before measurement: 1 held, 1 contradicted") {
		t.Errorf("header summary wrong:\n%s", out[:300])
	}
}

func TestSectionWithoutAPredictionRenders(t *testing.T) {
	r := New("t")
	r.Section("prose only").Text("no claim is made here")
	if _, err := r.Render(); err != nil {
		t.Fatalf("a section with no prediction should render: %v", err)
	}
}

func TestStatusStrings(t *testing.T) {
	if Open.String() != "OPEN" || Held.String() != "HELD" || Contradicted.String() != "CONTRADICTED" {
		t.Fatal("status names wrong")
	}
}
