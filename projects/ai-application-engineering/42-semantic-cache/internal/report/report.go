// Package report renders the experiment as text that a reader can check.
//
// The house style has one rule that matters: every section states what it
// EXPECTS before it prints what it found. A number with no prior attached to it
// cannot surprise you, and a result that cannot surprise you is not evidence of
// anything. Three of this project's findings were caught because the printed
// number disagreed with the prediction written above it.
package report

import (
	"fmt"
	"io"
	"math"
	"sort"
	"strings"
)

// W accumulates the report and writes it to every sink.
type W struct {
	sinks []io.Writer
}

// New builds a writer over the given sinks.
func New(sinks ...io.Writer) *W { return &W{sinks: sinks} }

func (w *W) raw(s string) {
	for _, sk := range w.sinks {
		io.WriteString(sk, s)
	}
}

// Printf writes a formatted line.
func (w *W) Printf(format string, a ...any) { w.raw(fmt.Sprintf(format, a...)) }

// Line writes one line.
func (w *W) Line(s string) { w.raw(s + "\n") }

// Blank writes an empty line.
func (w *W) Blank() { w.raw("\n") }

// Title writes the document title.
func (w *W) Title(s string) { w.raw("# " + s + "\n\n") }

// Section opens a numbered section.
func (w *W) Section(n int, title string) {
	w.raw(fmt.Sprintf("\n## %d. %s\n\n", n, title))
}

// Sub opens a subsection.
func (w *W) Sub(title string) { w.raw("\n### " + title + "\n\n") }

// Expect records the prediction for the section about to run. It is written
// before the numbers, so it cannot be edited to match them afterwards without
// that being a deliberate act.
func (w *W) Expect(lines ...string) {
	w.raw("> **Expected.** " + strings.Join(lines, " ") + "\n\n")
}

// Found records the verdict, including when the prediction was wrong.
func (w *W) Found(lines ...string) {
	w.raw("\n**Found.** " + strings.Join(lines, " ") + "\n\n")
}

// Note writes a plain paragraph.
func (w *W) Note(lines ...string) {
	w.raw(strings.Join(lines, " ") + "\n\n")
}

// Table renders a markdown table with right-aligned numeric columns.
func (w *W) Table(header []string, rows [][]string) {
	if len(header) == 0 {
		return
	}
	widths := make([]int, len(header))
	for i, h := range header {
		widths[i] = len(h)
	}
	for _, r := range rows {
		for i := range header {
			if i < len(r) && len(r[i]) > widths[i] {
				widths[i] = len(r[i])
			}
		}
	}
	var b strings.Builder
	b.WriteString("|")
	for i, h := range header {
		fmt.Fprintf(&b, " %-*s |", widths[i], h)
	}
	b.WriteString("\n|")
	for i := range header {
		b.WriteString(strings.Repeat("-", widths[i]+2))
		b.WriteString("|")
	}
	b.WriteString("\n")
	for _, r := range rows {
		b.WriteString("|")
		for i := range header {
			cell := ""
			if i < len(r) {
				cell = r[i]
			}
			fmt.Fprintf(&b, " %-*s |", widths[i], cell)
		}
		b.WriteString("\n")
	}
	w.raw(b.String())
	w.raw("\n")
}

// Code wraps text in a fenced block.
func (w *W) Code(body string) {
	w.raw("```\n" + strings.TrimRight(body, "\n") + "\n```\n\n")
}

// --- numeric helpers -------------------------------------------------------

// Pct formats a fraction as a percentage.
func Pct(x float64) string { return fmt.Sprintf("%.1f%%", 100*x) }

// Pct2 formats a fraction as a percentage with two decimals, for rates that
// are small enough that one decimal rounds them to zero.
func Pct2(x float64) string { return fmt.Sprintf("%.2f%%", 100*x) }

// F3 formats a similarity.
func F3(x float64) string { return fmt.Sprintf("%.3f", x) }

// N formats an integer.
func N(i int) string { return fmt.Sprintf("%d", i) }

// Percentile returns the p-th percentile of xs (0..1) using nearest-rank.
//
// It copies before sorting. The first version sorted in place, which is a
// silent aliasing bug: section 2 hands the same slice to Summarise, then to
// Overlap, then plots it, and an in-place sort turns the caller's data into
// something it did not ask for. A unit test asserting the caller's slice is
// unchanged found it. Empty input returns 0, not NaN - a NaN here propagates
// straight into a report table and renders as "NaN" beside real numbers.
func Percentile(xs []float64, p float64) float64 {
	if len(xs) == 0 {
		return 0
	}
	s := append([]float64(nil), xs...)
	sort.Float64s(s)
	i := int(p * float64(len(s)-1))
	if i < 0 {
		i = 0
	}
	if i >= len(s) {
		i = len(s) - 1
	}
	return s[i]
}

// Dist is a summary of a score distribution.
type Dist struct {
	Name                    string
	N                       int
	P05, P25, P50, P75, P95 float64
	Min, Max, Mean          float64
}

// Summarise computes a Dist without disturbing the caller's slice.
func Summarise(name string, xs []float64) Dist {
	d := Dist{Name: name, N: len(xs)}
	if len(xs) == 0 {
		return d
	}
	s := append([]float64(nil), xs...)
	sort.Float64s(s)
	sum := 0.0
	for _, x := range s {
		sum += x
	}
	d.Mean = sum / float64(len(s))
	d.Min, d.Max = s[0], s[len(s)-1]
	d.P05 = Percentile(s, 0.05)
	d.P25 = Percentile(s, 0.25)
	d.P50 = Percentile(s, 0.50)
	d.P75 = Percentile(s, 0.75)
	d.P95 = Percentile(s, 0.95)
	return d
}

// Row renders a Dist as a table row.
func (d Dist) Row() []string {
	return []string{d.Name, N(d.N), F3(d.Min), F3(d.P05), F3(d.P25), F3(d.P50),
		F3(d.P75), F3(d.P95), F3(d.Max)}
}

// DistHeader is the header for Dist rows.
func DistHeader() []string {
	return []string{"population", "n", "min", "p05", "p25", "p50", "p75", "p95", "max"}
}

// Overlap is the fraction of population b that scores at or above the given
// percentile of population a. It is the honest way to say "no threshold
// separates these": if you set the threshold to admit 90% of a, this is how
// much of b you also admit.
func Overlap(a, b []float64, keepA float64) (thr, admittedB float64) {
	if len(a) == 0 || len(b) == 0 {
		return 0, 0
	}
	thr = Percentile(a, 1-keepA)
	n := 0
	for _, x := range b {
		if x >= thr {
			n++
		}
	}
	return thr, float64(n) / float64(len(b))
}

// Bar renders a proportional ASCII bar, for eyeballing a sweep.
func Bar(x, max float64, width int) string {
	if max <= 0 || width <= 0 {
		return ""
	}
	n := int(math.Round(x / max * float64(width)))
	if n < 0 {
		n = 0
	}
	if n > width {
		n = width
	}
	return strings.Repeat("#", n) + strings.Repeat(".", width-n)
}

// Histogram renders a fixed-range histogram of xs over [lo,hi].
func Histogram(xs []float64, lo, hi float64, bins, width int) string {
	if bins <= 0 || len(xs) == 0 {
		return ""
	}
	counts := make([]int, bins)
	for _, x := range xs {
		i := int((x - lo) / (hi - lo) * float64(bins))
		if i < 0 {
			i = 0
		}
		if i >= bins {
			i = bins - 1
		}
		counts[i]++
	}
	maxC := 0
	for _, c := range counts {
		if c > maxC {
			maxC = c
		}
	}
	var b strings.Builder
	for i, c := range counts {
		edge := lo + (hi-lo)*float64(i)/float64(bins)
		fmt.Fprintf(&b, "%6.2f %s %6d\n", edge, Bar(float64(c), float64(maxC), width), c)
	}
	return b.String()
}
