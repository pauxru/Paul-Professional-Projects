// Package ddl parses the subset of PostgreSQL DDL that a migration tool needs
// to reason about.
//
// It is a real tokeniser and recursive-descent parser rather than a set of
// regular expressions. That choice is the whole reason the linter downstream
// can be trusted: a regex that matches `ALTER TABLE ... ADD COLUMN ... NOT
// NULL` will also match it inside a string literal, inside a comment, or in a
// statement that also does four other things, and a safety tool that can be
// fooled by a comment is not a safety tool.
package ddl

import (
	"fmt"
	"strings"
)

// Kind classifies a parsed statement.
type Kind int

const (
	Unknown Kind = iota
	AddColumn
	DropColumn
	AlterColumnType
	SetNotNull
	DropNotNull
	SetDefault
	DropDefault
	AddCheck
	AddForeignKey
	AddUnique
	AddPrimaryKey
	DropConstraint
	ValidateConstraint
	CreateIndex
	DropIndex
	RenameColumn
	RenameTable
	CreateTable
	DropTable
	SetStatistics
	Cluster
	Vacuum
	Analyze
	CreateTrigger
	DropTrigger
	Begin
	Commit
	Rollback
	SetParameter
)

var kindNames = map[Kind]string{
	Unknown: "UNKNOWN", AddColumn: "ADD COLUMN", DropColumn: "DROP COLUMN",
	AlterColumnType: "ALTER COLUMN TYPE", SetNotNull: "SET NOT NULL",
	DropNotNull: "DROP NOT NULL", SetDefault: "SET DEFAULT",
	DropDefault: "DROP DEFAULT", AddCheck: "ADD CHECK",
	AddForeignKey: "ADD FOREIGN KEY", AddUnique: "ADD UNIQUE",
	AddPrimaryKey: "ADD PRIMARY KEY", DropConstraint: "DROP CONSTRAINT",
	ValidateConstraint: "VALIDATE CONSTRAINT", CreateIndex: "CREATE INDEX",
	DropIndex: "DROP INDEX", RenameColumn: "RENAME COLUMN",
	RenameTable: "RENAME TABLE", CreateTable: "CREATE TABLE",
	DropTable: "DROP TABLE", SetStatistics: "SET STATISTICS",
	Cluster: "CLUSTER", Vacuum: "VACUUM", Analyze: "ANALYZE",
	CreateTrigger: "CREATE TRIGGER", DropTrigger: "DROP TRIGGER",
	Begin: "BEGIN", Commit: "COMMIT", Rollback: "ROLLBACK",
	SetParameter: "SET",
}

func (k Kind) String() string {
	if s, ok := kindNames[k]; ok {
		return s
	}
	return fmt.Sprintf("Kind(%d)", int(k))
}

// Stmt is one parsed DDL statement.
type Stmt struct {
	Kind    Kind
	Raw     string
	Table   string
	Column  string
	Index   string
	Type    string
	Default string
	// NotNull is set on ADD COLUMN when the new column is declared NOT NULL.
	NotNull bool
	// Concurrently is set for CREATE/DROP INDEX CONCURRENTLY.
	Concurrently bool
	// NotValid is set for ADD CONSTRAINT ... NOT VALID.
	NotValid bool
	// IfExists / IfNotExists guard clauses.
	IfExists    bool
	IfNotExists bool
	// Unique marks CREATE UNIQUE INDEX.
	Unique bool
	// UsingIndex holds the index named in ADD CONSTRAINT ... USING INDEX,
	// which is what makes adding a UNIQUE or PRIMARY KEY constraint a
	// catalogue-only operation instead of a blocking index build.
	UsingIndex string
	// UsingExpr holds the USING clause of an ALTER COLUMN TYPE.
	UsingExpr string
	// Line is the 1-based line the statement started on, for diagnostics.
	Line int
}

// Parse splits a script into statements and parses each one.
//
// Unparseable statements are returned with Kind == Unknown rather than
// dropped, so the linter can refuse them explicitly. A migration tool that
// silently ignores a statement it did not understand is worse than one that
// cannot parse it at all.
func Parse(script string) ([]Stmt, error) {
	raws, err := split(script)
	if err != nil {
		return nil, err
	}
	out := make([]Stmt, 0, len(raws))
	for _, r := range raws {
		s := parseOne(r.text)
		s.Raw = r.text
		s.Line = r.line
		out = append(out, s)
	}
	return out, nil
}

type rawStmt struct {
	text string
	line int
}

// split breaks a script on semicolons that are not inside a string literal, a
// quoted identifier, a dollar-quoted block or a comment.
//
// This is the part a regex cannot do. Dollar quoting in particular is
// context-sensitive: the closing tag must match the opening one, so `$$`
// inside a `$fn$ ... $fn$` block is data, not a delimiter.
func split(script string) ([]rawStmt, error) {
	var out []rawStmt
	var cur strings.Builder
	line, startLine := 1, 0
	runes := []rune(script)

	// mark records the line of the first non-blank character of the current
	// statement. Recording it at flush time instead is off by however many
	// blank lines separate the statements, because those newlines are
	// accumulated into the *previous* buffer before it is trimmed.
	mark := func() {
		if startLine == 0 {
			startLine = line
		}
	}

	flush := func() {
		t := strings.TrimSpace(cur.String())
		if t != "" {
			l := startLine
			if l == 0 {
				l = line
			}
			out = append(out, rawStmt{text: t, line: l})
		}
		cur.Reset()
		startLine = 0
	}

	for i := 0; i < len(runes); i++ {
		c := runes[i]
		switch {
		case c == '\n':
			line++
			cur.WriteRune(c)

		case c == '-' && i+1 < len(runes) && runes[i+1] == '-':
			for i < len(runes) && runes[i] != '\n' {
				i++
			}
			i--

		case c == '/' && i+1 < len(runes) && runes[i+1] == '*':
			depth, j := 1, i+2
			for j < len(runes) && depth > 0 {
				if runes[j] == '\n' {
					line++
				}
				if runes[j] == '/' && j+1 < len(runes) && runes[j+1] == '*' {
					depth++
					j += 2
					continue
				}
				if runes[j] == '*' && j+1 < len(runes) && runes[j+1] == '/' {
					depth--
					j += 2
					continue
				}
				j++
			}
			if depth != 0 {
				return nil, fmt.Errorf("ddl: unterminated block comment starting at line %d", line)
			}
			i = j - 1

		case c == '\'' || c == '"':
			q := c
			mark()
			cur.WriteRune(c)
			i++
			for i < len(runes) {
				if runes[i] == '\n' {
					line++
				}
				cur.WriteRune(runes[i])
				if runes[i] == q {
					// Doubled quote is an escaped quote, not a terminator.
					if i+1 < len(runes) && runes[i+1] == q {
						i += 2
						cur.WriteRune(q)
						continue
					}
					break
				}
				i++
			}
			if i >= len(runes) {
				return nil, fmt.Errorf("ddl: unterminated quoted string at line %d", startLine)
			}

		case c == '$':
			tag, ok := dollarTag(runes, i)
			if !ok {
				mark()
				cur.WriteRune(c)
				continue
			}
			mark()
			end := indexFrom(runes, i+len(tag), tag)
			if end < 0 {
				return nil, fmt.Errorf("ddl: unterminated dollar-quoted block %s at line %d", tag, line)
			}
			for _, r := range runes[i : end+len(tag)] {
				if r == '\n' {
					line++
				}
				cur.WriteRune(r)
			}
			i = end + len(tag) - 1

		case c == ';':
			flush()

		default:
			if c != ' ' && c != '\t' && c != '\r' {
				mark()
			}
			cur.WriteRune(c)
		}
	}
	flush()
	return out, nil
}

// dollarTag returns the dollar-quote tag starting at i, e.g. "$$" or "$fn$".
func dollarTag(runes []rune, i int) (string, bool) {
	if runes[i] != '$' {
		return "", false
	}
	j := i + 1
	for j < len(runes) && (isIdentRune(runes[j]) && runes[j] != '$') {
		j++
	}
	if j < len(runes) && runes[j] == '$' {
		return string(runes[i : j+1]), true
	}
	return "", false
}

func indexFrom(runes []rune, from int, tag string) int {
	t := []rune(tag)
	for i := from; i+len(t) <= len(runes); i++ {
		match := true
		for k := range t {
			if runes[i+k] != t[k] {
				match = false
				break
			}
		}
		if match {
			return i
		}
	}
	return -1
}

func isIdentRune(r rune) bool {
	return r == '_' || r == '$' ||
		(r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9')
}
