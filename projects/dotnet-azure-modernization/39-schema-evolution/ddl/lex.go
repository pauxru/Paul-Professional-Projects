package ddl

import "strings"

// token is a lexical token with its original text preserved.
type token struct {
	text  string // original, including quotes
	upper string // upper-cased, unquoted -- what keyword matching uses
	// quoted marks a "double quoted identifier", which is never a keyword
	// however it is spelled. `ALTER TABLE "add" ...` names a table called
	// add; matching it as a keyword would be a parse error at best.
	quoted bool
	str    bool // single-quoted string literal
}

func lex(s string) []token {
	var out []token
	runes := []rune(s)
	for i := 0; i < len(runes); i++ {
		c := runes[i]
		switch {
		case c == ' ' || c == '\t' || c == '\n' || c == '\r':
			continue

		case c == '"':
			j := i + 1
			var b strings.Builder
			for j < len(runes) {
				if runes[j] == '"' {
					if j+1 < len(runes) && runes[j+1] == '"' {
						b.WriteRune('"')
						j += 2
						continue
					}
					break
				}
				b.WriteRune(runes[j])
				j++
			}
			out = append(out, token{text: b.String(), upper: b.String(), quoted: true})
			i = j

		case c == '\'':
			j := i + 1
			var b strings.Builder
			for j < len(runes) {
				if runes[j] == '\'' {
					if j+1 < len(runes) && runes[j+1] == '\'' {
						b.WriteRune('\'')
						j += 2
						continue
					}
					break
				}
				b.WriteRune(runes[j])
				j++
			}
			out = append(out, token{text: b.String(), upper: strings.ToUpper(b.String()), str: true})
			i = j

		case c == '(' || c == ')' || c == ',':
			out = append(out, token{text: string(c), upper: string(c)})

		default:
			j := i
			for j < len(runes) && !isBreak(runes[j]) {
				j++
			}
			if j == i {
				j++
			}
			t := string(runes[i:j])
			out = append(out, token{text: t, upper: strings.ToUpper(t)})
			i = j - 1
		}
	}
	return out
}

func isBreak(r rune) bool {
	switch r {
	case ' ', '\t', '\n', '\r', '(', ')', ',', '"', '\'':
		return true
	}
	return false
}

type parser struct {
	t []token
	i int
}

func (p *parser) eof() bool { return p.i >= len(p.t) }

func (p *parser) peek() token {
	if p.eof() {
		return token{}
	}
	return p.t[p.i]
}

// kw matches an unquoted keyword and consumes it.
func (p *parser) kw(words ...string) bool {
	save := p.i
	for _, w := range words {
		if p.eof() || p.t[p.i].quoted || p.t[p.i].str || p.t[p.i].upper != w {
			p.i = save
			return false
		}
		p.i++
	}
	return true
}

// ident consumes and returns the next token as an identifier, preserving any
// schema qualification.
//
// An earlier version stripped the schema on the grounds that "for lock
// reasoning the table is the table". That was wrong twice over. `public.orders`
// and `archive.orders` are different relations that take independent locks, so
// stripping made them collide in any per-table analysis. Worse, the plan
// package echoes Stmt.Table back into generated SQL: a rewrite of
// `public.orders` emitted `ALTER TABLE orders`, which resolves through
// search_path and can land on a completely different table. A migration tool
// that silently retargets DDL is worse than no migration tool.
func (p *parser) ident() string {
	if p.eof() {
		return ""
	}
	t := p.t[p.i]
	p.i++
	return t.text
}

// Bare returns the unqualified part of a possibly schema-qualified name.
func Bare(name string) string {
	if k := strings.LastIndex(name, "."); k >= 0 && k+1 < len(name) {
		return name[k+1:]
	}
	return name
}

// Schema returns the qualifier of a name, or "" when it is unqualified.
func Schema(name string) string {
	if k := strings.LastIndex(name, "."); k > 0 {
		return name[:k]
	}
	return ""
}

// rest returns the remaining tokens joined, for capturing expressions.
func (p *parser) rest() string {
	var parts []string
	for !p.eof() {
		parts = append(parts, p.t[p.i].text)
		p.i++
	}
	return strings.Join(parts, " ")
}

// scanTo advances to just past the next occurrence of an unquoted keyword,
// reporting whether it was found. Used to skip over clauses whose contents do
// not change the lock analysis.
func (p *parser) scanTo(words ...string) bool {
	for p.i < len(p.t) {
		if p.kw(words...) {
			return true
		}
		p.i++
	}
	return false
}

func parseOne(raw string) Stmt {
	p := &parser{t: lex(raw)}
	switch {
	case p.kw("ALTER", "TABLE"):
		return parseAlterTable(p)
	case p.kw("CREATE", "UNIQUE", "INDEX"):
		s := parseCreateIndex(p)
		s.Unique = true
		return s
	case p.kw("CREATE", "INDEX"):
		return parseCreateIndex(p)
	case p.kw("DROP", "INDEX"):
		return parseDropIndex(p)
	case p.kw("CREATE", "TABLE"):
		s := Stmt{Kind: CreateTable}
		s.IfNotExists = p.kw("IF", "NOT", "EXISTS")
		s.Table = p.ident()
		return s
	case p.kw("DROP", "TABLE"):
		s := Stmt{Kind: DropTable}
		s.IfExists = p.kw("IF", "EXISTS")
		s.Table = p.ident()
		return s
	case p.kw("CREATE", "TRIGGER"):
		s := Stmt{Kind: CreateTrigger, Index: p.ident()}
		if p.scanTo("ON") {
			s.Table = p.ident()
		}
		return s
	case p.kw("DROP", "TRIGGER"):
		s := Stmt{Kind: DropTrigger}
		s.IfExists = p.kw("IF", "EXISTS")
		s.Index = p.ident()
		if p.scanTo("ON") {
			s.Table = p.ident()
		}
		return s
	case p.kw("BEGIN"), p.kw("START", "TRANSACTION"):
		return Stmt{Kind: Begin}
	case p.kw("COMMIT"), p.kw("END"):
		return Stmt{Kind: Commit}
	case p.kw("ROLLBACK"):
		return Stmt{Kind: Rollback}
	case p.kw("SET"):
		// SET lock_timeout / statement_timeout and friends. Session state,
		// not DDL, and specifically the thing this tool spends a whole
		// section telling people to do -- so it must not be reported as an
		// unparseable statement in the plans that do it.
		return Stmt{Kind: SetParameter, Column: p.ident()}
	case p.kw("CLUSTER"):
		return Stmt{Kind: Cluster, Table: p.ident()}
	case p.kw("VACUUM"):
		s := Stmt{Kind: Vacuum}
		p.kw("FULL")
		s.Table = p.ident()
		return s
	case p.kw("ANALYZE"):
		return Stmt{Kind: Analyze, Table: p.ident()}
	}
	return Stmt{Kind: Unknown}
}

func parseAlterTable(p *parser) Stmt {
	s := Stmt{}
	p.kw("IF", "EXISTS")
	p.kw("ONLY")
	s.Table = p.ident()

	switch {
	case p.kw("ADD", "COLUMN"), p.kw("ADD"):
		// ADD may introduce a column or a constraint; disambiguate on the
		// next keyword before consuming an identifier.
		if c, ok := parseAddConstraint(p, &s); ok {
			return c
		}
		s.IfNotExists = p.kw("IF", "NOT", "EXISTS")
		s.Kind = AddColumn
		s.Column = p.ident()
		s.Type = p.ident()
		// Scan the remaining column constraints. Order is not fixed and both
		// DEFAULT and NOT NULL may appear.
		for !p.eof() {
			if p.kw("NOT", "NULL") {
				s.NotNull = true
				continue
			}
			if p.kw("DEFAULT") {
				s.Default = parseDefaultExpr(p)
				continue
			}
			p.i++
		}
		return s

	case p.kw("DROP", "COLUMN"), p.kw("DROP"):
		if p.kw("CONSTRAINT") {
			s.Kind = DropConstraint
			s.Column = p.ident()
			return s
		}
		p.kw("IF", "EXISTS")
		s.Kind = DropColumn
		s.Column = p.ident()
		return s

	case p.kw("ALTER", "COLUMN"), p.kw("ALTER"):
		s.Column = p.ident()
		switch {
		case p.kw("SET", "NOT", "NULL"):
			s.Kind = SetNotNull
		case p.kw("DROP", "NOT", "NULL"):
			s.Kind = DropNotNull
		case p.kw("SET", "DEFAULT"):
			s.Kind = SetDefault
			s.Default = parseDefaultExpr(p)
		case p.kw("DROP", "DEFAULT"):
			s.Kind = DropDefault
		case p.kw("SET", "STATISTICS"):
			s.Kind = SetStatistics
		case p.kw("TYPE"), p.kw("SET", "DATA", "TYPE"):
			s.Kind = AlterColumnType
			s.Type = p.ident()
			// A type name may carry a parenthesised modifier.
			if p.peek().upper == "(" {
				for !p.eof() && p.peek().upper != ")" {
					s.Type += p.t[p.i].text
					p.i++
				}
				if !p.eof() {
					s.Type += ")"
					p.i++
				}
			}
			if p.kw("USING") {
				s.UsingExpr = p.rest()
			}
		default:
			s.Kind = Unknown
		}
		return s

	case p.kw("VALIDATE", "CONSTRAINT"):
		s.Kind = ValidateConstraint
		s.Column = p.ident()
		return s

	case p.kw("RENAME", "COLUMN"):
		s.Kind = RenameColumn
		s.Column = p.ident()
		return s

	case p.kw("RENAME", "TO"):
		s.Kind = RenameTable
		return s

	case p.kw("RENAME"):
		s.Kind = RenameColumn
		s.Column = p.ident()
		return s
	}
	s.Kind = Unknown
	return s
}

// parseAddConstraint handles the ADD [CONSTRAINT name] <type> forms. It
// reports false if what follows ADD is a plain column definition.
func parseAddConstraint(p *parser, s *Stmt) (Stmt, bool) {
	save := p.i
	if p.kw("CONSTRAINT") {
		s.Column = p.ident()
	}
	switch {
	case p.kw("CHECK"):
		s.Kind = AddCheck
	case p.kw("FOREIGN", "KEY"):
		s.Kind = AddForeignKey
	case p.kw("UNIQUE"):
		s.Kind = AddUnique
	case p.kw("PRIMARY", "KEY"):
		s.Kind = AddPrimaryKey
	default:
		p.i = save
		return Stmt{}, false
	}
	mark := p.i
	if p.scanTo("USING", "INDEX") {
		s.UsingIndex = p.ident()
	}
	p.i = mark
	if p.scanTo("NOT", "VALID") {
		s.NotValid = true
	}
	return *s, true
}

// parseDefaultExpr captures a default expression.
//
// The termination rule is a stop-set of column-constraint keywords rather
// than "one token unless a call follows". The earlier heuristic broke on
// three real forms: `DEFAULT (1)::int` (the `)::int` token never matched a
// bare `)` so the paren depth never returned to zero and the scanner ate the
// rest of the statement), `DEFAULT now() AT TIME ZONE 'utc'` (stopped after
// the call), and anything with an infix operator.
//
// Parenthesis depth is counted per rune rather than per token, because the
// lexer only splits on a leading or trailing paren -- `)::int` is one token
// that closes a group.
func parseDefaultExpr(p *parser) string {
	var parts []token
	depth := 0
	for !p.eof() {
		t := p.t[p.i]
		if depth == 0 && !t.quoted && !t.str && defaultStop[t.upper] {
			break
		}
		for _, r := range t.text {
			switch r {
			case '(':
				depth++
			case ')':
				depth--
			}
		}
		if depth < 0 {
			// A closing paren we never opened belongs to the enclosing
			// construct, not to us. Do not consume it.
			break
		}
		parts = append(parts, t)
		p.i++
	}
	return joinExpr(parts)
}

// defaultStop is the set of tokens that end a DEFAULT expression at depth
// zero. They are exactly the column constraints and separators that may
// legally follow one.
var defaultStop = map[string]bool{
	"NOT": true, "NULL": true, "DEFAULT": true, "REFERENCES": true,
	"CHECK": true, "UNIQUE": true, "PRIMARY": true, "CONSTRAINT": true,
	"GENERATED": true, "COLLATE": true, "DEFERRABLE": true, ",": true,
}

// joinExpr reassembles tokens into a readable expression: no space around
// parentheses or before a comma, single spaces elsewhere. Deterministic,
// because the Default string ends up in a byte-compared report.
func joinExpr(ts []token) string {
	var b strings.Builder
	for i, t := range ts {
		text := t.text
		if t.str {
			text = "'" + strings.ReplaceAll(t.text, "'", "''") + "'"
		} else if t.quoted {
			text = `"` + t.text + `"`
		}
		if i > 0 {
			prev := ts[i-1]
			noSpace := text == "," ||
				strings.HasPrefix(text, ")") ||
				(!prev.str && !prev.quoted && strings.HasSuffix(prev.text, "("))
			if !noSpace {
				b.WriteString(" ")
			}
		}
		b.WriteString(text)
	}
	return b.String()
}

func parseCreateIndex(p *parser) Stmt {
	s := Stmt{Kind: CreateIndex}
	s.Concurrently = p.kw("CONCURRENTLY")
	s.IfNotExists = p.kw("IF", "NOT", "EXISTS")
	// `CREATE INDEX ON t (...)` is legal: the name is optional.
	if p.peek().upper != "ON" {
		s.Index = p.ident()
	}
	if p.kw("ON") {
		p.kw("ONLY")
		s.Table = p.ident()
	}
	return s
}

func parseDropIndex(p *parser) Stmt {
	s := Stmt{Kind: DropIndex}
	s.Concurrently = p.kw("CONCURRENTLY")
	s.IfExists = p.kw("IF", "EXISTS")
	s.Index = p.ident()
	return s
}
