package report

import (
	"bytes"
	"math"
	"strconv"
	"strings"
	"testing"
)

func render(f func(*W)) string {
	var b bytes.Buffer
	f(New(&b))
	return b.String()
}

func TestPercentileEndpointsAndInterior(t *testing.T) {
	xs := []float64{5, 1, 4, 2, 3}
	if got := Percentile(xs, 0); got != 1 {
		t.Errorf("p0 = %v, want the minimum 1", got)
	}
	if got := Percentile(xs, 1); got != 5 {
		t.Errorf("p100 = %v, want the maximum 5", got)
	}
	if got := Percentile(xs, 0.5); got != 3 {
		t.Errorf("p50 = %v, want 3", got)
	}
}

func TestPercentileDoesNotMutateItsInput(t *testing.T) {
	xs := []float64{5, 1, 4, 2, 3}
	Percentile(xs, 0.5)
	want := []float64{5, 1, 4, 2, 3}
	for i := range want {
		if xs[i] != want[i] {
			t.Fatalf("Percentile sorted its caller's slice: %v", xs)
		}
	}
}

func TestPercentileIsMonotone(t *testing.T) {
	xs := []float64{9, 3, 7, 1, 8, 2, 6, 4, 5}
	prev := math.Inf(-1)
	for p := 0.0; p <= 1.0; p += 0.05 {
		v := Percentile(xs, p)
		if v < prev {
			t.Fatalf("p=%.2f gave %v after %v", p, v, prev)
		}
		prev = v
	}
}

func TestPercentileOfEmptyIsZero(t *testing.T) {
	if got := Percentile(nil, 0.5); got != 0 {
		t.Errorf("Percentile(nil) = %v", got)
	}
}

func TestSummariseMatchesTheData(t *testing.T) {
	xs := []float64{1, 2, 3, 4, 5, 6, 7, 8, 9, 10}
	d := Summarise("x", xs)
	if d.Name != "x" || d.N != 10 {
		t.Errorf("got %+v", d)
	}
	if d.Min != 1 || d.Max != 10 {
		t.Errorf("min/max = %v/%v, want 1/10", d.Min, d.Max)
	}
	if math.Abs(d.Mean-5.5) > 1e-9 {
		t.Errorf("mean = %v, want 5.5", d.Mean)
	}
	if d.P50 < d.Min || d.P50 > d.Max {
		t.Errorf("p50 %v outside [%v,%v]", d.P50, d.Min, d.Max)
	}
}

func TestSummariseHandlesEmpty(t *testing.T) {
	d := Summarise("empty", nil)
	if d.N != 0 {
		t.Errorf("N = %d", d.N)
	}
	if len(d.Row()) != len(DistHeader()) {
		t.Errorf("Row() has %d cells but the header has %d",
			len(d.Row()), len(DistHeader()))
	}
}

// Overlap is the measurement section 2 rests on: pick the threshold that keeps
// a given share of A, then report how much of B it lets through.
func TestOverlapOnDisjointPopulations(t *testing.T) {
	a := []float64{0.9, 0.91, 0.92, 0.93}
	b := []float64{0.1, 0.11, 0.12, 0.13}
	_, admitted := Overlap(a, b, 1.0)
	if admitted != 0 {
		t.Errorf("cleanly separated populations admitted %v of B", admitted)
	}
}

func TestOverlapOnIdenticalPopulations(t *testing.T) {
	a := []float64{0.5, 0.6, 0.7, 0.8}
	b := []float64{0.5, 0.6, 0.7, 0.8}
	_, admitted := Overlap(a, b, 1.0)
	if admitted < 0.99 {
		t.Errorf("identical populations admitted only %v of B; a threshold keeping "+
			"all of A must keep all of an identical B", admitted)
	}
}

func TestOverlapThresholdKeepsTheRequestedShareOfA(t *testing.T) {
	a := []float64{0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0}
	b := []float64{0.05, 0.15, 0.25}
	thr, _ := Overlap(a, b, 0.5)
	kept := 0
	for _, x := range a {
		if x >= thr {
			kept++
		}
	}
	if kept < 4 || kept > 6 {
		t.Errorf("threshold %v kept %d/10 of A, wanted about half", thr, kept)
	}
}

func TestOverlapHandlesEmptyInput(t *testing.T) {
	if thr, adm := Overlap(nil, nil, 0.5); thr != 0 || adm != 0 {
		t.Errorf("Overlap(nil,nil) = %v, %v", thr, adm)
	}
}

func TestTableColumnsAlignToTheWidestCell(t *testing.T) {
	out := render(func(w *W) {
		w.Table([]string{"a", "bbbbbbbb"}, [][]string{{"cccccccccc", "d"}})
	})
	lines := strings.Split(strings.TrimSpace(out), "\n")
	if len(lines) != 3 {
		t.Fatalf("expected header, rule and one row; got %d lines:\n%s", len(lines), out)
	}
	n := len(lines[0])
	for i, l := range lines {
		if len(l) != n {
			t.Errorf("line %d has width %d, header has %d:\n%s", i, len(l), n, out)
		}
	}
	if !strings.Contains(lines[1], "---") {
		t.Errorf("second line is not a markdown rule: %q", lines[1])
	}
}

func TestTableTolerantOfRaggedRows(t *testing.T) {
	// A ragged row must not panic; a report that crashes halfway through is
	// worse than one with a blank cell.
	out := render(func(w *W) {
		w.Table([]string{"a", "b", "c"}, [][]string{{"1"}, {"1", "2", "3", "4"}})
	})
	if out == "" {
		t.Error("ragged rows produced no output")
	}
}

func TestPctAndF3Formatting(t *testing.T) {
	cases := []struct{ got, want string }{
		{Pct(0.5), "50.0%"},
		{Pct(1), "100.0%"},
		{Pct2(0.0331), "3.31%"},
		{F3(0.6666), "0.667"},
		{N(42), "42"},
	}
	for _, c := range cases {
		if c.got != c.want {
			t.Errorf("got %q, want %q", c.got, c.want)
		}
	}
}

func TestExpectAndFoundAreVisuallyDistinct(t *testing.T) {
	e := render(func(w *W) { w.Expect("a prediction") })
	f := render(func(w *W) { w.Found("a result") })
	if e == f {
		t.Fatal("Expect and Found render identically; the report's whole discipline " +
			"is that a reader can tell a prediction from a result")
	}
	if !strings.Contains(e, "Expected") {
		t.Errorf("Expect output does not label itself: %q", e)
	}
	if !strings.Contains(f, "Found") {
		t.Errorf("Found output does not label itself: %q", f)
	}
}

func TestSectionAndTitleUseMarkdownHeadings(t *testing.T) {
	if out := render(func(w *W) { w.Title("T") }); !strings.HasPrefix(out, "# T") {
		t.Errorf("Title rendered %q", out)
	}
	if out := render(func(w *W) { w.Section(3, "S") }); !strings.Contains(out, "## 3. S") {
		t.Errorf("Section rendered %q", out)
	}
	if out := render(func(w *W) { w.Sub("S") }); !strings.Contains(out, "### S") {
		t.Errorf("Sub rendered %q", out)
	}
}

func TestCodeIsFenced(t *testing.T) {
	out := render(func(w *W) { w.Code("x := 1") })
	if strings.Count(out, "```") != 2 {
		t.Errorf("Code did not produce a fenced block: %q", out)
	}
}

func TestBarFillsProportionallyAndAlwaysOccupiesItsWidth(t *testing.T) {
	// Bar pads with '.' so columns stay aligned; the signal is the '#' count.
	fill := func(s string) int { return strings.Count(s, "#") }

	if got := Bar(0, 10, 20); fill(got) != 0 || len([]rune(got)) != 20 {
		t.Errorf("Bar(0,10,20) = %q; want 0 hashes in a 20-wide field", got)
	}
	if got := Bar(10, 10, 20); fill(got) != 20 {
		t.Errorf("Bar at max has %d hashes, want 20", fill(got))
	}
	if got := Bar(5, 10, 20); fill(got) < 9 || fill(got) > 11 {
		t.Errorf("Bar at half has %d hashes, want about 10", fill(got))
	}
	// A value above the maximum must not overflow the column.
	if got := Bar(100, 10, 20); len([]rune(got)) != 20 {
		t.Errorf("Bar overflowed its width: %d runes", len([]rune(got)))
	}
}

func TestBarDegeneratesSafely(t *testing.T) {
	if got := Bar(1, 0, 20); got != "" {
		t.Errorf("Bar with max=0 returned %q, want empty", got)
	}
	if got := Bar(1, 10, 0); got != "" {
		t.Errorf("Bar with width=0 returned %q, want empty", got)
	}
}

func TestHistogramCountsEveryValueExactlyOnce(t *testing.T) {
	xs := []float64{0.05, 0.15, 0.25, 0.35, 0.45, 0.55, 0.65, 0.75, 0.85, 0.95}
	out := Histogram(xs, 0, 1, 10, 20)
	total := 0
	lines := strings.Split(strings.TrimSpace(out), "\n")
	if len(lines) != 10 {
		t.Fatalf("10 bins produced %d lines:\n%s", len(lines), out)
	}
	for _, l := range lines {
		f := strings.Fields(l)
		n, err := strconv.Atoi(f[len(f)-1])
		if err != nil {
			t.Fatalf("could not read the count from %q", l)
		}
		total += n
	}
	if total != len(xs) {
		t.Errorf("the histogram accounts for %d values, but %d were given", total, len(xs))
	}
}

func TestHistogramClampsOutOfRangeValues(t *testing.T) {
	// Values outside [lo,hi] must land in the end bins rather than be dropped,
	// or a distribution plot would silently lose its tails.
	out := Histogram([]float64{-5, 0.5, 99}, 0, 1, 4, 10)
	total := 0
	for _, l := range strings.Split(strings.TrimSpace(out), "\n") {
		f := strings.Fields(l)
		n, _ := strconv.Atoi(f[len(f)-1])
		total += n
	}
	if total != 3 {
		t.Errorf("out-of-range values were dropped: counted %d of 3", total)
	}
}

func TestHistogramHandlesEmptyAndDegenerateRanges(t *testing.T) {
	if out := Histogram(nil, 0, 1, 10, 20); out != "" {
		t.Errorf("Histogram(nil) returned %q, want empty", out)
	}
	if out := Histogram([]float64{1, 2}, 0, 1, 0, 20); out != "" {
		t.Errorf("zero bins returned %q, want empty", out)
	}
	// lo == hi divides by zero internally. It must not panic.
	_ = Histogram([]float64{1, 1, 1}, 1, 1, 5, 10)
}

func TestWriterFansOutToEverySink(t *testing.T) {
	var a, b bytes.Buffer
	w := New(&a, &b)
	w.Line("hello")
	if a.String() != b.String() || a.String() == "" {
		t.Errorf("sinks disagree: %q vs %q", a.String(), b.String())
	}
}
