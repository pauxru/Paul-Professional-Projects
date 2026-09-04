package main

import (
	"crypto/sha256"
	"encoding/hex"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// TestResultsAreReproducible regenerates docs/results.md and compares it byte
// for byte with the committed copy.
//
// The point is not that the file is pretty. The point is that every number in
// it is derived, not typed. A report with hand-edited figures is indisting-
// uishable from a report with correct figures right up until someone checks,
// and by then it has been read by people who made decisions on it.
//
// This test failing means one of two things, and both are worth knowing: the
// model changed and the document is now stale, or the document was edited by
// hand and no longer describes the model.
//
// Skipped under -short because it runs every simulation in the report.
func TestResultsAreReproducible(t *testing.T) {
	if testing.Short() {
		t.Skip("regenerates the whole report; run without -short")
	}

	committed, err := os.ReadFile(filepath.Join("..", "..", "docs", "results.md"))
	if err != nil {
		t.Fatalf("read committed report: %v", err)
	}

	fresh, err := build().Render()
	if err != nil {
		t.Fatalf("render: %v", err)
	}

	if normalise(string(committed)) == normalise(fresh) {
		return
	}

	t.Errorf("docs/results.md does not match a fresh run\n  committed sha256/16 %s\n  regenerated sha256/16 %s\n%s",
		short(string(committed)), short(fresh), firstDifference(string(committed), fresh))
}

// TestEveryPredictionIsAdjudicated checks that no section states an
// expectation without a verdict.
//
// A prediction with no result is worse than no prediction: it reads as
// confidence and carries none. The report DSL makes it easy to write Expect
// and forget Found, and the failure is silent, so it is checked here.
func TestEveryPredictionIsAdjudicated(t *testing.T) {
	fs := build().Findings()
	if len(fs) == 0 {
		t.Fatal("no predictions found; the report has lost its point")
	}
	for _, f := range fs {
		if strings.TrimSpace(f.Expect) == "" {
			t.Errorf("section %q states a verdict with no expectation, which is a conclusion "+
				"written after seeing the data", f.Section)
		}
		if strings.TrimSpace(f.Found) == "" {
			t.Errorf("section %q states an expectation with no verdict", f.Section)
		}
	}
}

// TestContradictionsSurvive is a guard against the most tempting edit in this
// whole repository.
//
// When a prediction fails there are two ways forward: understand why, or
// quietly reword the prediction until it passes. The second is much faster and
// produces a document in which the author was right about everything, which is
// not a document anyone should trust. At least one contradiction is expected
// to remain; if that ever reaches zero, this test asks the author to prove it
// happened by learning something rather than by editing prose.
func TestContradictionsSurvive(t *testing.T) {
	total, held, contra := build().Tally()
	if total == 0 {
		t.Fatal("no adjudicated predictions")
	}
	if contra == 0 {
		t.Error("every prediction now holds. That is possible, but it is also what a report " +
			"looks like after the predictions have been edited to match the data. If this is " +
			"genuine, delete this test in the same commit that makes it pass, and say why.")
	}
	t.Logf("%d held, %d contradicted", held, contra)
}

func short(s string) string {
	sum := sha256.Sum256([]byte(s))
	return hex.EncodeToString(sum[:])[:16]
}

// normalise absorbs the one difference that is not a difference: Windows
// checkouts rewrite LF to CRLF, so a byte comparison against a file read back
// from disk fails on a clean tree for reasons that have nothing to do with the
// model.
func normalise(s string) string {
	return strings.ReplaceAll(s, "\r\n", "\n")
}

func firstDifference(a, b string) string {
	la, lb := strings.Split(normalise(a), "\n"), strings.Split(normalise(b), "\n")
	for i := 0; i < len(la) && i < len(lb); i++ {
		if la[i] != lb[i] {
			return "  first difference at line " + itoa(i+1) + "\n    committed: " +
				trunc(la[i]) + "\n    regenerated: " + trunc(lb[i])
		}
	}
	if len(la) != len(lb) {
		return "  lengths differ: committed " + itoa(len(la)) + " lines, regenerated " + itoa(len(lb))
	}
	return ""
}

func trunc(s string) string {
	if len(s) > 120 {
		return s[:120] + "..."
	}
	return s
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	var b []byte
	for n > 0 {
		b = append([]byte{byte('0' + n%10)}, b...)
		n /= 10
	}
	return string(b)
}
