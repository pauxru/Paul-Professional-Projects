// Package locks models PostgreSQL's table-level lock manager.
//
// The whole project rests on one property of that manager, so it is worth
// stating up front: lock requests are queued, and the queue is ordered. A
// request that conflicts with a held lock waits, and every request that
// arrives after it waits too, even if it would not have conflicted with
// anything currently held.
//
// That is why a three-second ALTER TABLE can stop a table for an hour. The
// ALTER waits behind one long-running SELECT; every subsequent query waits
// behind the ALTER. The outage is not caused by the DDL holding a lock. It
// is caused by the DDL *waiting* for one.
package locks

import "fmt"

// Mode is a PostgreSQL table-level lock mode.
//
// Ordered weakest to strongest. The ordering is a convenience for reporting;
// conflict is decided by the matrix below, not by comparing modes, because
// the conflict relation is not a total order -- SHARE and ROW EXCLUSIVE
// conflict with each other while neither is stronger than the other in any
// useful sense.
type Mode int

const (
	AccessShare Mode = iota
	RowShare
	RowExclusive
	ShareUpdateExclusive
	Share
	ShareRowExclusive
	Exclusive
	AccessExclusive
	// None is not a PostgreSQL lock mode. It is this package's way of saying
	// "this statement takes no table-level lock at all" -- BEGIN, COMMIT, SET.
	//
	// It is declared after AccessExclusive on purpose. The eight real modes
	// keep the indices PostgreSQL gives them, so the conflict matrix stays a
	// direct transcription of the documentation rather than a transcription
	// plus an offset that someone will eventually get wrong.
	None
)

// All is every mode, in the declared weakest-to-strongest order.
var All = []Mode{
	AccessShare, RowShare, RowExclusive, ShareUpdateExclusive,
	Share, ShareRowExclusive, Exclusive, AccessExclusive,
}

var modeNames = [...]string{
	"ACCESS SHARE",
	"ROW SHARE",
	"ROW EXCLUSIVE",
	"SHARE UPDATE EXCLUSIVE",
	"SHARE",
	"SHARE ROW EXCLUSIVE",
	"EXCLUSIVE",
	"ACCESS EXCLUSIVE",
	"NONE",
}

func (m Mode) String() string {
	if int(m) < 0 || int(m) >= len(modeNames) {
		return fmt.Sprintf("Mode(%d)", int(m))
	}
	return modeNames[m]
}

// Valid reports whether m is a known mode.
func (m Mode) Valid() bool { return int(m) >= 0 && int(m) < len(modeNames) }

// conflicts is PostgreSQL's lock conflict matrix, transcribed from the
// documented table rather than derived from the ordering, because it is not
// derivable from the ordering.
//
// conflicts[held][requested] is true when a request for `requested` must wait
// for a holder of `held` to release.
var conflicts = [8][8]bool{
	//                   AS     RS     RE     SUE    S      SRE    E      AE
	/* AccessShare */ {false, false, false, false, false, false, false, true},
	/* RowShare */ {false, false, false, false, false, false, true, true},
	/* RowExclusive */ {false, false, false, false, true, true, true, true},
	/* ShareUpdateEx */ {false, false, false, true, true, true, true, true},
	/* Share */ {false, false, true, true, false, true, true, true},
	/* ShareRowEx */ {false, false, true, true, true, true, true, true},
	/* Exclusive */ {false, true, true, true, true, true, true, true},
	/* AccessExcl */ {true, true, true, true, true, true, true, true},
}

// Conflicts reports whether a request for `want` must wait for a holder of
// `held`.
func Conflicts(held, want Mode) bool {
	if held == None || want == None {
		// A statement that takes no lock cannot block or be blocked. Reaching
		// the matrix with None would index out of bounds, so this has to come
		// before the validity check rather than after it.
		return false
	}
	if !held.Valid() || !want.Valid() {
		panic(fmt.Sprintf("locks: invalid mode pair %v/%v", held, want))
	}
	return conflicts[held][want]
}

// Symmetric reports whether the conflict matrix is symmetric.
//
// It is, and it must be: "A must wait for B" and "B must wait for A" describe
// the same incompatibility. Exposed rather than asserted in an init so a test
// can name the property, because a transcribed matrix with one transposed
// entry is a bug that would otherwise surface as a rare, load-dependent
// scheduling anomaly.
func Symmetric() bool {
	for a := AccessShare; a <= AccessExclusive; a++ {
		for b := AccessShare; b <= AccessExclusive; b++ {
			if conflicts[a][b] != conflicts[b][a] {
				return false
			}
		}
	}
	return true
}

// SelfConflicting reports whether a mode conflicts with itself, i.e. whether
// two transactions can hold it simultaneously.
func SelfConflicting(m Mode) bool { return Conflicts(m, m) }

// BlocksReads reports whether holding m prevents a plain SELECT.
//
// Only ACCESS EXCLUSIVE does. This is the single most useful fact about the
// matrix and the one most often got wrong: EXCLUSIVE, despite the name, does
// not block reads.
func BlocksReads(m Mode) bool { return Conflicts(m, AccessShare) }

// BlocksWrites reports whether holding m prevents INSERT, UPDATE or DELETE.
func BlocksWrites(m Mode) bool { return Conflicts(m, RowExclusive) }

// Compatible reports whether a set of already-granted modes admits `want`.
func Compatible(held []Mode, want Mode) bool {
	for _, h := range held {
		if Conflicts(h, want) {
			return false
		}
	}
	return true
}

// Dominates reports whether every conflict of `weak` is also a conflict of
// `strong` -- that is, whether requesting `strong` is always at least as
// restrictive as requesting `weak`.
//
// This is NOT implied by the ordinal ordering of Mode, and the difference is
// load-bearing. SHARE UPDATE EXCLUSIVE sorts below SHARE, yet SHARE UPDATE
// EXCLUSIVE conflicts with SHARE while SHARE does not conflict with itself.
// So "when in doubt, escalate one step up the ordering" is not a sound safety
// rule; only escalating all the way to ACCESS EXCLUSIVE is, because it is the
// unique top of the conflict lattice.
//
// LintOf's Unknown case depends on exactly that: an unparsed statement is
// assumed ACCESS EXCLUSIVE, not "the next mode up".
func Dominates(strong, weak Mode) bool {
	for _, o := range All {
		if Conflicts(weak, o) && !Conflicts(strong, o) {
			return false
		}
	}
	return true
}

// Top returns the unique mode that dominates every other mode.
func Top() Mode { return AccessExclusive }
