package locks

import "testing"

// The conflict matrix is transcribed from the PostgreSQL documentation. A
// transcription error in it would silently corrupt every result in this
// project, so it gets tested against its own structural properties rather
// than against a second copy of the same table.

func TestSymmetry(t *testing.T) {
	for _, a := range All {
		for _, b := range All {
			if Conflicts(a, b) != Conflicts(b, a) {
				t.Fatalf("asymmetric: %s vs %s = %v but %s vs %s = %v",
					a, b, Conflicts(a, b), b, a, Conflicts(b, a))
			}
		}
	}
}

func TestOrderingIsNotAConflictOrdering(t *testing.T) {
	// Written expecting the conflict relation to be monotone in the declared
	// weakest-to-strongest ordering. It is not, and the counterexample is
	// worth naming because it invalidates an intuitive safety rule.
	//
	// SHARE UPDATE EXCLUSIVE sorts *below* SHARE, but SUE conflicts with
	// SHARE while SHARE is self-compatible. So escalating a lock request one
	// step "up" the ordering can make it conflict with strictly fewer things.
	if Dominates(Share, ShareUpdateExclusive) {
		t.Fatal("expected SHARE not to dominate SHARE UPDATE EXCLUSIVE")
	}
	if !Conflicts(ShareUpdateExclusive, Share) {
		t.Fatal("SUE must conflict with SHARE")
	}
	if Conflicts(Share, Share) {
		t.Fatal("SHARE must be self-compatible")
	}

	// Count how many ordered pairs break monotonicity, so the report can
	// state the size of the effect rather than just its existence.
	breaks := 0
	for i := 0; i < len(All); i++ {
		for j := i + 1; j < len(All); j++ {
			if !Dominates(All[j], All[i]) {
				breaks++
			}
		}
	}
	if breaks == 0 {
		t.Fatal("expected at least one non-dominating ordered pair")
	}
	t.Logf("%d of %d ordered pairs are non-dominating", breaks, len(All)*(len(All)-1)/2)
}

func TestOnlyAccessExclusiveDominatesEverything(t *testing.T) {
	// The consequence of the above: there is exactly one mode that is safe to
	// assume when the statement is not understood.
	var tops []Mode
	for _, m := range All {
		ok := true
		for _, o := range All {
			if !Dominates(m, o) {
				ok = false
				break
			}
		}
		if ok {
			tops = append(tops, m)
		}
	}
	if len(tops) != 1 || tops[0] != AccessExclusive {
		t.Fatalf("expected ACCESS EXCLUSIVE to be the unique top, got %v", tops)
	}
	if Top() != AccessExclusive {
		t.Fatalf("Top() = %s", Top())
	}
}

func TestAccessShareOnlyConflictsWithAccessExclusive(t *testing.T) {
	for _, m := range All {
		got := Conflicts(AccessShare, m)
		want := m == AccessExclusive
		if got != want {
			t.Fatalf("ACCESS SHARE vs %s: got %v want %v", m, got, want)
		}
	}
}

func TestAccessExclusiveConflictsWithEverything(t *testing.T) {
	for _, m := range All {
		if !Conflicts(AccessExclusive, m) {
			t.Fatalf("ACCESS EXCLUSIVE should conflict with %s", m)
		}
	}
}

func TestSelfConflicting(t *testing.T) {
	// The four self-compatible modes are exactly the ones that let concurrent
	// traffic of the same shape run.
	want := map[Mode]bool{
		AccessShare: false, RowShare: false, RowExclusive: false,
		ShareUpdateExclusive: true, Share: false, ShareRowExclusive: true,
		Exclusive: true, AccessExclusive: true,
	}
	for m, w := range want {
		if got := SelfConflicting(m); got != w {
			t.Fatalf("SelfConflicting(%s) = %v want %v", m, got, w)
		}
	}
}

func TestShareSelfCompatible(t *testing.T) {
	// SHARE is self-compatible, which is why two concurrent non-concurrent
	// index builds can proceed together while blocking all writes. It is a
	// genuinely surprising cell of the matrix and worth pinning.
	if Conflicts(Share, Share) {
		t.Fatal("SHARE must not conflict with SHARE")
	}
	if !Conflicts(Share, RowExclusive) {
		t.Fatal("SHARE must conflict with ROW EXCLUSIVE (that is what blocks writes)")
	}
}

func TestBlocksReadsOnlyAccessExclusive(t *testing.T) {
	// This is the single most misunderstood fact about PostgreSQL DDL: only
	// ACCESS EXCLUSIVE blocks a plain SELECT. Everything else, including a
	// non-concurrent index build, lets reads through.
	for _, m := range All {
		got := BlocksReads(m)
		want := m == AccessExclusive
		if got != want {
			t.Fatalf("BlocksReads(%s) = %v want %v", m, got, want)
		}
	}
}

func TestBlocksWrites(t *testing.T) {
	want := map[Mode]bool{
		AccessShare: false, RowShare: false, RowExclusive: false,
		ShareUpdateExclusive: false, Share: true, ShareRowExclusive: true,
		Exclusive: true, AccessExclusive: true,
	}
	for m, w := range want {
		if got := BlocksWrites(m); got != w {
			t.Fatalf("BlocksWrites(%s) = %v want %v", m, got, w)
		}
	}
}

func TestShareUpdateExclusiveAllowsReadsAndWrites(t *testing.T) {
	// This is why CREATE INDEX CONCURRENTLY and VALIDATE CONSTRAINT are the
	// escape hatches the whole linter recommends.
	if BlocksReads(ShareUpdateExclusive) || BlocksWrites(ShareUpdateExclusive) {
		t.Fatal("SHARE UPDATE EXCLUSIVE must allow both reads and writes")
	}
	if !SelfConflicting(ShareUpdateExclusive) {
		t.Fatal("SHARE UPDATE EXCLUSIVE must conflict with itself: two concurrent index builds serialise")
	}
}

func TestCompatible(t *testing.T) {
	cases := []struct {
		held []Mode
		want Mode
		ok   bool
	}{
		{nil, AccessExclusive, true},
		{[]Mode{AccessShare}, AccessShare, true},
		{[]Mode{AccessShare}, AccessExclusive, false},
		{[]Mode{AccessShare, RowExclusive}, Share, false},
		{[]Mode{AccessShare, RowShare}, Share, true},
		{[]Mode{ShareUpdateExclusive}, ShareUpdateExclusive, false},
		{[]Mode{RowExclusive, RowExclusive, RowExclusive}, AccessShare, true},
	}
	for i, c := range cases {
		if got := Compatible(c.held, c.want); got != c.ok {
			t.Fatalf("case %d: Compatible(%v, %s) = %v want %v", i, c.held, c.want, got, c.ok)
		}
	}
}

func TestSymmetricHelper(t *testing.T) {
	if !Symmetric() {
		t.Fatal("Symmetric() must agree with the pairwise check")
	}
}

func TestAllCoversEveryMode(t *testing.T) {
	if len(All) != 8 {
		t.Fatalf("PostgreSQL has 8 table lock modes, All has %d", len(All))
	}
	seen := map[string]bool{}
	for _, m := range All {
		s := m.String()
		if seen[s] {
			t.Fatalf("duplicate mode name %q", s)
		}
		if s == "" || s[0] == 'M' {
			t.Fatalf("mode %d has no name", int(m))
		}
		seen[s] = true
	}
}

func TestOrderIsWeakestFirst(t *testing.T) {
	// Several call sites assume All is ordered weakest-to-strongest.
	for i := 1; i < len(All); i++ {
		prev, cur := All[i-1], All[i]
		pn, cn := 0, 0
		for _, o := range All {
			if Conflicts(prev, o) {
				pn++
			}
			if Conflicts(cur, o) {
				cn++
			}
		}
		if cn < pn {
			t.Fatalf("All is not weakest-first: %s conflicts with %d, %s with %d",
				prev, pn, cur, cn)
		}
	}
}
