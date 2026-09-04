// Package lint classifies DDL by the lock it takes and refuses what is unsafe.
//
// The classification is version-dependent, and that is not a detail. Three
// separate PostgreSQL releases changed whether a common migration rewrites
// the table:
//
//   - 11 made ADD COLUMN ... DEFAULT <non-volatile> a catalogue-only change
//   - 12 made ALTER COLUMN TYPE for some widening conversions non-rewriting
//   - 12 added the ability to use an existing index to satisfy SET NOT NULL
//
// A linter that hardcodes one version's behaviour gives the wrong answer on
// the others, and the direction of the error is the dangerous one: rules
// written against a modern PostgreSQL will pass a migration that rewrites a
// 900-million-row table on an older one.
package lint

import (
	"fmt"
	"sort"
	"strings"

	"evolve/ddl"
	"evolve/locks"
)

// Severity of a finding.
type Severity int

const (
	Info Severity = iota
	Warn
	Refuse
)

func (s Severity) String() string {
	switch s {
	case Info:
		return "INFO"
	case Warn:
		return "WARN"
	case Refuse:
		return "REFUSE"
	}
	return "?"
}

// Finding is one linter result.
type Finding struct {
	Rule     string
	Severity Severity
	Stmt     ddl.Stmt
	Lock     locks.Mode
	// Rewrite reports whether the statement rewrites the whole table, which
	// is what turns a short lock into a long one.
	Rewrite bool
	// Scan reports whether the statement takes a full table scan while
	// holding its lock, without rewriting.
	Scan   bool
	Reason string
	Fix    string
	// Discharged marks a finding whose hazard the surrounding plan answers.
	// It stays in the report at Info with the argument appended, so a reviewer
	// sees both the hazard and the claim that it has been handled.
	Discharged bool
}

// Report is the result of linting a script.
type Report struct {
	Findings []Finding
	Version  int
}

// Refused reports whether any finding blocks the migration.
func (r Report) Refused() bool {
	for _, f := range r.Findings {
		if f.Severity == Refuse {
			return true
		}
	}
	return false
}

// Count returns the number of findings at a severity.
func (r Report) Count(s Severity) int {
	n := 0
	for _, f := range r.Findings {
		if f.Severity == s {
			n++
		}
	}
	return n
}

// Flagged reports whether the linter considers the migration risky enough to
// deserve a safer alternative, which is a wider question than Refused.
//
// SET NOT NULL is the case that forced the distinction: it is a warning rather
// than a refusal, because on a small table the scan is over before anyone
// notices, but it still has a strictly better multi-phase form. Tying the
// rewriter to Refused would mean the tool declined to show that form to the
// one person who asked.
func (r Report) Flagged() bool {
	for _, f := range r.Findings {
		if f.Severity == Refuse || f.Severity == Warn {
			return true
		}
	}
	return false
}

// Rules returns the distinct rule names fired, sorted.
func (r Report) Rules() []string {
	seen := map[string]bool{}
	for _, f := range r.Findings {
		seen[f.Rule] = true
	}
	out := make([]string, 0, len(seen))
	for k := range seen {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

// volatileFuncs are functions whose value differs per row, so a DEFAULT that
// calls one forces a rewrite even on PostgreSQL 11 and later.
var volatileFuncs = map[string]bool{
	"random": true, "now": true, "clock_timestamp": true, "timeofday": true,
	"nextval": true, "gen_random_uuid": true, "uuid_generate_v4": true,
	"statement_timestamp": true, "transaction_timestamp": true,
}

// volatileKeywords are the SQL-standard spellings that take no parentheses.
var volatileKeywords = map[string]bool{
	"current_timestamp": true, "localtimestamp": true, "current_time": true,
	"localtime": true, "current_date": true,
}

// IsVolatileDefault reports whether a DEFAULT expression is volatile.
//
// The first implementation was a substring scan over the function names, with
// a comment claiming it was "deliberately a prefix match". It was neither: it
// matched `now` inside the string literal 'nowhere' and `random` inside
// 'random thoughts', so a linter built on it refused safe migrations for
// reasons its own error message could not explain. A tool that cries wolf on
// a constant default is a tool that gets a blanket exemption in CI.
//
// The rule now is lexical: a function name only counts when it is a complete
// identifier immediately followed by an open parenthesis, a bare keyword only
// when it is a complete identifier, and nothing inside a single-quoted string
// counts at all.
//
// `current_timestamp` is included even though PostgreSQL classifies it as
// STABLE rather than VOLATILE. The distinction matters for query planning; it
// does not matter here, because the question a reviewer needs forced in front
// of them is "does every existing row get the same value", and for a
// timestamp default that deserves a deliberate answer.
func IsVolatileDefault(expr string) bool {
	r := []rune(expr)
	for i := 0; i < len(r); i++ {
		c := r[i]

		if c == '\'' {
			i++
			for i < len(r) {
				if r[i] == '\'' {
					if i+1 < len(r) && r[i+1] == '\'' {
						i += 2
						continue
					}
					break
				}
				i++
			}
			continue
		}
		if c == '"' {
			i++
			for i < len(r) && r[i] != '"' {
				i++
			}
			continue
		}
		if !isIdentStart(c) {
			continue
		}

		j := i
		for j < len(r) && isIdentRune(r[j]) {
			j++
		}
		word := strings.ToLower(string(r[i:j]))
		i = j - 1

		if volatileKeywords[word] {
			return true
		}
		k := j
		for k < len(r) && (r[k] == ' ' || r[k] == '\t' || r[k] == '\n' || r[k] == '\r') {
			k++
		}
		if k < len(r) && r[k] == '(' && volatileFuncs[word] {
			return true
		}
	}
	return false
}

func isIdentStart(r rune) bool {
	return r == '_' || (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z')
}

func isIdentRune(r rune) bool {
	return isIdentStart(r) || (r >= '0' && r <= '9') || r == '$'
}

// LockOf returns the lock mode a statement takes on its table.
func LockOf(s ddl.Stmt) locks.Mode {
	switch s.Kind {
	case ddl.CreateIndex:
		if s.Concurrently {
			return locks.ShareUpdateExclusive
		}
		return locks.Share
	case ddl.DropIndex:
		if s.Concurrently {
			return locks.ShareUpdateExclusive
		}
		return locks.AccessExclusive
	case ddl.ValidateConstraint:
		return locks.ShareUpdateExclusive
	case ddl.SetStatistics:
		return locks.ShareUpdateExclusive
	case ddl.Analyze:
		return locks.ShareUpdateExclusive
	case ddl.Vacuum:
		return locks.ShareUpdateExclusive
	case ddl.Cluster:
		return locks.AccessExclusive
	case ddl.CreateTrigger, ddl.DropTrigger:
		// Both take ACCESS EXCLUSIVE on the table, but hold it only long
		// enough to update the catalogue. The risk is entirely the queue they
		// join, not the work they do.
		return locks.AccessExclusive
	case ddl.Begin, ddl.Commit, ddl.Rollback, ddl.SetParameter:
		// Transaction control and session GUCs take no table lock at all.
		// Treating them as ACCESS EXCLUSIVE by default made every transactional
		// plan look catastrophic and buried the one statement inside it that
		// actually was.
		return locks.None
	case ddl.Unknown:
		// The safe assumption for something we could not parse is the
		// strongest lock, so an unparsed statement is never quietly treated
		// as harmless.
		return locks.AccessExclusive
	}
	// Every remaining ALTER TABLE subform takes ACCESS EXCLUSIVE. That is the
	// point people miss: SET DEFAULT, DROP COLUMN and ADD COLUMN all take the
	// same lock as a rewrite. The difference between safe and unsafe is not
	// the lock mode, it is how long the lock is held.
	return locks.AccessExclusive
}

// Rewrites reports whether a statement rewrites the entire table on the given
// PostgreSQL major version.
func Rewrites(s ddl.Stmt, version int) bool {
	switch s.Kind {
	case ddl.AddColumn:
		if s.Default == "" {
			return false
		}
		if IsVolatileDefault(s.Default) {
			return true
		}
		// The PostgreSQL 11 change: a non-volatile default is stored in the
		// catalogue as the "missing value" for pre-existing rows.
		return version < 11
	case ddl.AlterColumnType:
		// A USING clause always rewrites; without one, some widening
		// conversions are catalogue-only from 12 onwards.
		if s.UsingExpr != "" {
			return true
		}
		if version >= 12 && wideningOnly(s.Type) {
			return false
		}
		return true
	case ddl.AddPrimaryKey:
		return false
	case ddl.Cluster:
		return true
	}
	return false
}

// wideningOnly is a deliberately conservative list of target types that
// PostgreSQL 12+ can adopt without rewriting, given a compatible source type.
//
// Conservative because the real rule depends on the *pair* of types and this
// linter only sees the target. Being wrong in the direction of "assume it
// rewrites" costs a warning; being wrong the other way costs an outage.
func wideningOnly(t string) bool {
	switch strings.ToLower(strings.TrimSpace(t)) {
	case "text", "varchar", "character varying", "bigint", "int8", "numeric":
		return true
	}
	return false
}

// Scans reports whether a statement takes a full table scan while holding its
// lock, without rewriting the table.
//
// This is the category people forget. A validating ADD CHECK does not rewrite
// anything, so it does not show up in "rewrite" checks, and it still holds
// ACCESS EXCLUSIVE for the length of a sequential scan of the whole table.
func Scans(s ddl.Stmt, version int) bool {
	switch s.Kind {
	case ddl.AddCheck, ddl.AddForeignKey:
		return !s.NotValid
	case ddl.SetNotNull:
		// PostgreSQL 12 can prove the constraint from an existing validated
		// CHECK, but the linter cannot see the catalogue, so it reports the
		// scan and the fix explains the escape.
		return true
	case ddl.AddUnique, ddl.AddPrimaryKey:
		// Builds an index under ACCESS EXCLUSIVE -- unless an index built
		// earlier with CREATE INDEX CONCURRENTLY is adopted with USING INDEX,
		// which is a catalogue-only change.
		return s.UsingIndex == ""
	}
	return false
}

// Lint classifies every statement in a script against a PostgreSQL version.
func Lint(stmts []ddl.Stmt, version int) Report {
	r := Report{Version: version}
	for _, s := range stmts {
		r.Findings = append(r.Findings, check(s, version)...)
	}
	return r
}

func check(s ddl.Stmt, version int) []Finding {
	lock := LockOf(s)
	rw := Rewrites(s, version)
	sc := Scans(s, version)
	base := Finding{Stmt: s, Lock: lock, Rewrite: rw, Scan: sc}

	mk := func(rule string, sev Severity, reason, fix string) Finding {
		f := base
		f.Rule, f.Severity, f.Reason, f.Fix = rule, sev, reason, fix
		return f
	}

	var out []Finding

	switch s.Kind {
	case ddl.Unknown:
		out = append(out, mk("unparsed", Refuse,
			"the statement could not be parsed, so no lock analysis is possible",
			"rewrite the statement in a supported form, or apply it manually with a documented lock plan"))
		return out

	case ddl.AddColumn:
		if rw {
			why := "a volatile DEFAULT is evaluated per row"
			if !IsVolatileDefault(s.Default) {
				why = fmt.Sprintf("PostgreSQL %d evaluates any DEFAULT per row on ADD COLUMN", version)
			}
			out = append(out, mk("add-column-rewrite", Refuse,
				fmt.Sprintf("ADD COLUMN with DEFAULT %s rewrites every row under ACCESS EXCLUSIVE: %s", s.Default, why),
				"ADD COLUMN with no default, backfill in batches, then SET DEFAULT for new rows"))
		}
		if s.NotNull && s.Default == "" {
			out = append(out, mk("add-column-not-null-no-default", Refuse,
				"ADD COLUMN ... NOT NULL without a DEFAULT fails outright on a non-empty table",
				"add the column nullable, backfill, add a NOT VALID CHECK, VALIDATE it, then SET NOT NULL"))
		}

	case ddl.SetNotNull:
		out = append(out, mk("set-not-null-scan", Warn,
			"SET NOT NULL scans the whole table under ACCESS EXCLUSIVE to prove no NULLs remain",
			"add a NOT VALID CHECK (col IS NOT NULL), VALIDATE CONSTRAINT under SHARE UPDATE EXCLUSIVE, then SET NOT NULL -- PostgreSQL 12+ proves it from the constraint without a scan"))

	case ddl.AddCheck, ddl.AddForeignKey:
		if !s.NotValid {
			out = append(out, mk("constraint-validates-inline", Refuse,
				"adding a validating constraint scans the whole table under ACCESS EXCLUSIVE",
				"add it NOT VALID, then VALIDATE CONSTRAINT in a separate statement -- validation takes only SHARE UPDATE EXCLUSIVE"))
		}

	case ddl.CreateIndex:
		if !s.Concurrently {
			out = append(out, mk("index-not-concurrent", Refuse,
				"CREATE INDEX takes SHARE for the whole build, blocking every write to the table",
				"use CREATE INDEX CONCURRENTLY, and check indisvalid afterwards"))
		} else {
			out = append(out, mk("index-concurrent-can-fail", Warn,
				"CREATE INDEX CONCURRENTLY can fail and leave an INVALID index behind, which is not used by queries and not rebuilt automatically",
				"follow with a check on pg_index.indisvalid and DROP INDEX CONCURRENTLY on failure; note it cannot run inside a transaction block"))
		}

	case ddl.AddUnique, ddl.AddPrimaryKey:
		if s.UsingIndex != "" {
			// The recommended form. Refusing this would have made the linter
			// reject its own advice, which is how a safety tool loses the
			// argument with the team it is meant to protect.
			out = append(out, mk("constraint-adopts-index", Info,
				fmt.Sprintf("ADD CONSTRAINT ... USING INDEX %s is catalogue-only: the index already exists", s.UsingIndex),
				"still takes a brief ACCESS EXCLUSIVE lock; use a lock timeout and confirm the index is valid first"))
		} else {
			out = append(out, mk("constraint-builds-index-inline", Refuse,
				"adding a UNIQUE or PRIMARY KEY constraint builds its index under ACCESS EXCLUSIVE",
				"CREATE UNIQUE INDEX CONCURRENTLY first, then ADD CONSTRAINT ... USING INDEX, which is catalogue-only"))
		}

	case ddl.AlterColumnType:
		if rw {
			out = append(out, mk("type-change-rewrite", Refuse,
				"ALTER COLUMN TYPE rewrites the table and every index on it under ACCESS EXCLUSIVE",
				"add a new column, dual-write, backfill, swap -- the expand/contract pattern"))
		} else {
			out = append(out, mk("type-change-catalogue-only", Info,
				fmt.Sprintf("PostgreSQL %d can widen to %s without a rewrite", version, s.Type),
				"still takes ACCESS EXCLUSIVE briefly; use a lock timeout"))
		}

	case ddl.DropColumn:
		out = append(out, mk("drop-column-irreversible", Warn,
			"DROP COLUMN is fast but irreversible: the data is gone and no rollback exists",
			"only drop in the contract phase, after the column has been unreferenced by every deployed version for a full release cycle"))

	case ddl.DropIndex:
		if !s.Concurrently {
			out = append(out, mk("drop-index-not-concurrent", Warn,
				"DROP INDEX takes ACCESS EXCLUSIVE",
				"use DROP INDEX CONCURRENTLY"))
		}

	case ddl.RenameColumn, ddl.RenameTable:
		out = append(out, mk("rename-breaks-deployed-code", Refuse,
			"a rename is atomic in the database and not atomic across a fleet: every currently deployed instance referring to the old name breaks the instant it commits",
			"expand/contract -- add the new name, dual-write, migrate readers, drop the old one in a later release"))

	case ddl.Cluster:
		out = append(out, mk("cluster-rewrites", Refuse,
			"CLUSTER rewrites the entire table under ACCESS EXCLUSIVE and holds it for the duration",
			"use pg_repack, or accept a maintenance window"))
	}

	if len(out) == 0 {
		out = append(out, mk("ok", Info,
			fmt.Sprintf("takes %s, no rewrite, no scan", lock),
			"still needs a lock timeout and a retry loop"))
	}
	return out
}
