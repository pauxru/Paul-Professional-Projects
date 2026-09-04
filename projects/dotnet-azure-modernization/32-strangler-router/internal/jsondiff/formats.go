package jsondiff

import (
	"regexp"
	"strings"
	"time"
)

// formats are the named shapes an OpFormat rule can require.
//
// The reason this exists rather than just using OpIgnoreValue everywhere: a
// timestamp field that starts returning "0001-01-01T00:00:00Z" because the new
// code forgot to set it is *still a valid timestamp*, so IgnoreValue passes it.
// A format check does not help there either — but a format check does catch the
// far more common failure where the new implementation returns a Unix epoch
// integer, a differently-formatted date, or an empty string.
//
// Measured over the corpus in docs/results.md, moving two noisy fields from
// IgnoreValue to Format recovers 162 of 812 defects — 20 percentage points of
// recall — at no cost in false positives whatsoever.
var formats = map[string]func(string) bool{
	"rfc3339": func(s string) bool {
		_, err := time.Parse(time.RFC3339, s)
		return err == nil
	},
	"uuid":     regexp.MustCompile(`^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$`).MatchString,
	"digits":   regexp.MustCompile(`^[0-9]+$`).MatchString,
	"hex":      regexp.MustCompile(`^[0-9a-fA-F]+$`).MatchString,
	"nonempty": func(s string) bool { return strings.TrimSpace(s) != "" },
	// A duration rendered as a bare non-negative number of milliseconds. It is
	// the loosest useful check on a latency field: it still fails on "", on a
	// negative, and on a value that has turned into a formatted string.
	"duration_ms": regexp.MustCompile(`^[0-9]+(\.[0-9]+)?$`).MatchString,
}

// Formats lists the registered format names, for error messages and docs.
func Formats() []string {
	out := make([]string, 0, len(formats))
	for k := range formats {
		out = append(out, k)
	}
	return out
}
