// Package plan models expand -> migrate -> contract, and the rollback
// obligation that makes it safe.
//
// The pattern exists because a schema change and a code deployment cannot be
// made atomic across a fleet. There is always a window in which some
// instances run the old code and some run the new, and a migration that is
// only correct at one end of that window is a migration that breaks
// production during the rollout rather than after it.
//
// The phases are:
//
//	expand   -- add the new shape. Old code must keep working unchanged.
//	migrate  -- backfill and switch readers. Both shapes are live.
//	contract -- remove the old shape. Only safe once no deployed code
//	            references it, which is a claim about the fleet, not the
//	            database.
package plan

import (
	"fmt"
	"sort"
	"strings"

	"evolve/ddl"
	"evolve/lint"
)

// Phase of the migration.
type Phase int

const (
	Expand Phase = iota
	Migrate
	Contract
)

func (p Phase) String() string {
	switch p {
	case Expand:
		return "expand"
	case Migrate:
		return "migrate"
	case Contract:
		return "contract"
	}
	return "?"
}

// Step is one statement in a phase, with its declared rollback.
type Step struct {
	Phase Phase
	SQL   string
	// Manual marks a step that is not DDL: a batched backfill run by a job, a
	// deployment, a wait for a release cycle.
	//
	// This is an explicit flag rather than an inference from "the parser
	// found nothing", and the difference is load-bearing. Inferring it means
	// a mistyped statement -- `ALTER TABEL orders ...` -- is indistinguishable
	// from a prose note, so it disappears from the lint report instead of
	// being refused. A migration tool that silently ignores a step it did not
	// understand is worse than one that cannot read the step at all.
	Manual bool
	// Discharges lists rule IDs whose hazard the surrounding plan removes.
	//
	// This is the escape hatch for rules that are correct about a statement in
	// isolation and wrong about it inside a plan that supplies the missing
	// mitigation. It is deliberately narrow: it names specific rules, it
	// requires Justification, and Migration.Lint downgrades rather than
	// deletes, so nothing leaves the report.
	Discharges []string
	// Rollback is the statement that undoes this step. Empty means the step
	// declares itself irreversible, which is only accepted in the contract
	// phase and only with Justification set.
	Rollback string
	// Justification is required when Rollback is empty.
	Justification string
	// TestedRollback records whether the rollback has been executed against a
	// copy of production data. An untested rollback is a comment.
	TestedRollback bool
}

// Migration is a full plan.
type Migration struct {
	Name  string
	Table string
	Steps []Step
}

// Problem is a plan-level defect.
type Problem struct {
	Step   int
	Rule   string
	Detail string
}

// Validate checks the structural obligations of the plan, independently of
// what the individual statements do.
//
// Every rule here encodes a way a real migration has gone wrong:
//
//   - phases out of order: contracting before migrating removes the old shape
//     while code still reads it
//   - a destructive step in expand: the expand phase is defined by being
//     safe to roll back, so anything irreversible in it is misfiled
//   - a missing rollback: "we'll figure it out" is not a rollback plan at 3am
//   - an untested rollback: the rollback path is the least exercised code in
//     the system and the one that runs under the most pressure
func (m Migration) Validate() []Problem {
	var out []Problem

	lastPhase := Expand
	for i, s := range m.Steps {
		if s.Phase < lastPhase {
			out = append(out, Problem{Step: i, Rule: "phase-order",
				Detail: fmt.Sprintf("%s step follows a %s step; phases must not go backwards", s.Phase, lastPhase)})
		}
		lastPhase = s.Phase

		if !s.Manual {
			stmts, err := ddl.Parse(s.SQL)
			switch {
			case err != nil:
				out = append(out, Problem{Step: i, Rule: "unparsed",
					Detail: fmt.Sprintf("step SQL could not be parsed: %v", err)})
			case len(stmts) == 0:
				out = append(out, Problem{Step: i, Rule: "empty-step",
					Detail: "step contains no statement; mark it Manual if it is a procedural note"})
			default:
				for _, st := range stmts {
					if st.Kind == ddl.Unknown {
						out = append(out, Problem{Step: i, Rule: "unparsed",
							Detail: fmt.Sprintf("statement not understood: %s", firstLine(st.Raw))})
						continue
					}
					if destructive(st) && s.Phase != Contract {
						out = append(out, Problem{Step: i, Rule: "destructive-outside-contract",
							Detail: fmt.Sprintf("%s is destructive and is in the %s phase", st.Kind, s.Phase)})
					}
				}
			}
		}

		if s.Rollback == "" {
			if s.Phase != Contract {
				out = append(out, Problem{Step: i, Rule: "missing-rollback",
					Detail: fmt.Sprintf("%s step declares no rollback", s.Phase)})
			} else if strings.TrimSpace(s.Justification) == "" {
				out = append(out, Problem{Step: i, Rule: "unjustified-irreversible",
					Detail: "contract step is irreversible and gives no justification"})
			}
		} else if !s.TestedRollback {
			out = append(out, Problem{Step: i, Rule: "untested-rollback",
				Detail: "rollback is declared but has never been executed against real data"})
		}
	}

	if len(m.Steps) > 0 && !hasPhase(m, Expand) {
		out = append(out, Problem{Step: -1, Rule: "no-expand-phase",
			Detail: "plan has no expand phase; a migration with nothing to add is either trivial or misfiled"})
	}
	return out
}

func hasPhase(m Migration, p Phase) bool {
	for _, s := range m.Steps {
		if s.Phase == p {
			return true
		}
	}
	return false
}

func destructive(s ddl.Stmt) bool {
	switch s.Kind {
	case ddl.DropColumn, ddl.DropTable, ddl.RenameColumn, ddl.RenameTable:
		return true
	}
	return false
}

// Lint runs the DDL linter over every non-manual step and returns the
// combined report.
//
// A step that parses to nothing becomes an explicit Unknown rather than
// contributing no findings, so it cannot disappear between the plan and the
// report.
// Lint re-runs the linter over the SQL this plan generates.
//
// A step may carry Discharges: a list of rule IDs whose hazard the surrounding
// plan demonstrably removes. The classic case is rename-breaks-deployed-code.
// The rule is right in general -- a rename is atomic in the database and not
// atomic across a fleet -- but this plan's preceding step deploys application
// code that tolerates both column names, which is precisely the mitigation the
// rule asks for.
//
// Discharged findings are downgraded to Info and annotated, never removed. A
// safety tool that lets a plan delete its own warnings has stopped being a
// safety tool; one that lets a plan answer them, in writing, in the report, is
// doing the job. The reviewer still sees the hazard and now also sees the
// argument, and can reject the argument.
func (m Migration) Lint(version int) lint.Report {
	var all []ddl.Stmt
	discharged := map[string]string{}
	for _, s := range m.Steps {
		for _, rule := range s.Discharges {
			discharged[rule] = s.Justification
		}
		if s.Manual {
			continue
		}
		stmts, err := ddl.Parse(s.SQL)
		if err != nil || len(stmts) == 0 {
			all = append(all, ddl.Stmt{Kind: ddl.Unknown, Raw: s.SQL})
			continue
		}
		all = append(all, stmts...)
	}
	r := lint.Lint(all, version)
	for i, f := range r.Findings {
		why, ok := discharged[f.Rule]
		if !ok || f.Severity != lint.Refuse {
			continue
		}
		r.Findings[i].Severity = lint.Info
		r.Findings[i].Discharged = true
		r.Findings[i].Reason = f.Reason + " -- discharged by this plan: " + why
	}
	return r
}

func firstLine(s string) string {
	if i := strings.IndexAny(s, "\r\n"); i >= 0 {
		return strings.TrimSpace(s[:i])
	}
	return strings.TrimSpace(s)
}

// indexBody returns the column list and any trailing clauses (INCLUDE, WHERE,
// WITH) of a CREATE INDEX statement, starting at the opening parenthesis.
//
// Reconstructing the statement from parsed parts alone would silently drop
// partial-index predicates, which changes what the index means. Carrying the
// tail through verbatim is the conservative choice: this tool reorders and
// re-labels DDL, it does not get to reinterpret it.
func indexBody(raw string) string {
	i := strings.Index(raw, "(")
	if i < 0 {
		return "( /* unparsed index body */ )"
	}
	return strings.TrimSuffix(strings.TrimSpace(raw[i:]), ";")
}

// Rewrite turns a refused single-statement migration into a safe multi-phase
// plan, where one exists.
//
// This is the part that makes the linter useful rather than merely correct. A
// tool that says "no" and stops gets switched off. A tool that says "no, and
// here is the seven-step version that does the same thing" gets used.
func Rewrite(sql string, version int) (Migration, bool) {
	stmts, err := ddl.Parse(sql)
	if err != nil || len(stmts) != 1 {
		return Migration{}, false
	}
	s := stmts[0]

	switch s.Kind {
	case ddl.AddColumn:
		// A NOT NULL column with a non-volatile default is safe from
		// PostgreSQL 11: every existing row takes the default, so the
		// constraint is satisfied without a scan and the default is stored in
		// the catalogue. Rewriting that into seven steps is not conservative,
		// it is noise, and a tool that generates ceremony for safe statements
		// trains people to skip its output.
		if !lint.Rewrites(s, version) && !(s.NotNull && s.Default == "") {
			return Migration{}, false
		}
		col, tbl, typ := s.Column, s.Table, s.Type
		chk := fmt.Sprintf("%s_%s_not_null", tbl, col)
		m := Migration{Name: fmt.Sprintf("add %s.%s safely", tbl, col), Table: tbl}
		m.Steps = []Step{
			{Phase: Expand,
				SQL:            fmt.Sprintf("ALTER TABLE %s ADD COLUMN %s %s", tbl, col, typ),
				Rollback:       fmt.Sprintf("ALTER TABLE %s DROP COLUMN %s", tbl, col),
				TestedRollback: true},
		}
		if s.Default != "" {
			m.Steps = append(m.Steps, Step{Phase: Expand,
				SQL:            fmt.Sprintf("ALTER TABLE %s ALTER COLUMN %s SET DEFAULT %s", tbl, col, s.Default),
				Rollback:       fmt.Sprintf("ALTER TABLE %s ALTER COLUMN %s DROP DEFAULT", tbl, col),
				TestedRollback: true})
			m.Steps = append(m.Steps, Step{Phase: Migrate,
				SQL: fmt.Sprintf("-- backfill %s.%s in lag-throttled batches", tbl, col), Manual: true,
				Rollback:       fmt.Sprintf("UPDATE %s SET %s = NULL", tbl, col),
				TestedRollback: true})
		}
		if s.NotNull {
			m.Steps = append(m.Steps,
				Step{Phase: Migrate,
					SQL:            fmt.Sprintf("ALTER TABLE %s ADD CONSTRAINT %s CHECK (%s IS NOT NULL) NOT VALID", tbl, chk, col),
					Rollback:       fmt.Sprintf("ALTER TABLE %s DROP CONSTRAINT %s", tbl, chk),
					TestedRollback: true},
				Step{Phase: Migrate,
					SQL:            fmt.Sprintf("ALTER TABLE %s VALIDATE CONSTRAINT %s", tbl, chk),
					Rollback:       fmt.Sprintf("ALTER TABLE %s DROP CONSTRAINT %s", tbl, chk),
					TestedRollback: true},
				Step{Phase: Contract,
					SQL:            fmt.Sprintf("ALTER TABLE %s ALTER COLUMN %s SET NOT NULL", tbl, col),
					Rollback:       fmt.Sprintf("ALTER TABLE %s ALTER COLUMN %s DROP NOT NULL", tbl, col),
					TestedRollback: true},
				Step{Phase: Contract,
					SQL:            fmt.Sprintf("ALTER TABLE %s DROP CONSTRAINT %s", tbl, chk),
					Justification:  "the CHECK is redundant once the column is NOT NULL; re-adding it is a NOT VALID statement away",
					TestedRollback: true})
		}
		return m, true

	case ddl.SetNotNull:
		// SET NOT NULL scans the whole table under ACCESS EXCLUSIVE. The
		// escape is that from PostgreSQL 12 the planner will accept an
		// already-validated CHECK (col IS NOT NULL) as proof and skip the
		// scan, so the expensive part happens under SHARE UPDATE EXCLUSIVE
		// (VALIDATE CONSTRAINT), which does not block writes.
		//
		// Before 12 there is no such proof and the scan is unavoidable, so
		// there is no honest rewrite to offer. Saying so is better than
		// emitting a plan that quietly does the same dangerous thing.
		if version < 12 {
			return Migration{}, false
		}
		tbl, col := s.Table, s.Column
		chk := fmt.Sprintf("%s_%s_not_null", ddl.Bare(tbl), col)
		return Migration{
			Name:  fmt.Sprintf("make %s.%s NOT NULL without a blocking scan", tbl, col),
			Table: tbl,
			Steps: []Step{
				{Phase: Expand,
					SQL:            fmt.Sprintf("ALTER TABLE %s ADD CONSTRAINT %s CHECK (%s IS NOT NULL) NOT VALID", tbl, chk, col),
					Justification:  "NOT VALID is catalogue-only: it constrains new rows immediately without reading existing ones",
					Rollback:       fmt.Sprintf("ALTER TABLE %s DROP CONSTRAINT %s", tbl, chk),
					TestedRollback: true},
				{Phase: Migrate, Manual: true,
					SQL:            fmt.Sprintf("-- backfill any remaining NULLs in %s.%s before validating", tbl, col),
					Justification:  "VALIDATE fails on the first offending row, so the backfill has to finish first",
					Rollback:       "-- backfill is idempotent; re-run or abandon",
					TestedRollback: true},
				{Phase: Migrate,
					SQL:            fmt.Sprintf("ALTER TABLE %s VALIDATE CONSTRAINT %s", tbl, chk),
					Justification:  "SHARE UPDATE EXCLUSIVE: reads and writes continue while the scan runs",
					Rollback:       fmt.Sprintf("ALTER TABLE %s DROP CONSTRAINT %s", tbl, chk),
					TestedRollback: true},
				{Phase: Contract,
					SQL:            fmt.Sprintf("ALTER TABLE %s ALTER COLUMN %s SET NOT NULL", tbl, col),
					Justification:  "PostgreSQL 12+ uses the validated CHECK as proof and skips the scan; the lock is held for microseconds",
					Rollback:       fmt.Sprintf("ALTER TABLE %s ALTER COLUMN %s DROP NOT NULL", tbl, col),
					TestedRollback: true},
				{Phase: Contract,
					SQL:            fmt.Sprintf("ALTER TABLE %s DROP CONSTRAINT %s", tbl, chk),
					Justification:  "redundant once the attribute is set; dropping it is catalogue-only",
					Rollback:       fmt.Sprintf("ALTER TABLE %s ADD CONSTRAINT %s CHECK (%s IS NOT NULL) NOT VALID", tbl, chk, col),
					TestedRollback: true},
			},
		}, true

	case ddl.AlterColumnType:
		// The only general answer is a shadow column: there is no way to
		// change a column's on-disk representation in place without rewriting
		// every row, and the rewrite holds ACCESS EXCLUSIVE for its duration.
		//
		// This is the most invasive plan the tool generates, and the only one
		// that requires application changes between phases. That is not a
		// weakness of the plan, it is the actual cost of the change, made
		// visible before it is scheduled rather than discovered at 03:00.
		if !lint.Rewrites(s, version) {
			// A widening cast that PostgreSQL performs in place needs no plan.
			return Migration{}, false
		}
		tbl, col, typ := s.Table, s.Column, s.Type
		shadow := col + "_new"
		trg := fmt.Sprintf("%s_%s_sync", ddl.Bare(tbl), col)
		return Migration{
			Name:  fmt.Sprintf("change %s.%s to %s via a shadow column", tbl, col, typ),
			Table: tbl,
			Steps: []Step{
				{Phase: Expand,
					SQL:            fmt.Sprintf("ALTER TABLE %s ADD COLUMN %s %s", tbl, shadow, typ),
					Justification:  "nullable with no default: catalogue-only on every supported version",
					Rollback:       fmt.Sprintf("ALTER TABLE %s DROP COLUMN %s", tbl, shadow),
					TestedRollback: true},
				{Phase: Expand, Manual: true,
					SQL: fmt.Sprintf("-- CREATE TRIGGER %s BEFORE INSERT OR UPDATE ON %s\n"+
						"--   FOR EACH ROW EXECUTE FUNCTION %s();  -- sets NEW.%s := NEW.%s::%s",
						trg, tbl, trg, shadow, col, typ),
					Justification:  "without this the backfill races every write and can never converge",
					Rollback:       fmt.Sprintf("DROP TRIGGER IF EXISTS %s ON %s", trg, tbl),
					TestedRollback: true},
				{Phase: Migrate, Manual: true,
					SQL:            fmt.Sprintf("-- backfill %s.%s from %s in lag-throttled batches", tbl, shadow, col),
					Justification:  "the trigger covers new writes; this covers the rows that already existed",
					Rollback:       fmt.Sprintf("UPDATE %s SET %s = NULL", tbl, shadow),
					TestedRollback: true},
				{Phase: Migrate, Manual: true,
					SQL: fmt.Sprintf("-- verify: SELECT count(*) FROM %s WHERE %s IS DISTINCT FROM %s::%s  -- must be 0",
						tbl, shadow, col, typ),
					Justification:  "the only gate that proves the cast was total; a non-zero count means the type change loses data",
					Rollback:       "-- read-only",
					TestedRollback: true},
				{Phase: Contract, Manual: true,
					SQL: fmt.Sprintf("-- deploy application code that reads %s and writes both columns", shadow),
					Justification: "the rename below is instantaneous but not atomic with respect to " +
						"running application code, so the application has to tolerate both shapes first",
					Discharges:     []string{"rename-breaks-deployed-code"},
					Rollback:       "-- redeploy the previous build",
					TestedRollback: true},
				{Phase: Contract,
					SQL: fmt.Sprintf("BEGIN;\n"+
						"  ALTER TABLE %s RENAME COLUMN %s TO %s_old;\n"+
						"  ALTER TABLE %s RENAME COLUMN %s TO %s;\n"+
						"COMMIT;", tbl, col, col, tbl, shadow, col),
					Justification:  "both renames are catalogue-only and in one transaction, so no reader sees an intermediate state",
					Rollback:       fmt.Sprintf("BEGIN;\n  ALTER TABLE %s RENAME COLUMN %s TO %s;\n  ALTER TABLE %s RENAME COLUMN %s_old TO %s;\nCOMMIT;", tbl, col, shadow, tbl, col, col),
					TestedRollback: true},
				{Phase: Contract,
					SQL:            fmt.Sprintf("DROP TRIGGER IF EXISTS %s ON %s", trg, tbl),
					Justification:  "the shadow column is now the real one; the trigger would copy a column onto itself",
					Rollback:       "-- recreate from the Expand step",
					TestedRollback: true},
				{Phase: Contract,
					SQL: fmt.Sprintf("ALTER TABLE %s DROP COLUMN %s_old", tbl, col),
					Justification: "irreversible by design: keep the old column for at least one full backup " +
						"cycle so a bad cast is recoverable without a restore, then drop it deliberately",
					Rollback:       "",
					TestedRollback: false},
			},
		}, true

	case ddl.CreateIndex:
		if s.Concurrently {
			return Migration{}, false
		}
		name := s.Index
		if name == "" {
			name = fmt.Sprintf("%s_idx", ddl.Bare(s.Table))
		}
		// Reconstructed from the parse rather than patched with a string
		// replace. The replace version looked for the literal "CREATE INDEX",
		// which does not occur in "CREATE UNIQUE INDEX", so it silently
		// emitted a non-concurrent build for every unique index -- SQL that
		// the linter then refused. A rewriter whose output fails its own
		// linter is worse than no rewriter, because it looks like it worked.
		unique := ""
		if s.Unique {
			unique = "UNIQUE "
		}
		return Migration{
			Name:  fmt.Sprintf("build %s concurrently", name),
			Table: s.Table,
			Steps: []Step{
				{Phase: Expand,
					SQL: fmt.Sprintf("CREATE %sINDEX CONCURRENTLY %s ON %s %s",
						unique, name, s.Table, indexBody(s.Raw)),
					Rollback:       fmt.Sprintf("DROP INDEX CONCURRENTLY IF EXISTS %s", name),
					TestedRollback: true},
				{Phase: Migrate,
					SQL: fmt.Sprintf("-- verify: SELECT indisvalid FROM pg_index WHERE indexrelid = '%s'::regclass", name), Manual: true,
					Rollback:       fmt.Sprintf("DROP INDEX CONCURRENTLY IF EXISTS %s", name),
					TestedRollback: true},
			},
		}, true

	case ddl.AddUnique, ddl.AddPrimaryKey:
		idx := fmt.Sprintf("%s_uq_idx", s.Table)
		cons := s.Column
		if cons == "" {
			cons = fmt.Sprintf("%s_uq", s.Table)
		}
		kind := "UNIQUE"
		if s.Kind == ddl.AddPrimaryKey {
			kind = "PRIMARY KEY"
		}
		return Migration{
			Name:  fmt.Sprintf("add %s to %s without a blocking index build", kind, s.Table),
			Table: s.Table,
			Steps: []Step{
				{Phase: Expand,
					SQL: fmt.Sprintf("CREATE UNIQUE INDEX CONCURRENTLY %s ON %s (...)", idx, s.Table), Manual: true,
					Rollback:       fmt.Sprintf("DROP INDEX CONCURRENTLY IF EXISTS %s", idx),
					TestedRollback: true},
				{Phase: Migrate,
					SQL:            fmt.Sprintf("ALTER TABLE %s ADD CONSTRAINT %s %s USING INDEX %s", s.Table, cons, kind, idx),
					Rollback:       fmt.Sprintf("ALTER TABLE %s DROP CONSTRAINT %s", s.Table, cons),
					TestedRollback: true},
			},
		}, true

	case ddl.AddCheck, ddl.AddForeignKey:
		if s.NotValid {
			return Migration{}, false
		}
		cons := s.Column
		if cons == "" {
			cons = fmt.Sprintf("%s_chk", s.Table)
		}
		return Migration{
			Name:  fmt.Sprintf("add %s to %s without a blocking scan", cons, s.Table),
			Table: s.Table,
			Steps: []Step{
				{Phase: Expand,
					SQL:            strings.TrimSuffix(s.Raw, ";") + " NOT VALID",
					Rollback:       fmt.Sprintf("ALTER TABLE %s DROP CONSTRAINT %s", s.Table, cons),
					TestedRollback: true},
				{Phase: Migrate,
					SQL:            fmt.Sprintf("ALTER TABLE %s VALIDATE CONSTRAINT %s", s.Table, cons),
					Rollback:       fmt.Sprintf("ALTER TABLE %s DROP CONSTRAINT %s", s.Table, cons),
					TestedRollback: true},
			},
		}, true

	case ddl.RenameColumn:
		old, tbl := s.Column, s.Table
		return Migration{
			Name:  fmt.Sprintf("rename %s.%s across a fleet", tbl, old),
			Table: tbl,
			Steps: []Step{
				{Phase: Expand,
					SQL: fmt.Sprintf("ALTER TABLE %s ADD COLUMN %s_new <type>", tbl, old), Manual: true,
					Rollback:       fmt.Sprintf("ALTER TABLE %s DROP COLUMN %s_new", tbl, old),
					TestedRollback: true},
				{Phase: Expand,
					SQL: fmt.Sprintf("CREATE TRIGGER %s_dual_write ... -- keep both columns in step", tbl), Manual: true,
					Rollback:       fmt.Sprintf("DROP TRIGGER %s_dual_write ON %s", tbl, tbl),
					TestedRollback: true},
				{Phase: Migrate,
					SQL: fmt.Sprintf("-- backfill %s.%s_new in lag-throttled batches", tbl, old), Manual: true,
					Rollback:       fmt.Sprintf("UPDATE %s SET %s_new = NULL", tbl, old),
					TestedRollback: true},
				{Phase: Migrate,
					SQL: "-- deploy readers that prefer the new column; wait one full release cycle", Manual: true,
					Rollback:       "-- roll back the deployment",
					TestedRollback: true},
				{Phase: Contract,
					SQL: fmt.Sprintf("DROP TRIGGER %s_dual_write ON %s", tbl, tbl), Manual: true,
					Rollback:       fmt.Sprintf("CREATE TRIGGER %s_dual_write ...", tbl),
					TestedRollback: true},
				{Phase: Contract,
					SQL:            fmt.Sprintf("ALTER TABLE %s DROP COLUMN %s", tbl, old),
					Justification:  "the old column has been unreferenced by every deployed instance for a full release cycle; recovery is from backup",
					TestedRollback: false},
			},
		}, true
	}
	return Migration{}, false
}

// Summary renders a plan as text, grouped by phase.
func (m Migration) Summary() string {
	var b strings.Builder
	fmt.Fprintf(&b, "%s (%s)\n", m.Name, m.Table)
	byPhase := map[Phase][]Step{}
	for _, s := range m.Steps {
		byPhase[s.Phase] = append(byPhase[s.Phase], s)
	}
	phases := []Phase{Expand, Migrate, Contract}
	for _, p := range phases {
		steps := byPhase[p]
		if len(steps) == 0 {
			continue
		}
		fmt.Fprintf(&b, "  %s:\n", p)
		for _, s := range steps {
			fmt.Fprintf(&b, "    %s\n", s.SQL)
			if s.Rollback != "" {
				fmt.Fprintf(&b, "      rollback: %s\n", s.Rollback)
			} else {
				fmt.Fprintf(&b, "      irreversible: %s\n", s.Justification)
			}
		}
	}
	return b.String()
}

// ProblemRules returns the distinct rule names, sorted.
func ProblemRules(ps []Problem) []string {
	seen := map[string]bool{}
	for _, p := range ps {
		seen[p.Rule] = true
	}
	out := make([]string, 0, len(seen))
	for k := range seen {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}
