package plan

import (
	"strings"
	"testing"

	"evolve/ddl"
	"evolve/lint"
)

func rules(ps []Problem) map[string]bool {
	m := map[string]bool{}
	for _, p := range ps {
		m[p.Rule] = true
	}
	return m
}

func TestValidPlanHasNoProblems(t *testing.T) {
	m := Migration{Name: "n", Table: "t", Steps: []Step{
		{Phase: Expand, SQL: "ALTER TABLE t ADD COLUMN a int",
			Rollback: "ALTER TABLE t DROP COLUMN a", TestedRollback: true},
		{Phase: Migrate, SQL: "ALTER TABLE t ADD CONSTRAINT c CHECK (a IS NOT NULL) NOT VALID",
			Rollback: "ALTER TABLE t DROP CONSTRAINT c", TestedRollback: true},
		{Phase: Contract, SQL: "ALTER TABLE t DROP COLUMN b",
			Justification: "unreferenced for a full release cycle"},
	}}
	if ps := m.Validate(); len(ps) != 0 {
		t.Fatalf("unexpected problems: %+v", ps)
	}
}

func TestPhasesMustNotGoBackwards(t *testing.T) {
	// Contracting before migrating removes the old shape while deployed code
	// still reads it.
	m := Migration{Steps: []Step{
		{Phase: Expand, SQL: "ALTER TABLE t ADD COLUMN a int",
			Rollback: "ALTER TABLE t DROP COLUMN a", TestedRollback: true},
		{Phase: Contract, SQL: "ALTER TABLE t DROP COLUMN b", Justification: "j"},
		{Phase: Migrate, SQL: "ALTER TABLE t VALIDATE CONSTRAINT c",
			Rollback: "-- none needed", TestedRollback: true},
	}}
	if !rules(m.Validate())["phase-order"] {
		t.Fatalf("phase order violation not detected: %+v", m.Validate())
	}
}

func TestDestructiveStepOutsideContractIsRejected(t *testing.T) {
	for _, sql := range []string{
		"ALTER TABLE t DROP COLUMN a",
		"DROP TABLE t",
		"ALTER TABLE t RENAME COLUMN a TO b",
		"ALTER TABLE t RENAME TO u",
	} {
		m := Migration{Steps: []Step{
			{Phase: Expand, SQL: sql, Rollback: "-- x", TestedRollback: true},
		}}
		if !rules(m.Validate())["destructive-outside-contract"] {
			t.Errorf("%q in the expand phase was accepted", sql)
		}
	}
}

func TestMissingRollbackOutsideContract(t *testing.T) {
	m := Migration{Steps: []Step{
		{Phase: Expand, SQL: "ALTER TABLE t ADD COLUMN a int"},
	}}
	if !rules(m.Validate())["missing-rollback"] {
		t.Fatal("a rollback-less expand step was accepted")
	}
}

func TestIrreversibleContractStepNeedsJustification(t *testing.T) {
	m := Migration{Steps: []Step{
		{Phase: Expand, SQL: "ALTER TABLE t ADD COLUMN a int",
			Rollback: "ALTER TABLE t DROP COLUMN a", TestedRollback: true},
		{Phase: Contract, SQL: "ALTER TABLE t DROP COLUMN b"},
	}}
	if !rules(m.Validate())["unjustified-irreversible"] {
		t.Fatal("an unjustified irreversible step was accepted")
	}
}

func TestUntestedRollbackIsAProblem(t *testing.T) {
	// The rollback path is the least exercised code in the system and the one
	// that runs under the most pressure. A declared, never-executed rollback
	// is a comment.
	m := Migration{Steps: []Step{
		{Phase: Expand, SQL: "ALTER TABLE t ADD COLUMN a int",
			Rollback: "ALTER TABLE t DROP COLUMN a", TestedRollback: false},
	}}
	if !rules(m.Validate())["untested-rollback"] {
		t.Fatal("an untested rollback was accepted")
	}
}

func TestUnparsedStepIsAProblem(t *testing.T) {
	m := Migration{Steps: []Step{
		{Phase: Expand, SQL: "GRANT SELECT ON t TO alice",
			Rollback: "REVOKE", TestedRollback: true},
	}}
	if !rules(m.Validate())["unparsed"] {
		t.Fatalf("%+v", m.Validate())
	}
}

func TestEmptyPlanIsVacuouslyValid(t *testing.T) {
	if ps := (Migration{}).Validate(); len(ps) != 0 {
		t.Fatalf("empty plan produced %+v", ps)
	}
}

func TestPlanWithoutExpandPhase(t *testing.T) {
	m := Migration{Steps: []Step{
		{Phase: Contract, SQL: "ALTER TABLE t DROP COLUMN b", Justification: "j"},
	}}
	if !rules(m.Validate())["no-expand-phase"] {
		t.Fatal("a contract-only plan was accepted")
	}
}

func TestProblemRulesSortedAndUnique(t *testing.T) {
	m := Migration{Steps: []Step{
		{Phase: Expand, SQL: "ALTER TABLE t DROP COLUMN a"},
		{Phase: Expand, SQL: "ALTER TABLE t DROP COLUMN b"},
	}}
	rs := ProblemRules(m.Validate())
	for i := 1; i < len(rs); i++ {
		if rs[i-1] >= rs[i] {
			t.Fatalf("not sorted and unique: %v", rs)
		}
	}
}

// Rewrite is the half of the tool that makes it usable. A linter that only
// refuses gets a blanket exemption in CI; one that produces the safe version
// gets adopted.

func TestRewriteAddColumnNotNull(t *testing.T) {
	m, ok := Rewrite("ALTER TABLE orders ADD COLUMN status text NOT NULL", 16)
	if !ok {
		t.Fatal("no rewrite produced")
	}
	if ps := m.Validate(); len(ps) != 0 {
		t.Fatalf("the generated plan does not pass its own validator: %+v", ps)
	}
	// The generated plan must itself lint clean.
	r := m.Lint(16)
	if r.Refused() {
		t.Fatalf("the generated plan is refused by the linter: %v", r.Rules())
	}
	if len(m.Steps) < 5 {
		t.Fatalf("expected a multi-step plan, got %d", len(m.Steps))
	}
	// It must reach SET NOT NULL, which is the point of the exercise.
	found := false
	for _, s := range m.Steps {
		if strings.Contains(s.SQL, "SET NOT NULL") {
			found = true
		}
	}
	if !found {
		t.Fatal("the plan never establishes the NOT NULL constraint")
	}
}

func TestRewritePreservesSchemaQualification(t *testing.T) {
	// The regression that matters most: a rewrite that drops the schema
	// generates DDL against whatever search_path resolves to.
	m, ok := Rewrite("ALTER TABLE archive.orders ADD COLUMN status text NOT NULL", 16)
	if !ok {
		t.Fatal("no rewrite produced")
	}
	for _, s := range m.Steps {
		if strings.Contains(s.SQL, "ALTER TABLE") && !strings.Contains(s.SQL, "archive.orders") {
			t.Fatalf("generated SQL lost the schema: %q", s.SQL)
		}
		if s.Rollback != "" && strings.Contains(s.Rollback, "ALTER TABLE") &&
			!strings.Contains(s.Rollback, "archive.orders") {
			t.Fatalf("generated rollback lost the schema: %q", s.Rollback)
		}
	}
}

func TestRewriteIndex(t *testing.T) {
	m, ok := Rewrite("CREATE INDEX idx_o ON orders (ref)", 16)
	if !ok {
		t.Fatal("no rewrite")
	}
	if !strings.Contains(m.Steps[0].SQL, "CONCURRENTLY") {
		t.Fatalf("first step: %q", m.Steps[0].SQL)
	}
	if m.Lint(16).Refused() {
		t.Fatalf("generated plan refused: %v", m.Lint(16).Rules())
	}
	// It must also check indisvalid: CREATE INDEX CONCURRENTLY can fail and
	// leave a dead index nothing rebuilds.
	joined := m.Summary()
	if !strings.Contains(joined, "indisvalid") {
		t.Fatal("the plan does not verify the index actually built")
	}
}

func TestAlreadySafeStatementsAreNotRewritten(t *testing.T) {
	for _, sql := range []string{
		"CREATE INDEX CONCURRENTLY i ON t (a)",
		"ALTER TABLE t ADD CONSTRAINT c CHECK (a > 0) NOT VALID",
		"ALTER TABLE t ADD COLUMN a int",
	} {
		if _, ok := Rewrite(sql, 16); ok {
			t.Errorf("%q was rewritten but is already safe", sql)
		}
	}
}

func TestRewriteConstraint(t *testing.T) {
	m, ok := Rewrite("ALTER TABLE t ADD CONSTRAINT ck CHECK (a > 0)", 16)
	if !ok {
		t.Fatal("no rewrite")
	}
	if !strings.Contains(m.Steps[0].SQL, "NOT VALID") {
		t.Fatalf("first step: %q", m.Steps[0].SQL)
	}
	if !strings.Contains(m.Steps[1].SQL, "VALIDATE CONSTRAINT") {
		t.Fatalf("second step: %q", m.Steps[1].SQL)
	}
	if m.Lint(16).Refused() {
		t.Fatalf("generated plan refused: %v", m.Lint(16).Rules())
	}
}

func TestRewriteUnique(t *testing.T) {
	m, ok := Rewrite("ALTER TABLE t ADD CONSTRAINT uq UNIQUE (a)", 16)
	if !ok {
		t.Fatal("no rewrite")
	}
	if !strings.Contains(m.Steps[0].SQL, "CREATE UNIQUE INDEX CONCURRENTLY") {
		t.Fatalf("first step: %q", m.Steps[0].SQL)
	}
	if !strings.Contains(m.Steps[1].SQL, "USING INDEX") {
		t.Fatalf("second step: %q", m.Steps[1].SQL)
	}
}

func TestRewriteRenameIsTheFullDance(t *testing.T) {
	// A rename is the case where the safe version looks nothing like the
	// original: six steps, a trigger, a backfill and a release cycle of
	// waiting, to replace one statement.
	m, ok := Rewrite("ALTER TABLE orders RENAME COLUMN ref TO reference", 16)
	if !ok {
		t.Fatal("no rewrite")
	}
	if len(m.Steps) < 6 {
		t.Fatalf("expected at least 6 steps, got %d", len(m.Steps))
	}
	if ps := m.Validate(); len(ps) != 0 {
		t.Fatalf("generated rename plan fails validation: %+v", ps)
	}
	last := m.Steps[len(m.Steps)-1]
	if last.Phase != Contract {
		t.Fatal("the drop must be in the contract phase")
	}
	if last.Rollback != "" {
		t.Fatal("dropping a column is irreversible; it must not claim a rollback")
	}
	if last.Justification == "" {
		t.Fatal("and it must justify itself")
	}
}

func TestEveryGeneratedPlanValidates(t *testing.T) {
	// The strongest property: whatever the linter refuses, the rewriter turns
	// into something that passes both the validator and the linter.
	inputs := []string{
		"ALTER TABLE t ADD COLUMN a int NOT NULL",
		"CREATE INDEX i ON t (a)",
		"CREATE UNIQUE INDEX i ON t (a)",
		"ALTER TABLE t ADD CONSTRAINT c CHECK (a > 0)",
		"ALTER TABLE t ADD CONSTRAINT c FOREIGN KEY (a) REFERENCES b (id)",
		"ALTER TABLE t ADD CONSTRAINT c UNIQUE (a)",
		"ALTER TABLE t ADD CONSTRAINT c PRIMARY KEY (a)",
		"ALTER TABLE t RENAME COLUMN a TO b",
	}
	for _, sql := range inputs {
		m, ok := Rewrite(sql, 16)
		if !ok {
			t.Errorf("%q: no rewrite produced", sql)
			continue
		}
		if ps := m.Validate(); len(ps) != 0 {
			t.Errorf("%q: generated plan fails validation: %+v", sql, ps)
		}
		if len(m.Steps) == 0 {
			t.Errorf("%q: empty plan", sql)
		}
	}
}

func TestRewriteOnOlderVersion(t *testing.T) {
	// On PostgreSQL 10 an ADD COLUMN with a constant default rewrites, so it
	// needs a plan; on 16 it does not.
	sql := "ALTER TABLE t ADD COLUMN a text DEFAULT 'x'"
	if _, ok := Rewrite(sql, 16); ok {
		t.Fatal("safe on 16, should not be rewritten")
	}
	if _, ok := Rewrite(sql, 10); !ok {
		t.Fatal("unsafe on 10, should be rewritten")
	}
}

func TestRewriteRejectsMultipleStatements(t *testing.T) {
	if _, ok := Rewrite("ALTER TABLE t ADD COLUMN a int; ALTER TABLE t ADD COLUMN b int", 16); ok {
		t.Fatal("rewrote a multi-statement script")
	}
}

func TestRewriteRejectsUnparsed(t *testing.T) {
	if _, ok := Rewrite("GRANT SELECT ON t TO alice", 16); ok {
		t.Fatal("rewrote something it could not parse")
	}
}

func TestSummaryGroupsByPhaseInOrder(t *testing.T) {
	m, _ := Rewrite("ALTER TABLE t ADD COLUMN a int NOT NULL", 16)
	s := m.Summary()
	e, mi, c := strings.Index(s, "expand:"), strings.Index(s, "migrate:"), strings.Index(s, "contract:")
	if e < 0 || mi < 0 || c < 0 {
		t.Fatalf("missing a phase heading:\n%s", s)
	}
	if !(e < mi && mi < c) {
		t.Fatalf("phases out of order in the summary:\n%s", s)
	}
}

func TestSummaryIsDeterministic(t *testing.T) {
	m, _ := Rewrite("ALTER TABLE t ADD COLUMN a int NOT NULL", 16)
	first := m.Summary()
	for i := 0; i < 50; i++ {
		if m.Summary() != first {
			t.Fatal("Summary varies between calls")
		}
	}
}

func TestLintOfAPlanSeesEveryStep(t *testing.T) {
	m := Migration{Steps: []Step{
		{Phase: Expand, SQL: "CREATE INDEX i ON t (a)", Rollback: "x", TestedRollback: true},
		{Phase: Expand, SQL: "ALTER TABLE t ADD COLUMN b int", Rollback: "y", TestedRollback: true},
	}}
	r := m.Lint(16)
	if len(r.Findings) < 2 {
		t.Fatalf("only %d findings for 2 steps", len(r.Findings))
	}
	if !r.Refused() {
		t.Fatal("the non-concurrent index build should be refused")
	}
}

func TestLintOfAPlanHandlesUnparsedSteps(t *testing.T) {
	m := Migration{Steps: []Step{
		{Phase: Migrate, SQL: "-- a comment-only step", Rollback: "x", TestedRollback: true},
	}}
	r := m.Lint(16)
	if len(r.Findings) == 0 {
		t.Fatal("a comment-only step produced no findings at all")
	}
	if r.Findings[0].Stmt.Kind != ddl.Unknown {
		t.Fatalf("kind %s", r.Findings[0].Stmt.Kind)
	}
	if r.Findings[0].Severity != lint.Refuse {
		t.Fatal("an unanalysable step must not be waved through")
	}
}

func TestPhaseStrings(t *testing.T) {
	for _, p := range []Phase{Expand, Migrate, Contract} {
		if p.String() == "?" {
			t.Fatalf("phase %d has no name", int(p))
		}
	}
}

// Manual steps. The first version of this model inferred "procedural note"
// from "the parser produced nothing", which meant a typo was indistinguishable
// from prose. These tests pin the explicit version.

func TestManualStepIsNotLinted(t *testing.T) {
	m := Migration{Steps: []Step{
		{Phase: Expand, SQL: "ALTER TABLE t ADD COLUMN a int",
			Rollback: "ALTER TABLE t DROP COLUMN a", TestedRollback: true},
		{Phase: Migrate, SQL: "-- backfill in batches", Manual: true,
			Rollback: "UPDATE t SET a = NULL", TestedRollback: true},
	}}
	if ps := m.Validate(); len(ps) != 0 {
		t.Fatalf("a manual step was treated as broken SQL: %+v", ps)
	}
	if m.Lint(16).Refused() {
		t.Fatalf("a manual step was linted: %v", m.Lint(16).Rules())
	}
}

func TestManualStepStillNeedsARollback(t *testing.T) {
	// Being procedural does not exempt a step from the rollback obligation --
	// a backfill that half-ran is exactly the case where you need one.
	m := Migration{Steps: []Step{
		{Phase: Expand, SQL: "ALTER TABLE t ADD COLUMN a int",
			Rollback: "ALTER TABLE t DROP COLUMN a", TestedRollback: true},
		{Phase: Migrate, SQL: "-- backfill in batches", Manual: true},
	}}
	if !rules(m.Validate())["missing-rollback"] {
		t.Fatalf("a manual step escaped the rollback requirement: %+v", m.Validate())
	}
}

func TestTypoIsNotMistakenForProse(t *testing.T) {
	// The defect the Manual flag exists to prevent.
	m := Migration{Steps: []Step{
		{Phase: Expand, SQL: "ALTER TABEL orders ADD COLUMN a int",
			Rollback: "x", TestedRollback: true},
	}}
	if !rules(m.Validate())["unparsed"] {
		t.Fatalf("a typo passed validation: %+v", m.Validate())
	}
	if !m.Lint(16).Refused() {
		t.Fatal("a typo was not refused by the linter")
	}
}

func TestEmptySQLStepIsFlagged(t *testing.T) {
	m := Migration{Steps: []Step{
		{Phase: Expand, SQL: "   ", Rollback: "x", TestedRollback: true},
	}}
	if !rules(m.Validate())["empty-step"] {
		t.Fatalf("an empty non-manual step passed: %+v", m.Validate())
	}
}

func TestValidateChecksEveryStatementInAStep(t *testing.T) {
	// A step containing several statements must not be judged by its first.
	m := Migration{Steps: []Step{
		{Phase: Expand,
			SQL:      "ALTER TABLE t ADD COLUMN a int; ALTER TABLE t DROP COLUMN b;",
			Rollback: "x", TestedRollback: true},
	}}
	if !rules(m.Validate())["destructive-outside-contract"] {
		t.Fatalf("a destructive second statement was missed: %+v", m.Validate())
	}
}

func TestGeneratedManualStepsAreMarked(t *testing.T) {
	// Every generated step that is not executable DDL must say so, or the
	// generated plan fails its own validator.
	for _, sql := range []string{
		"ALTER TABLE t ADD COLUMN a int NOT NULL",
		"CREATE INDEX i ON t (a)",
		"ALTER TABLE t ADD CONSTRAINT c UNIQUE (a)",
		"ALTER TABLE orders RENAME COLUMN ref TO reference",
	} {
		m, ok := Rewrite(sql, 16)
		if !ok {
			t.Fatalf("%q: no rewrite", sql)
		}
		for i, s := range m.Steps {
			if s.Manual {
				continue
			}
			stmts, err := ddl.Parse(s.SQL)
			if err != nil || len(stmts) == 0 || stmts[0].Kind == ddl.Unknown {
				t.Errorf("%q step %d is unparseable but not marked Manual: %q", sql, i, s.SQL)
			}
		}
	}
}

// TestSafeNotNullDefaultIsNotRewritten pins the fix for a bug where the
// rewriter generated a seven-step expand/migrate/contract plan for a statement
// that PostgreSQL 11 and later execute in milliseconds.
//
// ADD COLUMN ... NOT NULL DEFAULT <constant> is catalogue-only from 11: the
// default is recorded as a missing value, every existing row reads it back
// without being written, and NOT NULL is therefore satisfied by construction.
// The old guard was `if !s.NotNull && !Rewrites(...)`, which fired on NotNull
// alone and never asked whether a default made it safe.
//
// Generating ceremony for safe statements is not the harmless direction of
// error it looks like. It is how a tool teaches its users that its output is
// noise, and the next time it produces seven steps that genuinely matter,
// nobody reads them.
func TestSafeNotNullDefaultIsNotRewritten(t *testing.T) {
	const stmt = "ALTER TABLE orders ADD COLUMN status text NOT NULL DEFAULT 'new'"

	if _, ok := Rewrite(stmt, 16); ok {
		t.Error("rewrote a statement that is catalogue-only on PostgreSQL 16")
	}
	if _, ok := Rewrite(stmt, 11); ok {
		t.Error("rewrote a statement that is catalogue-only on PostgreSQL 11")
	}
	// On 10 the missing-value optimisation does not exist, so the same
	// statement rewrites the whole table and must be rewritten.
	if _, ok := Rewrite(stmt, 10); !ok {
		t.Error("PostgreSQL 10 rewrites the table here; a plan was expected")
	}
	// A volatile default is never catalogue-only: each row needs its own value.
	if _, ok := Rewrite("ALTER TABLE orders ADD COLUMN id uuid NOT NULL DEFAULT gen_random_uuid()", 16); !ok {
		t.Error("a volatile default forces a rewrite on every version")
	}
}

// TestUniqueIndexRewriteStaysUnique pins the fix for a rewriter that used
// strings.Replace(raw, "CREATE INDEX", "CREATE INDEX CONCURRENTLY", 1).
//
// "CREATE UNIQUE INDEX ..." does not contain the substring "CREATE INDEX", so
// the replace matched nothing and returned the input unchanged. The generated
// plan therefore contained the original blocking build, presented as the safe
// version. The step still validated, because it was valid SQL -- it was just
// the wrong SQL.
func TestUniqueIndexRewriteStaysUnique(t *testing.T) {
	m, ok := Rewrite("CREATE UNIQUE INDEX idx_orders_ref ON public.orders (ref)", 16)
	if !ok {
		t.Fatal("no rewrite produced")
	}
	sql := m.Steps[0].SQL
	if !strings.Contains(sql, "CONCURRENTLY") {
		t.Errorf("rewrite is not concurrent: %q", sql)
	}
	if !strings.Contains(sql, "UNIQUE") {
		t.Errorf("rewrite dropped UNIQUE, which changes what the index enforces: %q", sql)
	}
	if !strings.Contains(sql, "public.orders") {
		t.Errorf("rewrite dropped the schema qualifier: %q", sql)
	}
}

// TestIndexRewritePreservesPartialPredicate checks that a WHERE clause
// survives the rewrite. Rebuilding the statement from parsed fields alone
// would drop it, and a partial index silently promoted to a full index has
// different size, different cost, and -- for a unique index -- different
// semantics.
func TestIndexRewritePreservesPartialPredicate(t *testing.T) {
	m, ok := Rewrite("CREATE INDEX idx_live ON orders (ref) WHERE deleted_at IS NULL", 16)
	if !ok {
		t.Fatal("no rewrite produced")
	}
	if !strings.Contains(m.Steps[0].SQL, "WHERE deleted_at IS NULL") {
		t.Errorf("partial predicate lost: %q", m.Steps[0].SQL)
	}
}

// TestRewriteOutputSurvivesItsOwnLinter is the property that matters most in
// this package, and the one that caught two separate bugs.
//
// Every plan this tool generates is the answer it gives to someone who asked
// "how do I do this safely?". If re-linting that answer produces a Refuse, the
// tool has contradicted itself, and the person following its advice is running
// something it would have blocked.
func TestRewriteOutputSurvivesItsOwnLinter(t *testing.T) {
	refused := []string{
		"ALTER TABLE orders ADD COLUMN status text NOT NULL",
		"ALTER TABLE orders ALTER COLUMN ref TYPE varchar(20)",
		"ALTER TABLE orders ALTER COLUMN qty SET NOT NULL",
		"CREATE INDEX idx_orders_ref ON orders (ref)",
		"CREATE UNIQUE INDEX idx_orders_ref ON public.orders (ref)",
		"ALTER TABLE public.orders ADD CONSTRAINT uq_ref UNIQUE (ref)",
		"ALTER TABLE orders ADD CONSTRAINT ck_amt CHECK (amount > 0)",
		"ALTER TABLE orders ADD CONSTRAINT fk_c FOREIGN KEY (c) REFERENCES cust (id)",
	}
	for _, stmt := range refused {
		m, ok := Rewrite(stmt, 16)
		if !ok {
			t.Errorf("%q: refused with no rewrite offered", stmt)
			continue
		}
		if errs := m.Validate(); len(errs) != 0 {
			t.Errorf("%q: generated plan does not validate: %v", stmt, errs)
		}
		for _, f := range m.Lint(16).Findings {
			if f.Severity == lint.Refuse {
				t.Errorf("%q: the tool refuses its own advice: [%s] %s",
					stmt, f.Rule, f.Reason)
			}
		}
	}
}

// TestAdoptedIndexIsNotRefused isolates the linter half of the contradiction
// above. ADD CONSTRAINT ... USING INDEX is the recommended second step of
// every unique-constraint rewrite, and the linter used to refuse it, because
// the rule matched on statement kind without reading the USING INDEX clause
// that changes what the statement does.
func TestAdoptedIndexIsNotRefused(t *testing.T) {
	inline := lintOf(t, "ALTER TABLE orders ADD CONSTRAINT uq UNIQUE (ref)")
	adopted := lintOf(t, "ALTER TABLE orders ADD CONSTRAINT uq UNIQUE USING INDEX idx_uq")

	if !hasSeverity(inline, lint.Refuse) {
		t.Error("an inline unique constraint builds its index under ACCESS EXCLUSIVE and must be refused")
	}
	if hasSeverity(adopted, lint.Refuse) {
		t.Errorf("USING INDEX is catalogue-only and must not be refused: %v", adopted)
	}
	if len(adopted) == 0 {
		t.Error("USING INDEX still takes a brief ACCESS EXCLUSIVE lock; that deserves a note, not silence")
	}
}

func lintOf(t *testing.T, sql string) []lint.Finding {
	t.Helper()
	stmts, err := ddl.Parse(sql)
	if err != nil {
		t.Fatalf("parse %q: %v", sql, err)
	}
	return lint.Lint(stmts, 16).Findings
}

func hasSeverity(fs []lint.Finding, s lint.Severity) bool {
	for _, f := range fs {
		if f.Severity == s {
			return true
		}
	}
	return false
}

// TestShadowColumnPlanIsComplete checks the type-change rewrite end to end.
//
// This is the most invasive plan the tool produces, and the one where an
// omission is least likely to be noticed in review: every individual step
// looks reasonable, and the failure is a missing step rather than a wrong one.
func TestShadowColumnPlanIsComplete(t *testing.T) {
	m, ok := Rewrite("ALTER TABLE public.orders ALTER COLUMN ref TYPE varchar(20)", 16)
	if !ok {
		t.Fatal("no rewrite produced for a narrowing type change")
	}
	joined := strings.Join(sqlOf(m), "\n")

	// Each of these is load-bearing, and each has a specific failure mode if
	// it is missing.
	must := map[string]string{
		"ADD COLUMN ref_new":  "without the shadow column there is nothing to migrate into",
		"TRIGGER":             "without the trigger the backfill races writes and never converges",
		"backfill":            "without the backfill the shadow column is NULL for every pre-existing row",
		"IS DISTINCT FROM":    "without the verification a lossy cast ships silently",
		"RENAME COLUMN":       "without the swap the application never sees the new type",
		"DROP COLUMN ref_old": "without the drop the table carries both columns forever",
	}
	for frag, why := range must {
		if !strings.Contains(joined, frag) {
			t.Errorf("plan is missing %q: %s", frag, why)
		}
	}

	// Order matters more than content here: verifying after the swap, or
	// dropping the trigger before the swap, produces a plan that passes every
	// content check above and still loses data.
	assertOrder(t, joined,
		"ADD COLUMN ref_new", "TRIGGER", "backfill", "IS DISTINCT FROM",
		"RENAME COLUMN", "DROP COLUMN ref_old")

	if got := m.Steps[len(m.Steps)-1]; got.Rollback != "" || got.Justification == "" {
		t.Error("the irreversible final step must declare itself irreversible and justify it")
	}
}

// TestSetNotNullPlanIsVersionHonest checks that the tool does not offer a plan
// it cannot deliver. The CHECK-then-SET-NOT-NULL trick only skips the scan
// from PostgreSQL 12; before that the scan is unavoidable, and emitting the
// same five steps would be theatre that ends in exactly the outage the user
// was trying to avoid.
func TestSetNotNullPlanIsVersionHonest(t *testing.T) {
	const stmt = "ALTER TABLE orders ALTER COLUMN qty SET NOT NULL"

	m, ok := Rewrite(stmt, 12)
	if !ok {
		t.Fatal("PostgreSQL 12 supports the CHECK-constraint route; a plan was expected")
	}
	joined := strings.Join(sqlOf(m), "\n")
	assertOrder(t, joined, "NOT VALID", "VALIDATE CONSTRAINT", "SET NOT NULL", "DROP CONSTRAINT")

	if _, ok := Rewrite(stmt, 11); ok {
		t.Error("PostgreSQL 11 cannot skip the scan; offering a plan here would be dishonest")
	}
}

// TestDischargeDowngradesButDoesNotDelete pins the contract of the discharge
// mechanism, which is the one place a plan is allowed to answer back to the
// linter. If a discharge could remove a finding, the mechanism would be a
// general-purpose mute button and every future plan would grow one.
func TestDischargeDowngradesButDoesNotDelete(t *testing.T) {
	rename := Step{Phase: Contract, SQL: "ALTER TABLE orders RENAME COLUMN a TO b",
		Rollback: "ALTER TABLE orders RENAME COLUMN b TO a", TestedRollback: true}

	bare := Migration{Name: "n", Table: "orders", Steps: []Step{rename}}
	if !bare.Lint(16).Refused() {
		t.Fatal("an unmitigated rename must be refused")
	}

	guarded := Migration{Name: "n", Table: "orders", Steps: []Step{
		{Phase: Contract, Manual: true, SQL: "-- deploy dual-reading code",
			Justification: "both names are readable for a full deploy cycle",
			Discharges:    []string{"rename-breaks-deployed-code"},
			Rollback:      "-- redeploy", TestedRollback: true},
		rename,
	}}
	rep := guarded.Lint(16)
	if rep.Refused() {
		t.Error("a discharged rename must not be refused")
	}

	var found bool
	for _, f := range rep.Findings {
		if f.Rule != "rename-breaks-deployed-code" {
			continue
		}
		found = true
		if !f.Discharged {
			t.Error("the finding must be marked as discharged, not silently downgraded")
		}
		if !strings.Contains(f.Reason, "discharged by this plan:") {
			t.Errorf("the discharge argument must appear in the report: %q", f.Reason)
		}
		if !strings.Contains(f.Reason, "full deploy cycle") {
			t.Errorf("the justification itself must be carried through: %q", f.Reason)
		}
	}
	if !found {
		t.Error("the finding was deleted; discharge must downgrade and annotate, never remove")
	}
}

// TestDischargeIsRuleScoped checks that naming one rule does not mute another.
func TestDischargeIsRuleScoped(t *testing.T) {
	m := Migration{Name: "n", Table: "orders", Steps: []Step{
		{Phase: Contract, Manual: true, SQL: "-- unrelated note",
			Justification: "unrelated",
			Discharges:    []string{"some-other-rule"},
			Rollback:      "-- none", TestedRollback: true},
		{Phase: Contract, SQL: "ALTER TABLE orders RENAME COLUMN a TO b",
			Rollback: "ALTER TABLE orders RENAME COLUMN b TO a", TestedRollback: true},
	}}
	if !m.Lint(16).Refused() {
		t.Error("discharging an unrelated rule must not clear rename-breaks-deployed-code")
	}
}

func sqlOf(m Migration) []string {
	out := make([]string, 0, len(m.Steps))
	for _, s := range m.Steps {
		out = append(out, s.SQL)
	}
	return out
}

func assertOrder(t *testing.T, hay string, frags ...string) {
	t.Helper()
	prev := -1
	for _, f := range frags {
		i := strings.Index(hay, f)
		if i < 0 {
			t.Errorf("missing %q", f)
			return
		}
		if i < prev {
			t.Errorf("%q appears out of order", f)
			return
		}
		prev = i
	}
}
