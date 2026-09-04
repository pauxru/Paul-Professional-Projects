package jsondiff

import (
	"strings"
	"testing"
)

func compile(t *testing.T, rules ...Rule) *Ruleset {
	t.Helper()
	rs, err := Compile(rules)
	if err != nil {
		t.Fatalf("Compile: %v", err)
	}
	return rs
}

func paths(ds []Difference) []string {
	out := make([]string, 0, len(ds))
	for _, d := range ds {
		out = append(out, d.Path)
	}
	return out
}

func TestPatternParsing(t *testing.T) {
	ok := []string{
		"$", "$.a", "$.a.b", "$.items[0]", "$.items[*]", "$.*.x",
		"$..x", "$..*", "$.a..b", "$.a[*].b", "$..**",
	}
	for _, p := range ok {
		if _, err := Compile([]Rule{{Pattern: p, Op: OpIgnore}}); err != nil {
			t.Errorf("Compile(%q): unexpected error %v", p, err)
		}
	}
	bad := []string{"a.b", "$.a[", "$.a[x]", "$..", "$.a."}
	for _, p := range bad {
		if _, err := Compile([]Rule{{Pattern: p, Op: OpIgnore}}); err == nil {
			t.Errorf("Compile(%q): expected an error", p)
		}
	}
}

// The descendant axis must compose with every suffix form. An earlier parser
// consumed both dots of ".." and then failed on the remainder, so "$..*" and
// "$..**" were rejected while "$..x" worked.
func TestDescendantAxisComposes(t *testing.T) {
	left := `{"a":{"b":{"n":1}},"c":2}`
	right := `{"a":{"b":{"n":9}},"c":8}`
	for _, pat := range []string{"$..*", "$..**", "$..n"} {
		rs := compile(t, Rule{Pattern: pat, Op: OpIgnore})
		got := CompareBytes([]byte(left), []byte(right), rs)
		if pat == "$..n" {
			if len(got) != 1 || got[0].Path != "$.c" {
				t.Errorf("%s: want only $.c, got %v", pat, paths(got))
			}
			continue
		}
		if len(got) != 0 {
			t.Errorf("%s: want no differences, got %v", pat, paths(got))
		}
	}
}

// Rules must resolve by specificity, not declaration order. A ruleset whose
// meaning depends on line order is a ruleset nobody can safely edit.
func TestSpecificityBeatsOrder(t *testing.T) {
	left := `{"meta":{"id":"a","n":1}}`
	right := `{"meta":{"id":"b","n":2}}`

	// Broad rule first, narrow rule second.
	rs1 := compile(t,
		Rule{Pattern: "$..*", Op: OpIgnore},
		Rule{Pattern: "$.meta.n", Op: OpTolerance, Arg: "0"},
	)
	// Same two rules, declared the other way round.
	rs2 := compile(t,
		Rule{Pattern: "$.meta.n", Op: OpTolerance, Arg: "0"},
		Rule{Pattern: "$..*", Op: OpIgnore},
	)
	g1, g2 := CompareBytes([]byte(left), []byte(right), rs1), CompareBytes([]byte(left), []byte(right), rs2)
	if len(g1) != 1 || g1[0].Path != "$.meta.n" {
		t.Fatalf("broad-first: want $.meta.n, got %v", paths(g1))
	}
	if len(g2) != len(g1) || g2[0].Path != g1[0].Path {
		t.Fatalf("order changed the result: %v vs %v", paths(g1), paths(g2))
	}
}

func TestIgnoreValueStillChecksPresenceAndType(t *testing.T) {
	rs := compile(t, Rule{Pattern: "$.ts", Op: OpIgnoreValue})

	// Different values: fine.
	if got := CompareBytes([]byte(`{"ts":"a"}`), []byte(`{"ts":"b"}`), rs); len(got) != 0 {
		t.Errorf("differing values should be ignored, got %v", paths(got))
	}
	// Missing on one side: not fine. "Ignore the value" is not "ignore the field".
	if got := CompareBytes([]byte(`{"ts":"a"}`), []byte(`{}`), rs); len(got) != 1 {
		t.Errorf("missing field should be reported, got %v", paths(got))
	}
	// Type change: not fine.
	got := CompareBytes([]byte(`{"ts":"a"}`), []byte(`{"ts":1}`), rs)
	if len(got) != 1 || got[0].Kind != TypeChanged {
		t.Errorf("type change should be reported, got %#v", got)
	}
}

// This is the whole argument of the project in one test: Format absorbs the
// noise that IgnoreValue absorbs, and catches the defect that IgnoreValue hides.
func TestFormatCatchesWhatIgnoreValueHides(t *testing.T) {
	good := []byte(`{"ts":"2024-06-12T09:31:45Z"}`)
	noise := []byte(`{"ts":"2024-06-12T09:31:46Z"}`)
	broken := []byte(`{"ts":""}`)

	fmtRS := compile(t, Rule{Pattern: "$.ts", Op: OpFormat, Arg: "rfc3339"})
	ignRS := compile(t, Rule{Pattern: "$.ts", Op: OpIgnoreValue})

	if got := CompareBytes(good, noise, fmtRS); len(got) != 0 {
		t.Errorf("format rule flagged noise: %v", got)
	}
	if got := CompareBytes(good, broken, fmtRS); len(got) != 1 || got[0].Kind != FormatBroken {
		t.Errorf("format rule missed the defect: %#v", got)
	}
	if got := CompareBytes(good, broken, ignRS); len(got) != 0 {
		t.Errorf("ignore-value was expected to hide the defect, got %v", got)
	}
}

func TestFormatOnNonStringIsAConfigError(t *testing.T) {
	rs := compile(t, Rule{Pattern: "$.n", Op: OpFormat, Arg: "rfc3339"})
	got := CompareBytes([]byte(`{"n":1}`), []byte(`{"n":1}`), rs)
	if len(got) != 1 || got[0].Kind != FormatBroken {
		t.Fatalf("want a surfaced config error, got %#v", got)
	}
	if !strings.Contains(got[0].Note, "non-string") {
		t.Errorf("note should explain the problem, got %q", got[0].Note)
	}
}

func TestUnknownFormatIsRejectedAtCompileTime(t *testing.T) {
	if _, err := Compile([]Rule{{Pattern: "$.a", Op: OpFormat, Arg: "no-such-format"}}); err == nil {
		t.Fatal("expected Compile to reject an unknown format name")
	}
}

func TestToleranceKinds(t *testing.T) {
	abs := compile(t, Rule{Pattern: "$.v", Op: OpTolerance, Arg: "0.01"})
	rel := compile(t, Rule{Pattern: "$.v", Op: OpRelTolerance, Arg: "1e-9"})

	cases := []struct {
		rs          *Ruleset
		l, r        string
		wantDiffs   int
		desc        string
		toleranceOf string
	}{
		{abs, `{"v":10.00}`, `{"v":10.01}`, 0, "inside absolute tolerance", "abs"},
		{abs, `{"v":10.00}`, `{"v":10.02}`, 1, "outside absolute tolerance", "abs"},
		{rel, `{"v":484.31}`, `{"v":484.310000000001}`, 0, "float noise", "rel"},
		{rel, `{"v":484.31}`, `{"v":484.30}`, 1, "a real penny", "rel"},
	}
	for _, c := range cases {
		got := CompareBytes([]byte(c.l), []byte(c.r), c.rs)
		if len(got) != c.wantDiffs {
			t.Errorf("%s (%s): want %d differences, got %v", c.desc, c.toleranceOf, c.wantDiffs, got)
		}
	}
}

// An absolute tolerance wide enough to swallow float noise on a large number is
// also wide enough to swallow a penny. That is the trap `tolerant` falls into.
func TestAbsoluteToleranceSwallowsAPenny(t *testing.T) {
	rs := compile(t, Rule{Pattern: "$..*", Op: OpTolerance, Arg: "0.01"})
	got := CompareBytes([]byte(`{"total":581.17}`), []byte(`{"total":581.16}`), rs)
	if len(got) != 0 {
		t.Fatalf("expected the penny to be hidden, got %v", got)
	}
	rel := compile(t, Rule{Pattern: "$..*", Op: OpRelTolerance, Arg: "1e-9"})
	if got := CompareBytes([]byte(`{"total":581.17}`), []byte(`{"total":581.16}`), rel); len(got) != 1 {
		t.Fatalf("relative tolerance should still catch it, got %v", got)
	}
}

func TestUnorderedPairsByBestMatch(t *testing.T) {
	rs := compile(t, Rule{Pattern: "$.lines", Op: OpUnordered})
	l := `{"lines":[{"sku":"A","qty":1},{"sku":"B","qty":2},{"sku":"C","qty":3}]}`
	r := `{"lines":[{"sku":"C","qty":3},{"sku":"A","qty":1},{"sku":"B","qty":2}]}`
	if got := CompareBytes([]byte(l), []byte(r), rs); len(got) != 0 {
		t.Fatalf("reordering should not be a difference, got %v", got)
	}
}

// Unordered must not become "ignore the contents". A changed element inside a
// reordered list still has to be reported.
func TestUnorderedStillDetectsChangedElement(t *testing.T) {
	rs := compile(t, Rule{Pattern: "$.lines", Op: OpUnordered})
	l := `{"lines":[{"sku":"A","qty":1},{"sku":"B","qty":2}]}`
	r := `{"lines":[{"sku":"B","qty":2},{"sku":"A","qty":99}]}`
	got := CompareBytes([]byte(l), []byte(r), rs)
	if len(got) == 0 {
		t.Fatal("changed element inside a reordered list was not reported")
	}
}

func TestUnorderedDetectsDroppedElement(t *testing.T) {
	rs := compile(t, Rule{Pattern: "$.lines", Op: OpUnordered})
	l := `{"lines":[{"sku":"A"},{"sku":"B"},{"sku":"C"}]}`
	r := `{"lines":[{"sku":"C"},{"sku":"A"}]}`
	if got := CompareBytes([]byte(l), []byte(r), rs); len(got) == 0 {
		t.Fatal("a dropped line was not reported")
	}
}

// A broad ignore must not silently outrank a narrow rule beneath it, or every
// ignore becomes an all-or-nothing decision and people stop writing narrow ones.
func TestNarrowRuleSurvivesBroadIgnore(t *testing.T) {
	rs := compile(t,
		Rule{Pattern: "$.meta", Op: OpIgnore},
		Rule{Pattern: "$.meta.tier", Op: OpIgnoreValue},
	)
	l := []byte(`{"meta":{"tier":"gold","noise":1,"deep":{"x":1}},"id":1}`)

	// Noise anywhere else in the ignored subtree stays ignored...
	quiet := []byte(`{"meta":{"tier":"gold","noise":99,"deep":{"x":99}},"id":1}`)
	if got := CompareBytes(l, quiet, rs); len(got) != 0 {
		t.Errorf("noise under $.meta should stay ignored, got %v", paths(got))
	}
	// ...but the field that was explicitly claimed is still checked.
	gone := []byte(`{"meta":{"noise":1,"deep":{"x":1}},"id":1}`)
	got := CompareBytes(l, gone, rs)
	if len(got) != 1 || got[0].Path != "$.meta.tier" || got[0].Kind != Missing {
		t.Errorf("want $.meta.tier missing, got %#v", got)
	}
	typed := []byte(`{"meta":{"tier":7,"noise":1,"deep":{"x":1}},"id":1}`)
	if got := CompareBytes(l, typed, rs); len(got) != 1 || got[0].Kind != TypeChanged {
		t.Errorf("want a type change on $.meta.tier, got %#v", got)
	}
}

// Everything else about an ignored subtree must stay quiet: length changes,
// type changes and disappearing objects included.
func TestBroadIgnoreStaysQuietStructurally(t *testing.T) {
	rs := compile(t,
		Rule{Pattern: "$.meta", Op: OpIgnore},
		Rule{Pattern: "$.meta.tier", Op: OpIgnoreValue},
	)
	l := []byte(`{"meta":{"tier":"gold","list":[1,2,3],"obj":{"a":1}},"id":1}`)
	r := []byte(`{"meta":{"tier":"silver","list":[1],"obj":"now a string"},"id":1}`)
	if got := CompareBytes(l, r, rs); len(got) != 0 {
		t.Fatalf("want silence inside the ignored subtree, got %v", paths(got))
	}
}

func TestMissingAndExtraAreDistinct(t *testing.T) {
	rs := compile(t)
	got := CompareBytes([]byte(`{"a":1}`), []byte(`{"b":2}`), rs)
	if len(got) != 2 {
		t.Fatalf("want 2 differences, got %v", paths(got))
	}
	kinds := map[Kind]bool{got[0].Kind: true, got[1].Kind: true}
	if !kinds[Missing] || !kinds[Extra] {
		t.Errorf("want one Missing and one Extra, got %v and %v", got[0].Kind, got[1].Kind)
	}
}

// A field that is absent on one side but sits under an ignored subtree must not
// be reported — otherwise `$.meta` OpIgnore would still produce meta noise.
func TestIgnoredSubtreeSuppressesMissingFields(t *testing.T) {
	rs := compile(t, Rule{Pattern: "$.meta", Op: OpIgnore})
	if got := CompareBytes([]byte(`{"meta":{"a":1},"x":1}`), []byte(`{"meta":{},"x":1}`), rs); len(got) != 0 {
		t.Fatalf("want no differences, got %v", paths(got))
	}
}

// Malformed JSON from the modern implementation is a finding, not a crash.
func TestMalformedJSON(t *testing.T) {
	rs := compile(t)
	got := CompareBytes([]byte(`{"a":1}`), []byte(`<html>502 Bad Gateway</html>`), rs)
	if len(got) != 1 || got[0].Kind != TypeChanged {
		t.Fatalf("want one TypeChanged, got %#v", got)
	}
	if !strings.Contains(got[0].Note, "right is not JSON") {
		t.Errorf("note should say which side, got %q", got[0].Note)
	}
	if got := CompareBytes([]byte(`oops`), []byte(`oops`), rs); len(got) != 0 {
		t.Errorf("two identical non-JSON bodies are not a difference, got %v", got)
	}
	if got := CompareBytes([]byte(`oops`), []byte(`nope`), rs); len(got) != 1 {
		t.Errorf("two different non-JSON bodies are a difference, got %v", got)
	}
}

// Reports get diffed against previous reports, so the order has to be stable.
func TestOutputIsDeterministic(t *testing.T) {
	rs := compile(t)
	l := []byte(`{"z":1,"a":2,"m":{"q":1,"b":2}}`)
	r := []byte(`{"z":9,"a":8,"m":{"q":9,"b":8}}`)
	first := paths(CompareBytes(l, r, rs))
	for i := 0; i < 20; i++ {
		got := paths(CompareBytes(l, r, rs))
		if strings.Join(got, ",") != strings.Join(first, ",") {
			t.Fatalf("run %d differed: %v vs %v", i, got, first)
		}
	}
	want := "$.a,$.m.b,$.m.q,$.z"
	if strings.Join(first, ",") != want {
		t.Errorf("want sorted paths %q, got %q", want, strings.Join(first, ","))
	}
}

func TestNumericTypeChangeIsNotHiddenByTolerance(t *testing.T) {
	rs := compile(t, Rule{Pattern: "$.id", Op: OpTolerance, Arg: "1000"})
	got := CompareBytes([]byte(`{"id":24894}`), []byte(`{"id":"24894"}`), rs)
	if len(got) != 1 || got[0].Kind != TypeChanged {
		t.Fatalf("an int becoming a string must survive any tolerance, got %#v", got)
	}
}

func TestFormats(t *testing.T) {
	cases := []struct {
		format string
		value  string
		want   bool
	}{
		{"rfc3339", "2024-06-12T09:31:45Z", true},
		{"rfc3339", "2024-06-12T09:31:45.123Z", true},
		{"rfc3339", "2024-06-12 09:31:45", false},
		{"rfc3339", "", false},
		{"rfc3339", "1718184704", false},
		{"uuid", "d708d49e-3743-655a-a63a-807191b445bb", true},
		{"uuid", "null", false},
		{"uuid", "", false},
		{"digits", "12345", true},
		{"digits", "12a45", false},
		{"hex", "deadBEEF", true},
		{"hex", "deadbeeg", false},
		{"nonempty", "x", true},
		{"nonempty", "  ", false},
		{"duration_ms", "18", true},
		{"duration_ms", "-1", false},
	}
	for _, c := range cases {
		f, ok := formats[c.format]
		if !ok {
			t.Fatalf("no such format %q", c.format)
		}
		if got := f(c.value); got != c.want {
			t.Errorf("%s(%q) = %v, want %v", c.format, c.value, got, c.want)
		}
	}
	if len(Formats()) != len(formats) {
		t.Errorf("Formats() should list every registered format")
	}
}
