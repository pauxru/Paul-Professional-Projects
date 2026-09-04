package lint

import (
	"strings"
	"testing"

	"evolve/ddl"
	"evolve/locks"
)

func one(t *testing.T, sql string) ddl.Stmt {
	t.Helper()
	st, err := ddl.Parse(sql)
	if err != nil || len(st) != 1 {
		t.Fatalf("Parse(%q): %v (%d stmts)", sql, err, len(st))
	}
	return st[0]
}

func lintOne(t *testing.T, sql string, version int) Report {
	t.Helper()
	return Lint([]ddl.Stmt{one(t, sql)}, version)
}

func hasRule(r Report, rule string) bool {
	for _, f := range r.Findings {
		if f.Rule == rule {
			return true
		}
	}
	return false
}

// The version boundary is the central claim of this package: the same
// statement is safe on one PostgreSQL major and an outage on the one before
// it. These tests pin both sides of each boundary.

func TestAddColumnDefaultBoundaryAt11(t *testing.T) {
	sql := "ALTER TABLE orders ADD COLUMN status text DEFAULT 'new'"
	if !Rewrites(one(t, sql), 10) {
		t.Fatal("PostgreSQL 10 must rewrite on ADD COLUMN ... DEFAULT")
	}
	if Rewrites(one(t, sql), 11) {
		t.Fatal("PostgreSQL 11 stores a non-volatile default in the catalogue")
	}
	if Rewrites(one(t, sql), 16) {
		t.Fatal("PostgreSQL 16 must behave like 11")
	}
}

func TestAddColumnDefaultRefusedOnlyBelow11(t *testing.T) {
	sql := "ALTER TABLE orders ADD COLUMN status text DEFAULT 'new'"
	if !lintOne(t, sql, 10).Refused() {
		t.Fatal("expected REFUSE on 10")
	}
	if lintOne(t, sql, 11).Refused() {
		t.Fatal("expected no REFUSE on 11")
	}
}

func TestVolatileDefaultRewritesOnEveryVersion(t *testing.T) {
	// The PostgreSQL 11 optimisation only applies to a constant. A volatile
	// default has to be evaluated per row on every version, forever.
	for _, v := range []int{9, 10, 11, 12, 14, 16, 17} {
		s := one(t, "ALTER TABLE orders ADD COLUMN id uuid DEFAULT gen_random_uuid()")
		if !Rewrites(s, v) {
			t.Fatalf("version %d: volatile default must rewrite", v)
		}
	}
}

func TestVolatileDetection(t *testing.T) {
	volatileCases := []string{
		"now()", "NOW()", "random()", "clock_timestamp()", "nextval('s')",
		"gen_random_uuid()", "current_timestamp", "CURRENT_DATE",
		"now() - interval '1 day'", "coalesce(x, now())",
	}
	for _, c := range volatileCases {
		if !IsVolatileDefault(c) {
			t.Errorf("%q should be volatile", c)
		}
	}
	stableCases := []string{"'new'", "0", "false", "'{}'::jsonb", "", "  ", "'nowhere'"}
	for _, c := range stableCases {
		if IsVolatileDefault(c) {
			t.Errorf("%q should not be volatile", c)
		}
	}
}

func TestVolatileSubstringFalsePositive(t *testing.T) {
	// A substring match is the obvious implementation and it is wrong: the
	// literal 'nowhere' contains "now". This test exists to record that the
	// case is handled and to fail loudly if the matcher is ever loosened.
	if IsVolatileDefault("'nowhere'") {
		t.Fatal("'nowhere' must not be treated as now()")
	}
	if IsVolatileDefault("'random thoughts'") {
		t.Fatal("'random thoughts' must not be treated as random()")
	}
	if !IsVolatileDefault("now()") {
		t.Fatal("now() must still be detected")
	}
}

func TestAlterTypeBoundaryAt12(t *testing.T) {
	sql := "ALTER TABLE orders ALTER COLUMN note TYPE text"
	if !Rewrites(one(t, sql), 11) {
		t.Fatal("11 must rewrite on a type change")
	}
	if Rewrites(one(t, sql), 12) {
		t.Fatal("12 can widen varchar to text without a rewrite")
	}
}

func TestAlterTypeWithUsingAlwaysRewrites(t *testing.T) {
	sql := "ALTER TABLE orders ALTER COLUMN amount TYPE bigint USING amount::bigint"
	for _, v := range []int{11, 12, 16} {
		if !Rewrites(one(t, sql), v) {
			t.Fatalf("version %d: a USING clause always rewrites", v)
		}
	}
}

func TestNarrowingTypeAlwaysRewrites(t *testing.T) {
	sql := "ALTER TABLE orders ALTER COLUMN amount TYPE smallint"
	for _, v := range []int{11, 12, 16} {
		if !Rewrites(one(t, sql), v) {
			t.Fatalf("version %d: narrowing must rewrite", v)
		}
	}
}

func TestLockModes(t *testing.T) {
	cases := []struct {
		sql  string
		want locks.Mode
	}{
		{"CREATE INDEX i ON t (a)", locks.Share},
		{"CREATE INDEX CONCURRENTLY i ON t (a)", locks.ShareUpdateExclusive},
		{"DROP INDEX i", locks.AccessExclusive},
		{"DROP INDEX CONCURRENTLY i", locks.ShareUpdateExclusive},
		{"ALTER TABLE t VALIDATE CONSTRAINT c", locks.ShareUpdateExclusive},
		{"ALTER TABLE t ALTER COLUMN a SET STATISTICS 100", locks.ShareUpdateExclusive},
		{"ANALYZE t", locks.ShareUpdateExclusive},
		{"VACUUM t", locks.ShareUpdateExclusive},
		{"CLUSTER t USING i", locks.AccessExclusive},
		{"ALTER TABLE t ADD COLUMN a int", locks.AccessExclusive},
		{"ALTER TABLE t ALTER COLUMN a SET DEFAULT 1", locks.AccessExclusive},
		{"ALTER TABLE t DROP COLUMN a", locks.AccessExclusive},
		{"GRANT SELECT ON t TO alice", locks.AccessExclusive},
	}
	for _, c := range cases {
		if got := LockOf(one(t, c.sql)); got != c.want {
			t.Errorf("%q: lock %s, want %s", c.sql, got, c.want)
		}
	}
}

func TestNonConcurrentIndexDoesNotBlockReads(t *testing.T) {
	// The point people get wrong in the other direction: CREATE INDEX is bad
	// because it blocks writes, not because it blocks reads.
	m := LockOf(one(t, "CREATE INDEX i ON t (a)"))
	if locks.BlocksReads(m) {
		t.Fatal("CREATE INDEX must not block reads")
	}
	if !locks.BlocksWrites(m) {
		t.Fatal("CREATE INDEX must block writes")
	}
}

func TestUnparsedIsRefusedAndAssumedExclusive(t *testing.T) {
	// The fail-safe. If it cannot be understood it must not be waved through.
	r := lintOne(t, "GRANT SELECT ON t TO alice", 16)
	if !r.Refused() {
		t.Fatal("an unparsed statement must be refused")
	}
	if !hasRule(r, "unparsed") {
		t.Fatalf("rules fired: %v", r.Rules())
	}
	if r.Findings[0].Lock != locks.AccessExclusive {
		t.Fatalf("unparsed lock = %s", r.Findings[0].Lock)
	}
}

func TestStatementInsideACommentIsNotLinted(t *testing.T) {
	// End-to-end proof that the parser, not a regex, feeds the linter.
	st, err := ddl.Parse("/* CREATE INDEX i ON t (a); */ ALTER TABLE t ADD COLUMN a int;")
	if err != nil {
		t.Fatal(err)
	}
	r := Lint(st, 16)
	if hasRule(r, "index-not-concurrent") {
		t.Fatal("linted a statement that was inside a comment")
	}
	if r.Refused() {
		t.Fatalf("a safe ADD COLUMN was refused: %v", r.Rules())
	}
}

func TestStringLiteralDoesNotTriggerARule(t *testing.T) {
	r := lintOne(t, "ALTER TABLE t ALTER COLUMN a SET DEFAULT 'DROP TABLE users'", 16)
	if r.Refused() {
		t.Fatalf("refused on the contents of a string literal: %v", r.Rules())
	}
}

func TestRefusalCatalogue(t *testing.T) {
	refused := []string{
		"ALTER TABLE t ADD COLUMN a int NOT NULL",
		"ALTER TABLE t ADD CONSTRAINT c CHECK (a > 0)",
		"ALTER TABLE t ADD CONSTRAINT c FOREIGN KEY (a) REFERENCES b (id)",
		"ALTER TABLE t ADD CONSTRAINT c UNIQUE (a)",
		"ALTER TABLE t ADD CONSTRAINT c PRIMARY KEY (a)",
		"CREATE INDEX i ON t (a)",
		"ALTER TABLE t ALTER COLUMN a TYPE smallint",
		"ALTER TABLE t RENAME COLUMN a TO b",
		"ALTER TABLE t RENAME TO u",
		"CLUSTER t USING i",
		"GRANT SELECT ON t TO alice",
	}
	for _, sql := range refused {
		if !lintOne(t, sql, 16).Refused() {
			t.Errorf("expected REFUSE for %q", sql)
		}
	}

	allowed := []string{
		"ALTER TABLE t ADD COLUMN a int",
		"ALTER TABLE t ADD COLUMN a int DEFAULT 0",
		"ALTER TABLE t ADD CONSTRAINT c CHECK (a > 0) NOT VALID",
		"ALTER TABLE t VALIDATE CONSTRAINT c",
		"CREATE INDEX CONCURRENTLY i ON t (a)",
		"ALTER TABLE t DROP COLUMN a",
		"ALTER TABLE t ALTER COLUMN a SET NOT NULL",
		"ALTER TABLE t ALTER COLUMN a SET DEFAULT 1",
		"ALTER TABLE t DROP CONSTRAINT c",
		"DROP INDEX CONCURRENTLY i",
	}
	for _, sql := range allowed {
		if r := lintOne(t, sql, 16); r.Refused() {
			t.Errorf("unexpected REFUSE for %q: %v", sql, r.Rules())
		}
	}
}

func TestEveryFindingHasAFix(t *testing.T) {
	// A refusal without a remedy is a tool people route around. Every rule
	// must say what to do instead.
	all := []string{
		"ALTER TABLE t ADD COLUMN a int NOT NULL",
		"ALTER TABLE t ADD COLUMN a int",
		"ALTER TABLE t ADD CONSTRAINT c CHECK (a > 0)",
		"CREATE INDEX i ON t (a)",
		"CREATE INDEX CONCURRENTLY i ON t (a)",
		"ALTER TABLE t ALTER COLUMN a TYPE smallint",
		"ALTER TABLE t ALTER COLUMN a TYPE text",
		"ALTER TABLE t ALTER COLUMN a SET NOT NULL",
		"ALTER TABLE t DROP COLUMN a",
		"ALTER TABLE t RENAME COLUMN a TO b",
		"CLUSTER t USING i",
		"DROP INDEX i",
		"GRANT SELECT ON t TO alice",
	}
	for _, sql := range all {
		for _, f := range lintOne(t, sql, 16).Findings {
			if strings.TrimSpace(f.Fix) == "" {
				t.Errorf("%q rule %q has no fix", sql, f.Rule)
			}
			if strings.TrimSpace(f.Reason) == "" {
				t.Errorf("%q rule %q has no reason", sql, f.Rule)
			}
			if f.Rule == "" {
				t.Errorf("%q produced an unnamed finding", sql)
			}
		}
	}
}

func TestEveryStatementProducesAtLeastOneFinding(t *testing.T) {
	// Silence is indistinguishable from "not analysed". The linter always
	// says something, even if it is "ok".
	for _, sql := range []string{
		"ALTER TABLE t ADD COLUMN a int",
		"ALTER TABLE t VALIDATE CONSTRAINT c",
		"ANALYZE t",
		"VACUUM t",
		"CREATE TABLE t (a int)",
		"DROP TABLE t",
		"ALTER TABLE t DROP CONSTRAINT c",
	} {
		if n := len(lintOne(t, sql, 16).Findings); n == 0 {
			t.Errorf("%q produced no findings", sql)
		}
	}
}

func TestScans(t *testing.T) {
	scanning := []string{
		"ALTER TABLE t ADD CONSTRAINT c CHECK (a > 0)",
		"ALTER TABLE t ADD CONSTRAINT c FOREIGN KEY (a) REFERENCES b (id)",
		"ALTER TABLE t ALTER COLUMN a SET NOT NULL",
		"ALTER TABLE t ADD CONSTRAINT c UNIQUE (a)",
	}
	for _, sql := range scanning {
		if !Scans(one(t, sql), 16) {
			t.Errorf("%q should scan", sql)
		}
	}
	notScanning := []string{
		"ALTER TABLE t ADD CONSTRAINT c CHECK (a > 0) NOT VALID",
		"ALTER TABLE t VALIDATE CONSTRAINT c",
		"ALTER TABLE t ADD COLUMN a int",
		"ALTER TABLE t DROP COLUMN a",
	}
	for _, sql := range notScanning {
		if Scans(one(t, sql), 16) {
			t.Errorf("%q should not scan", sql)
		}
	}
}

func TestScanIsNotRewrite(t *testing.T) {
	// The category people forget: a validating CHECK does not rewrite
	// anything, so it is invisible to any "does this rewrite the table"
	// check, and it still holds ACCESS EXCLUSIVE for a full sequential scan.
	s := one(t, "ALTER TABLE t ADD CONSTRAINT c CHECK (a > 0)")
	if Rewrites(s, 16) {
		t.Fatal("a validating CHECK does not rewrite")
	}
	if !Scans(s, 16) {
		t.Fatal("a validating CHECK does scan")
	}
	if LockOf(s) != locks.AccessExclusive {
		t.Fatal("and it does so under ACCESS EXCLUSIVE")
	}
}

func TestNotValidEscapesTheScan(t *testing.T) {
	s := one(t, "ALTER TABLE t ADD CONSTRAINT c CHECK (a > 0) NOT VALID")
	if Scans(s, 16) {
		t.Fatal("NOT VALID must not scan")
	}
	v := one(t, "ALTER TABLE t VALIDATE CONSTRAINT c")
	if LockOf(v) != locks.ShareUpdateExclusive {
		t.Fatalf("VALIDATE takes %s", LockOf(v))
	}
	if locks.BlocksWrites(LockOf(v)) {
		t.Fatal("VALIDATE must not block writes -- that is the whole point")
	}
}

func TestConcurrentIndexStillWarns(t *testing.T) {
	// CONCURRENTLY is the right answer and it is not free: it can leave an
	// INVALID index that no query uses and nothing rebuilds.
	r := lintOne(t, "CREATE INDEX CONCURRENTLY i ON t (a)", 16)
	if r.Refused() {
		t.Fatal("CONCURRENTLY must not be refused")
	}
	if r.Count(Warn) == 0 {
		t.Fatalf("expected a warning about INVALID indexes, got %v", r.Rules())
	}
}

func TestReportCounts(t *testing.T) {
	st, err := ddl.Parse(`
		CREATE INDEX i ON t (a);
		ALTER TABLE t ADD COLUMN b int;
		ALTER TABLE t DROP COLUMN c;
	`)
	if err != nil {
		t.Fatal(err)
	}
	r := Lint(st, 16)
	if r.Count(Refuse) != 1 {
		t.Fatalf("refusals = %d", r.Count(Refuse))
	}
	if r.Count(Warn) != 1 {
		t.Fatalf("warnings = %d", r.Count(Warn))
	}
	if !r.Refused() {
		t.Fatal("Refused() disagrees with Count(Refuse)")
	}
	rules := r.Rules()
	for i := 1; i < len(rules); i++ {
		if rules[i-1] >= rules[i] {
			t.Fatalf("Rules() is not sorted and deduplicated: %v", rules)
		}
	}
}

func TestSeverityStrings(t *testing.T) {
	for _, s := range []Severity{Info, Warn, Refuse} {
		if s.String() == "?" {
			t.Fatalf("severity %d has no name", int(s))
		}
	}
}

func TestVersionSweepChangesTheVerdict(t *testing.T) {
	// The headline: hold the script constant, sweep the version, count the
	// refusals. If this ever returns a flat line the version modelling has
	// stopped doing anything.
	script := `
		ALTER TABLE orders ADD COLUMN status text DEFAULT 'new';
		ALTER TABLE orders ALTER COLUMN note TYPE text;
	`
	st, err := ddl.Parse(script)
	if err != nil {
		t.Fatal(err)
	}
	counts := map[int]int{}
	for _, v := range []int{10, 11, 12, 16} {
		counts[v] = Lint(st, v).Count(Refuse)
	}
	if counts[10] <= counts[16] {
		t.Fatalf("expected more refusals on older versions: %v", counts)
	}
	if counts[10] != 2 || counts[11] != 1 || counts[12] != 0 || counts[16] != 0 {
		t.Fatalf("version sweep = %v; want 10:2 11:1 12:0 16:0", counts)
	}
}
