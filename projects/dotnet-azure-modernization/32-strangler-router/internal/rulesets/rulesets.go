// Package rulesets holds the ignore-rule configurations compared in
// docs/results.md.
//
// These are not hypothetical. Each one is a configuration teams actually write,
// in roughly the order they write them: start exact, get flooded, ignore the
// obvious volatile fields, get flooded again by something subtler, ignore a
// whole subtree, and eventually add a blanket numeric tolerance because "the
// penny differences are just floating point".
//
// The progression is rational at every step and ends somewhere indefensible.
// The point of scoring them is to show where on that path the rules stop being
// a noise filter and start being a bug filter.
package rulesets

import "strangler/internal/jsondiff"

// Named is a ruleset with the story of how a team arrives at it.
type Named struct {
	Name  string
	Story string
	Rules []jsondiff.Rule
}

// Exact compares semantically but with no rules at all.
var Exact = Named{
	Name:  "exact",
	Story: "No ignore rules. Every response differs; the report is unreadable.",
	Rules: nil,
}

// Precise is the ruleset this project argues for: every volatile field named
// individually, and constrained as tightly as it can be while still absorbing
// the noise.
var Precise = Named{
	Name: "precise",
	Story: "Every volatile field named individually and constrained to its shape. " +
		"More typing, and it is the only ruleset that costs nothing.",
	Rules: []jsondiff.Rule{
		{Pattern: "$.meta.requestId", Op: jsondiff.OpFormat, Arg: "uuid"},
		{Pattern: "$.meta.generatedAt", Op: jsondiff.OpFormat, Arg: "rfc3339"},
		{Pattern: "$.meta.durationMs", Op: jsondiff.OpIgnoreValue},
		{Pattern: "$.meta.cache", Op: jsondiff.OpIgnoreValue},
		{Pattern: "$.order.lines", Op: jsondiff.OpUnordered},
		{Pattern: "$.order.subtotal", Op: jsondiff.OpRelTolerance, Arg: "1e-9"},
	},
}

// ValueBlind replaces the two format checks with IgnoreValue. This is the most
// common ruleset in the wild, because IgnoreValue is what most diff tools offer
// and formats are extra work.
var ValueBlind = Named{
	Name: "value-blind",
	Story: "Same fields, but the values are unconstrained instead of shape-checked. " +
		"One config change away from `precise`.",
	Rules: []jsondiff.Rule{
		{Pattern: "$.meta.requestId", Op: jsondiff.OpIgnoreValue},
		{Pattern: "$.meta.generatedAt", Op: jsondiff.OpIgnoreValue},
		{Pattern: "$.meta.durationMs", Op: jsondiff.OpIgnoreValue},
		{Pattern: "$.meta.cache", Op: jsondiff.OpIgnoreValue},
		{Pattern: "$.order.lines", Op: jsondiff.OpUnordered},
		{Pattern: "$.order.subtotal", Op: jsondiff.OpRelTolerance, Arg: "1e-9"},
	},
}

// SubtreeIgnored is the "just ignore the meta block" ruleset. It is one line
// shorter than ValueBlind and reads as a simplification.
var SubtreeIgnored = Named{
	Name: "subtree-ignored",
	Story: "\"The whole meta block is noise, just drop it.\" Shorter, tidier, " +
		"and it stops comparing a field the business cares about.",
	Rules: []jsondiff.Rule{
		{Pattern: "$.meta", Op: jsondiff.OpIgnore},
		{Pattern: "$.order.lines", Op: jsondiff.OpUnordered},
		{Pattern: "$.order.subtotal", Op: jsondiff.OpRelTolerance, Arg: "1e-9"},
	},
}

// Tolerant adds the blanket numeric tolerance that gets proposed the first time
// a rounding difference shows up in the report.
var Tolerant = Named{
	Name: "tolerant",
	Story: "Plus \"a penny is just floating point\": an absolute 0.01 tolerance " +
		"on every number in the document.",
	Rules: []jsondiff.Rule{
		{Pattern: "$.meta", Op: jsondiff.OpIgnore},
		{Pattern: "$.order.lines", Op: jsondiff.OpUnordered},
		{Pattern: "$..*", Op: jsondiff.OpTolerance, Arg: "0.01"},
	},
}

// Resigned is the end of the road: someone got tired of a flaky field and
// ignored it, and someone else stopped comparing status because a casing change
// "wasn't real".
var Resigned = Named{
	Name: "resigned",
	Story: "Plus the discount field, which \"kept flapping\", and status, which " +
		"\"is just casing\". Every rule here was added by a reasonable person.",
	Rules: []jsondiff.Rule{
		{Pattern: "$.meta", Op: jsondiff.OpIgnore},
		{Pattern: "$.order.lines", Op: jsondiff.OpUnordered},
		{Pattern: "$..*", Op: jsondiff.OpTolerance, Arg: "0.01"},
		{Pattern: "$.order.lines[*].discount", Op: jsondiff.OpIgnore},
		{Pattern: "$.order.status", Op: jsondiff.OpIgnoreValue},
	},
}

// All is the progression, in the order teams walk it.
var All = []Named{Exact, Precise, ValueBlind, SubtreeIgnored, Tolerant, Resigned}
