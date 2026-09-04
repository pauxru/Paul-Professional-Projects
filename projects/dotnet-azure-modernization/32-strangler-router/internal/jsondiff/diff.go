// Package jsondiff compares two JSON documents semantically.
//
// The naive version of this is twenty lines: unmarshal both sides into
// interface{} and call reflect.DeepEqual. That version is useless against real
// traffic, because real responses differ on every single request — timestamps,
// request IDs, durations, cache flags, float formatting — and a diff that
// reports every response as divergent reports nothing at all.
//
// So every shadow-traffic tool grows ignore rules, and this is where the
// interesting problem is. An ignore rule is a *classifier*: it decides that a
// difference is noise. Get it too narrow and the report is unreadable; get it
// too broad and it silently hides the defect the shadow run existed to find.
//
// That tradeoff is not hand-waved here. The package offers three progressively
// weaker ways to suppress the same noise —
//
//	Format      the value must still match a declared shape
//	IgnoreValue the value is free, but the field must exist with the same type
//	Ignore      the subtree is not compared at all
//
// — and docs/results.md measures precision and recall for each of them against
// a corpus with known ground truth. They are not stylistic alternatives. Over
// 4,000 pairs, the tightest ruleset catches 812 of 812 defects with zero false
// positives; swapping two Format rules for IgnoreValue drops that to 650, and
// the false-positive count stays at zero. The weaker rule buys nothing.
package jsondiff

import (
	"encoding/json"
	"fmt"
	"math"
	"sort"
	"strconv"
	"strings"
)

// Kind classifies a difference.
type Kind string

const (
	Missing      Kind = "missing"      // present on the left, absent on the right
	Extra        Kind = "extra"        // absent on the left, present on the right
	TypeChanged  Kind = "type"         // string became a number, object became null
	ValueChanged Kind = "value"        // same type, different content
	LengthDiff   Kind = "array_length" // arrays of different length
	FormatBroken Kind = "format"       // a Format rule matched but the value did not
)

// Difference is one semantic divergence between two documents.
type Difference struct {
	Path  string
	Kind  Kind
	Left  any
	Right any
	Note  string
}

func (d Difference) String() string {
	s := fmt.Sprintf("%s: %s", d.Path, d.Kind)
	switch d.Kind {
	case Missing:
		s += fmt.Sprintf(" (left=%s)", render(d.Left))
	case Extra:
		s += fmt.Sprintf(" (right=%s)", render(d.Right))
	default:
		s += fmt.Sprintf(" (%s != %s)", render(d.Left), render(d.Right))
	}
	if d.Note != "" {
		s += " [" + d.Note + "]"
	}
	return s
}

func render(v any) string {
	b, err := json.Marshal(v)
	if err != nil || len(b) > 60 {
		return fmt.Sprintf("%.57v...", v)
	}
	return string(b)
}

// Op is what a rule does to the node it matches.
type Op string

const (
	// OpIgnore drops the node and everything beneath it. The weakest and most
	// dangerous rule: a field that vanishes entirely is also ignored.
	OpIgnore Op = "ignore"
	// OpIgnoreValue allows any value but requires the field to be present on
	// both sides with the same JSON type. Catches disappearance and type
	// changes, which OpIgnore does not.
	OpIgnoreValue Op = "ignore_value"
	// OpFormat requires both values to match a named shape (rfc3339, uuid,
	// digits, ...). Catches disappearance, type changes, and a value that stops
	// looking like what it is supposed to be.
	OpFormat Op = "format"
	// OpUnordered compares an array as a multiset. Necessary whenever ordering
	// is a database implementation detail; also hides genuine ordering bugs, so
	// it must be declared per path rather than switched on globally.
	OpUnordered Op = "unordered"
	// OpTolerance accepts numbers within an absolute epsilon.
	OpTolerance Op = "tolerance"
	// OpRelTolerance accepts numbers within a relative epsilon.
	OpRelTolerance Op = "rel_tolerance"
)

// Rule suppresses or relaxes comparison at the paths its pattern matches.
type Rule struct {
	Pattern string
	Op      Op
	Arg     string

	segs []segment
	spec int
}

// Ruleset is an ordered collection of rules with a defined resolution order.
//
// Two rules can match the same path. Rather than "first wins" or "last wins" —
// both of which make a ruleset order-dependent in ways nobody can hold in their
// head — resolution is by *specificity*: the rule with more literal path
// segments wins, ties broken by the later declaration. A blanket
// `$..**.updatedAt` can therefore be overridden by a precise
// `$.order.updatedAt` without reordering the file.
type Ruleset struct {
	rules []Rule
	// maxSpec is the highest specificity in the set. It is the cheap test for
	// "could anything below this ignored node still be worth comparing?".
	maxSpec int
}

type segment struct {
	kind segKind
	name string
	idx  int
}

type segKind int

const (
	segField segKind = iota
	segAnyField
	segAnyDepth
	segIndex
	segAnyIndex
)

// Compile parses rule patterns and pre-computes specificity.
//
// Patterns look like JSONPath but the supported subset is deliberately small:
//
//	$.order.id           a literal path
//	$.items[0].sku       an indexed element
//	$.items[*].sku       every element
//	$.*.updatedAt        any single field, then updatedAt
//	$.**.updatedAt       updatedAt at any depth
//
// A larger subset would be a query language, and a query language in a
// configuration file is a debugging problem waiting to happen.
func Compile(rules []Rule) (*Ruleset, error) {
	out := make([]Rule, 0, len(rules))
	for i, r := range rules {
		segs, spec, err := parsePattern(r.Pattern)
		if err != nil {
			return nil, fmt.Errorf("rule %d (%q): %w", i, r.Pattern, err)
		}
		switch r.Op {
		case OpIgnore, OpIgnoreValue, OpUnordered:
		case OpFormat:
			if _, ok := formats[r.Arg]; !ok {
				return nil, fmt.Errorf("rule %d: unknown format %q", i, r.Arg)
			}
		case OpTolerance, OpRelTolerance:
			if _, err := strconv.ParseFloat(r.Arg, 64); err != nil {
				return nil, fmt.Errorf("rule %d: tolerance %q is not a number", i, r.Arg)
			}
		default:
			return nil, fmt.Errorf("rule %d: unknown op %q", i, r.Op)
		}
		r.segs, r.spec = segs, spec
		out = append(out, r)
	}
	rs := &Ruleset{rules: out}
	for i := range out {
		if out[i].spec > rs.maxSpec {
			rs.maxSpec = out[i].spec
		}
	}
	return rs, nil
}

func parsePattern(p string) ([]segment, int, error) {
	if !strings.HasPrefix(p, "$") {
		return nil, 0, fmt.Errorf("pattern must start with $")
	}
	rest := strings.TrimPrefix(p, "$")
	var segs []segment
	spec := 0
	for rest != "" {
		switch {
		case strings.HasPrefix(rest, "["):
			end := strings.Index(rest, "]")
			if end < 0 {
				return nil, 0, fmt.Errorf("unclosed [")
			}
			body := rest[1:end]
			rest = rest[end+1:]
			if body == "*" {
				segs = append(segs, segment{kind: segAnyIndex})
			} else {
				n, err := strconv.Atoi(body)
				if err != nil {
					return nil, 0, fmt.Errorf("bad index %q", body)
				}
				segs = append(segs, segment{kind: segIndex, idx: n})
				spec += 2
			}
		case strings.HasPrefix(rest, "."):
			// "$..x" — descendant axis. Consume only the first dot and emit the
			// any-depth segment; the second dot is left in place so that the
			// next iteration parses ".x", ".*" or ".**" through the ordinary
			// path, rather than needing a special case for every suffix.
			if strings.HasPrefix(rest[1:], ".") {
				rest = rest[1:]
				segs = append(segs, segment{kind: segAnyDepth})
				continue
			}
			rest = rest[1:]
			end := strings.IndexAny(rest, ".[")
			var name string
			if end < 0 {
				name, rest = rest, ""
			} else {
				name, rest = rest[:end], rest[end:]
			}
			switch name {
			case "":
				return nil, 0, fmt.Errorf("empty field name")
			case "*":
				segs = append(segs, segment{kind: segAnyField})
			case "**":
				segs = append(segs, segment{kind: segAnyDepth})
			default:
				segs = append(segs, segment{kind: segField, name: name})
				spec += 2
			}
		default:
			return nil, 0, fmt.Errorf("unexpected %q", rest)
		}
	}
	return segs, spec, nil
}

// path is the location of a node, built as the walk descends.
type path struct {
	parent *path
	name   string
	index  int
	isIdx  bool
}

func (p *path) child(name string) *path { return &path{parent: p, name: name} }
func (p *path) elem(i int) *path        { return &path{parent: p, index: i, isIdx: true} }

func (p *path) String() string {
	if p == nil {
		return "$"
	}
	if p.isIdx {
		return p.parent.String() + "[" + strconv.Itoa(p.index) + "]"
	}
	return p.parent.String() + "." + p.name
}

func (p *path) segments() []segment {
	if p == nil {
		return nil
	}
	s := p.parent.segments()
	if p.isIdx {
		return append(s, segment{kind: segIndex, idx: p.index})
	}
	return append(s, segment{kind: segField, name: p.name})
}

// lookup returns the winning rule for a path, or nil.
func (rs *Ruleset) lookup(p *path) *Rule {
	if rs == nil {
		return nil
	}
	segs := p.segments()
	var best *Rule
	for i := range rs.rules {
		r := &rs.rules[i]
		if !matchSegs(r.segs, segs) {
			continue
		}
		if best == nil || r.spec >= best.spec {
			best = r
		}
	}
	return best
}

// ignoreSpec returns the specificity of the ignore rule covering p, if any.
//
// The subtlety is that an ignore rule on an ancestor must not silently outrank
// a more specific rule on the node itself. "$.meta ignored, but $.meta.tier
// still compared" has to work, or every ignore rule is an all-or-nothing
// decision and people stop writing the narrow ones. So the ignore only counts
// if it is at least as specific as whatever rule claims the node directly.
func (rs *Ruleset) ignoreSpec(p *path) (int, bool) {
	if rs == nil {
		return 0, false
	}
	own := rs.lookup(p)
	ownSpec := -1
	if own != nil {
		if own.Op == OpIgnore {
			return own.spec, true
		}
		ownSpec = own.spec
	}
	for q := p; q != nil; q = q.parent {
		if r := rs.lookup(q); r != nil && r.Op == OpIgnore && r.spec >= ownSpec {
			return r.spec, true
		}
	}
	return 0, false
}

// ignoredAncestor reports whether p is covered by an ignore rule.
func (rs *Ruleset) ignoredAncestor(p *path) bool {
	_, ok := rs.ignoreSpec(p)
	return ok
}

func matchSegs(pat, actual []segment) bool {
	if len(pat) == 0 {
		return len(actual) == 0
	}
	switch pat[0].kind {
	case segAnyDepth:
		for i := 0; i <= len(actual); i++ {
			if matchSegs(pat[1:], actual[i:]) {
				return true
			}
		}
		return false
	default:
		if len(actual) == 0 {
			return false
		}
		if !matchOne(pat[0], actual[0]) {
			return false
		}
		return matchSegs(pat[1:], actual[1:])
	}
}

func matchOne(p, a segment) bool {
	switch p.kind {
	case segField:
		return a.kind == segField && a.name == p.name
	case segAnyField:
		// `.*` is a wildcard over any child, field or element, matching the
		// JSONPath convention. `[*]` below is the index-only form.
		return a.kind == segField || a.kind == segIndex
	case segIndex:
		return a.kind == segIndex && a.idx == p.idx
	case segAnyIndex:
		return a.kind == segIndex
	}
	return false
}

// Compare produces every semantic difference between left and right.
//
// Differences are returned in a deterministic order — object keys are walked
// sorted — so that two runs over the same inputs produce identical reports and
// a report can be diffed against a previous one.
func Compare(left, right any, rs *Ruleset) []Difference {
	var out []Difference
	compare(nil, left, right, rs, &out)
	sort.SliceStable(out, func(i, j int) bool { return out[i].Path < out[j].Path })
	return out
}

// CompareBytes unmarshals both sides first. Malformed JSON on either side is a
// difference, not an error: the whole point of a shadow run is that the new
// implementation might return something unparseable, and that is a finding.
func CompareBytes(left, right []byte, rs *Ruleset) []Difference {
	var l, r any
	lerr := json.Unmarshal(left, &l)
	rerr := json.Unmarshal(right, &r)
	switch {
	case lerr != nil && rerr != nil:
		if string(left) == string(right) {
			return nil
		}
		return []Difference{{Path: "$", Kind: ValueChanged, Left: string(left), Right: string(right),
			Note: "neither side is JSON"}}
	case lerr != nil:
		return []Difference{{Path: "$", Kind: TypeChanged, Left: string(left), Right: r,
			Note: "left is not JSON"}}
	case rerr != nil:
		return []Difference{{Path: "$", Kind: TypeChanged, Left: l, Right: string(right),
			Note: "right is not JSON"}}
	}
	return Compare(l, r, rs)
}

func compare(p *path, l, r any, rs *Ruleset, out *[]Difference) {
	rule := rs.lookup(p)
	// suppressed means "this node is inside an ignored subtree": keep walking so
	// that a narrower rule further down still applies, but report nothing about
	// the node itself.
	suppressed := false

	if isp, covered := rs.ignoreSpec(p); covered {
		// Stop immediately if nothing in the ruleset could out-specify this
		// ignore further down — the common case, and the cheap one. Otherwise
		// keep walking composites so a narrower rule beneath still gets its say.
		if rs.maxSpec <= isp {
			return
		}
		switch l.(type) {
		case map[string]any, []any:
			suppressed, rule = true, nil
		default:
			return
		}
	}

	if rule != nil {
		switch rule.Op {
		case OpIgnoreValue:
			if d, bad := requirePresenceAndType(p, l, r); bad {
				*out = append(*out, d)
			}
			return
		case OpFormat:
			if d, bad := requirePresenceAndType(p, l, r); bad {
				*out = append(*out, d)
				return
			}
			f := formats[rule.Arg]
			ls, lok := l.(string)
			rs2, rok := r.(string)
			if !lok || !rok {
				// A format rule on a non-string is a configuration error worth
				// surfacing, not silently skipping.
				*out = append(*out, Difference{Path: p.String(), Kind: FormatBroken,
					Left: l, Right: r, Note: "format rule on a non-string value"})
				return
			}
			if !f(ls) || !f(rs2) {
				*out = append(*out, Difference{Path: p.String(), Kind: FormatBroken,
					Left: l, Right: r, Note: "does not match format " + rule.Arg})
			}
			return
		}
	}

	switch lv := l.(type) {
	case map[string]any:
		rv, ok := r.(map[string]any)
		if !ok {
			if !suppressed {
				*out = append(*out, Difference{Path: p.String(), Kind: TypeChanged, Left: l, Right: r})
			}
			return
		}
		keys := union(lv, rv)
		for _, k := range keys {
			cp := p.child(k)
			lval, lok := lv[k]
			rval, rok := rv[k]
			switch {
			case lok && rok:
				compare(cp, lval, rval, rs, out)
			case lok:
				if !rs.ignoredAncestor(cp) {
					*out = append(*out, Difference{Path: cp.String(), Kind: Missing, Left: lval})
				}
			default:
				if !rs.ignoredAncestor(cp) {
					*out = append(*out, Difference{Path: cp.String(), Kind: Extra, Right: rval})
				}
			}
		}
	case []any:
		rv, ok := r.([]any)
		if !ok {
			if !suppressed {
				*out = append(*out, Difference{Path: p.String(), Kind: TypeChanged, Left: l, Right: r})
			}
			return
		}
		if rule != nil && rule.Op == OpUnordered {
			compareUnordered(p, lv, rv, rs, out)
			return
		}
		if len(lv) != len(rv) && !suppressed {
			*out = append(*out, Difference{Path: p.String(), Kind: LengthDiff,
				Left: len(lv), Right: len(rv)})
		}
		for i := 0; i < min(len(lv), len(rv)); i++ {
			compare(p.elem(i), lv[i], rv[i], rs, out)
		}
	case float64:
		rv, ok := r.(float64)
		if !ok {
			*out = append(*out, Difference{Path: p.String(), Kind: TypeChanged, Left: l, Right: r})
			return
		}
		if numbersEqual(lv, rv, rule) {
			return
		}
		*out = append(*out, Difference{Path: p.String(), Kind: ValueChanged, Left: l, Right: r})
	case nil:
		if r != nil {
			*out = append(*out, Difference{Path: p.String(), Kind: TypeChanged, Left: l, Right: r})
		}
	default:
		if !sameScalar(l, r) {
			kind := ValueChanged
			if fmt.Sprintf("%T", l) != fmt.Sprintf("%T", r) {
				kind = TypeChanged
			}
			*out = append(*out, Difference{Path: p.String(), Kind: kind, Left: l, Right: r})
		}
	}
}

func requirePresenceAndType(p *path, l, r any) (Difference, bool) {
	lt, rt := jsonType(l), jsonType(r)
	if lt != rt {
		return Difference{Path: p.String(), Kind: TypeChanged, Left: l, Right: r,
			Note: "value ignored, type is not"}, true
	}
	return Difference{}, false
}

func jsonType(v any) string {
	switch v.(type) {
	case nil:
		return "null"
	case bool:
		return "bool"
	case float64:
		return "number"
	case string:
		return "string"
	case []any:
		return "array"
	case map[string]any:
		return "object"
	}
	return fmt.Sprintf("%T", v)
}

func numbersEqual(a, b float64, rule *Rule) bool {
	if a == b {
		return true
	}
	if rule == nil {
		return false
	}
	eps, err := strconv.ParseFloat(rule.Arg, 64)
	if err != nil {
		return false
	}
	switch rule.Op {
	case OpTolerance:
		return math.Abs(a-b) <= eps
	case OpRelTolerance:
		scale := math.Max(math.Abs(a), math.Abs(b))
		if scale == 0 {
			return true
		}
		return math.Abs(a-b)/scale <= eps
	}
	return false
}

func sameScalar(a, b any) bool {
	return fmt.Sprintf("%T|%v", a, a) == fmt.Sprintf("%T|%v", b, b)
}

// compareUnordered matches elements as a multiset.
//
// Elements are paired greedily by canonical form. Anything left unpaired on
// each side is then compared positionally, so a report says "this element
// changed" rather than "one element is missing and a different one appeared",
// which is what a naive set difference produces and what makes unordered diffs
// unreadable.
func compareUnordered(p *path, l, r []any, rs *Ruleset, out *[]Difference) {
	used := make([]bool, len(r))
	var leftover []int
	for i, lv := range l {
		key := canonical(lv)
		found := -1
		for j, rv := range r {
			if used[j] {
				continue
			}
			if canonical(rv) == key {
				found = j
				break
			}
		}
		if found >= 0 {
			used[found] = true
		} else {
			leftover = append(leftover, i)
		}
	}
	var unmatchedRight []int
	for j, u := range used {
		if !u {
			unmatchedRight = append(unmatchedRight, j)
		}
	}
	for k := 0; k < min(len(leftover), len(unmatchedRight)); k++ {
		compare(p.elem(leftover[k]), l[leftover[k]], r[unmatchedRight[k]], rs, out)
	}
	for k := len(unmatchedRight); k < len(leftover); k++ {
		*out = append(*out, Difference{Path: p.elem(leftover[k]).String(), Kind: Missing,
			Left: l[leftover[k]], Note: "no unordered match"})
	}
	for k := len(leftover); k < len(unmatchedRight); k++ {
		*out = append(*out, Difference{Path: p.elem(unmatchedRight[k]).String(), Kind: Extra,
			Right: r[unmatchedRight[k]], Note: "no unordered match"})
	}
}

func canonical(v any) string {
	b, err := json.Marshal(sortedValue(v))
	if err != nil {
		return fmt.Sprintf("%v", v)
	}
	return string(b)
}

func sortedValue(v any) any {
	switch t := v.(type) {
	case map[string]any:
		keys := make([]string, 0, len(t))
		for k := range t {
			keys = append(keys, k)
		}
		sort.Strings(keys)
		// json.Marshal already sorts map keys, but nested arrays need
		// normalising too, so the recursion is not redundant.
		m := make(map[string]any, len(t))
		for _, k := range keys {
			m[k] = sortedValue(t[k])
		}
		return m
	case []any:
		s := make([]any, len(t))
		for i := range t {
			s[i] = sortedValue(t[i])
		}
		return s
	}
	return v
}

func union(a, b map[string]any) []string {
	seen := make(map[string]bool, len(a)+len(b))
	keys := make([]string, 0, len(a)+len(b))
	for k := range a {
		seen[k] = true
		keys = append(keys, k)
	}
	for k := range b {
		if !seen[k] {
			keys = append(keys, k)
		}
	}
	sort.Strings(keys)
	return keys
}

func min(a, b int) int {
	if a < b {
		return a
	}
	return b
}
