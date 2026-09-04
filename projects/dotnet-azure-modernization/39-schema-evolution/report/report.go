// Package report is the expect/found harness.
//
// A section of the report declares what it expects to happen *before* it
// measures. The measurement then either holds or contradicts the prediction,
// and the renderer refuses to emit a section with an open prediction.
//
// This exists because the failure mode of a self-authored engineering report
// is that the author writes the conclusion after seeing the number, and the
// report becomes a description of the output rather than a test of a belief.
// Making the prediction a required, ordered, machine-checked field is the
// cheapest way to stop that.
package report

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"math"
	"sort"
	"strings"
)

// Status of a prediction after measurement.
type Status int

const (
	Open Status = iota
	Held
	Contradicted
)

func (s Status) String() string {
	switch s {
	case Held:
		return "HELD"
	case Contradicted:
		return "CONTRADICTED"
	}
	return "OPEN"
}

// Finding is one expect/found pair.
type Finding struct {
	Section string
	Expect  string
	Found   string
	Status  Status
}

// Section is a chunk of the report.
type Section struct {
	Num   int
	Title string
	body  []string
	pred  *Finding
	// closed is set once found() has been called for the open prediction.
	closed bool
}

// Report accumulates sections.
type Report struct {
	Title    string
	Preamble []string
	sections []*Section
}

// New starts a report.
func New(title string) *Report { return &Report{Title: title} }

// Intro adds a paragraph before the first section.
func (r *Report) Intro(s string) { r.Preamble = append(r.Preamble, s) }

// Section opens a new numbered section.
func (r *Report) Section(title string) *Section {
	if n := len(r.sections); n > 0 && r.sections[n-1].pred != nil && !r.sections[n-1].closed {
		panic(fmt.Sprintf("section %q left a prediction open", r.sections[n-1].Title))
	}
	s := &Section{Num: len(r.sections) + 1, Title: title}
	r.sections = append(r.sections, s)
	return s
}

// Text adds a paragraph.
func (s *Section) Text(format string, args ...any) *Section {
	s.body = append(s.body, "P"+fmt.Sprintf(format, args...))
	return s
}

// Expect records the prediction. It must be called before Found.
func (s *Section) Expect(format string, args ...any) *Section {
	if s.pred != nil {
		panic(fmt.Sprintf("section %q already has a prediction", s.Title))
	}
	s.pred = &Finding{Section: s.Title, Expect: fmt.Sprintf(format, args...)}
	s.body = append(s.body, "E"+s.pred.Expect)
	return s
}

// Found records the measurement and whether the prediction held.
func (s *Section) Found(held bool, format string, args ...any) *Section {
	if s.pred == nil {
		panic(fmt.Sprintf("section %q recorded a finding with no prediction", s.Title))
	}
	if s.closed {
		panic(fmt.Sprintf("section %q closed its prediction twice", s.Title))
	}
	s.pred.Found = fmt.Sprintf(format, args...)
	if held {
		s.pred.Status = Held
	} else {
		s.pred.Status = Contradicted
	}
	s.closed = true
	s.body = append(s.body, "F"+s.pred.Found)
	return s
}

// Table adds a markdown table. Columns are left-aligned; the caller supplies
// pre-formatted cells so numeric formatting stays with the measurement.
func (s *Section) Table(head []string, rows [][]string) *Section {
	var b strings.Builder
	b.WriteString("| " + strings.Join(head, " | ") + " |\n")
	seps := make([]string, len(head))
	for i := range seps {
		seps[i] = "---"
	}
	b.WriteString("| " + strings.Join(seps, " | ") + " |\n")
	for _, r := range rows {
		b.WriteString("| " + strings.Join(r, " | ") + " |\n")
	}
	s.body = append(s.body, "T"+b.String())
	return s
}

// Code adds a fenced block.
func (s *Section) Code(lang, body string) *Section {
	s.body = append(s.body, "C"+lang+"\n"+body)
	return s
}

// Findings returns every prediction in order.
func (r *Report) Findings() []Finding {
	var out []Finding
	for _, s := range r.sections {
		if s.pred != nil {
			out = append(out, *s.pred)
		}
	}
	return out
}

// Tally counts predictions by status.
func (r *Report) Tally() (total, held, contradicted int) {
	for _, f := range r.Findings() {
		total++
		switch f.Status {
		case Held:
			held++
		case Contradicted:
			contradicted++
		}
	}
	return
}

// Render produces the markdown. It refuses to render an open prediction: a
// section that predicts and never measures is the exact failure this harness
// exists to prevent.
func (r *Report) Render() (string, error) {
	for _, s := range r.sections {
		if s.pred != nil && !s.closed {
			return "", fmt.Errorf("section %d (%q) has an open prediction", s.Num, s.Title)
		}
	}
	var b strings.Builder
	b.WriteString("# " + r.Title + "\n\n")
	for _, p := range r.Preamble {
		b.WriteString(wrap(p, 78) + "\n\n")
	}

	total, held, contra := r.Tally()
	b.WriteString(fmt.Sprintf("**%d predictions recorded before measurement: %d held, %d contradicted.**\n\n",
		total, held, contra))
	b.WriteString("---\n\n")

	for _, s := range r.sections {
		b.WriteString(fmt.Sprintf("## %d. %s\n\n", s.Num, s.Title))
		for _, item := range s.body {
			kind, text := item[0], item[1:]
			switch kind {
			case 'P':
				b.WriteString(wrap(text, 78) + "\n\n")
			case 'E':
				b.WriteString("> **Expected (written first):** " + wrap(text, 74) + "\n\n")
			case 'F':
				tag := "HELD"
				if s.pred.Status == Contradicted {
					tag = "CONTRADICTED"
				}
				b.WriteString(fmt.Sprintf("> **Found — %s:** %s\n\n", tag, wrap(text, 74)))
			case 'T':
				b.WriteString(text + "\n")
			case 'C':
				nl := strings.Index(text, "\n")
				b.WriteString("```" + text[:nl] + "\n" + strings.TrimRight(text[nl+1:], "\n") + "\n```\n\n")
			}
		}
	}

	b.WriteString("---\n\n## Findings index\n\n")
	b.WriteString("| # | Section | Status |\n| --- | --- | --- |\n")
	for i, f := range r.Findings() {
		b.WriteString(fmt.Sprintf("| %d | %s | %s |\n", i+1, f.Section, f.Status))
	}
	b.WriteString("\n")
	return b.String(), nil
}

// wrap hard-wraps at a column, hand-rolled so the output is byte-stable
// across Go versions. It leaves table rows, list items and anything already
// containing a newline alone.
func wrap(s string, width int) string {
	if strings.Contains(s, "\n") || strings.HasPrefix(s, "|") || strings.HasPrefix(s, "- ") {
		return s
	}
	words := strings.Fields(s)
	if len(words) == 0 {
		return ""
	}
	var lines []string
	cur := words[0]
	for _, w := range words[1:] {
		if len(cur)+1+len(w) > width {
			lines = append(lines, cur)
			cur = w
		} else {
			cur += " " + w
		}
	}
	lines = append(lines, cur)
	return strings.Join(lines, "\n")
}

// Num formats a float with n decimals and suppresses signed zero, which
// otherwise turns a byte-compared artefact into a flaky one.
func Num(v float64, n int) string {
	s := fmt.Sprintf("%.*f", n, v)
	if strings.TrimLeft(s, "-0.") == "" && strings.HasPrefix(s, "-") {
		s = s[1:]
	}
	return s
}

// Pct formats a ratio as a percentage.
func Pct(v float64, n int) string { return Num(v*100, n) + "%" }

// Sha returns the first 16 hex chars of the sha256 of a string, which is what
// the integrity test pins.
func Sha(s string) string {
	sum := sha256.Sum256([]byte(s))
	return hex.EncodeToString(sum[:])[:16]
}

// Ratio guards against division by zero in report arithmetic.
func Ratio(a, b float64) float64 {
	if b == 0 {
		return math.NaN()
	}
	return a / b
}

// SortedKeys is a small helper used by sections that iterate maps, because a
// map iteration in a byte-compared artefact is a latent flake.
func SortedKeys[V any](m map[string]V) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}
