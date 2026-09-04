package ddl

import (
	"strings"
	"testing"
)

func parseOneT(t *testing.T, sql string) Stmt {
	t.Helper()
	st, err := Parse(sql)
	if err != nil {
		t.Fatalf("Parse(%q): %v", sql, err)
	}
	if len(st) != 1 {
		t.Fatalf("Parse(%q): got %d statements, want 1", sql, len(st))
	}
	return st[0]
}

func TestSplitBasic(t *testing.T) {
	st, err := Parse("ALTER TABLE a ADD COLUMN x int; ALTER TABLE b DROP COLUMN y;")
	if err != nil {
		t.Fatal(err)
	}
	if len(st) != 2 {
		t.Fatalf("got %d statements", len(st))
	}
	if st[0].Kind != AddColumn || st[1].Kind != DropColumn {
		t.Fatalf("kinds: %s, %s", st[0].Kind, st[1].Kind)
	}
}

func TestTrailingSemicolonOptional(t *testing.T) {
	a := parseOneT(t, "ALTER TABLE a ADD COLUMN x int")
	b := parseOneT(t, "ALTER TABLE a ADD COLUMN x int;")
	if a.Kind != b.Kind || a.Column != b.Column {
		t.Fatal("trailing semicolon changed the parse")
	}
}

func TestEmptyStatementsDropped(t *testing.T) {
	st, err := Parse(";;;  ;\n;")
	if err != nil {
		t.Fatal(err)
	}
	if len(st) != 0 {
		t.Fatalf("got %d statements from an empty script", len(st))
	}
}

// The comment and string cases are the reason this is a parser and not a
// regex. A safety tool that lints a statement inside a comment will refuse
// migrations that are fine, and -- worse -- a tool that misses a statement
// because a string literal confused its splitter will pass one that is not.

func TestLineCommentHidesSemicolon(t *testing.T) {
	st, err := Parse("ALTER TABLE a ADD COLUMN x int -- ; not a split\n;")
	if err != nil {
		t.Fatal(err)
	}
	if len(st) != 1 {
		t.Fatalf("got %d statements, want 1", len(st))
	}
}

func TestBlockCommentHidesStatement(t *testing.T) {
	st, err := Parse("/* DROP TABLE users; */ ALTER TABLE a ADD COLUMN x int;")
	if err != nil {
		t.Fatal(err)
	}
	if len(st) != 1 {
		t.Fatalf("got %d statements, want 1: %+v", len(st), st)
	}
	if st[0].Kind != AddColumn {
		t.Fatalf("commented-out DROP TABLE leaked into the parse: %s", st[0].Kind)
	}
}

func TestNestedBlockComment(t *testing.T) {
	// PostgreSQL block comments nest. A naive scan-to-*/ ends the comment at
	// the inner terminator and treats the rest as SQL.
	st, err := Parse("/* outer /* inner */ DROP TABLE users; */ ALTER TABLE a ADD COLUMN x int;")
	if err != nil {
		t.Fatal(err)
	}
	if len(st) != 1 {
		t.Fatalf("nested comment mishandled: got %d statements %+v", len(st), st)
	}
	if st[0].Kind != AddColumn {
		t.Fatalf("got %s", st[0].Kind)
	}
}

func TestSemicolonInsideStringLiteral(t *testing.T) {
	st, err := Parse("ALTER TABLE a ALTER COLUMN x SET DEFAULT 'a;b';")
	if err != nil {
		t.Fatal(err)
	}
	if len(st) != 1 {
		t.Fatalf("split inside a string literal: got %d", len(st))
	}
}

func TestEscapedQuoteInString(t *testing.T) {
	st, err := Parse("ALTER TABLE a ALTER COLUMN x SET DEFAULT 'it''s; fine';")
	if err != nil {
		t.Fatal(err)
	}
	if len(st) != 1 {
		t.Fatalf("doubled quote mishandled: got %d", len(st))
	}
}

func TestDollarQuoting(t *testing.T) {
	st, err := Parse("ALTER TABLE a ALTER COLUMN x SET DEFAULT $tag$ a;b $tag$;")
	if err != nil {
		t.Fatal(err)
	}
	if len(st) != 1 {
		t.Fatalf("dollar-quoted body mishandled: got %d", len(st))
	}
}

func TestQuotedIdentifierWithSemicolon(t *testing.T) {
	st, err := Parse(`ALTER TABLE "we;ird" ADD COLUMN x int;`)
	if err != nil {
		t.Fatal(err)
	}
	if len(st) != 1 {
		t.Fatalf("got %d", len(st))
	}
}

func TestUnterminatedStringIsAnError(t *testing.T) {
	if _, err := Parse("ALTER TABLE a SET DEFAULT 'oops"); err == nil {
		t.Fatal("expected an error for an unterminated string literal")
	}
}

func TestUnterminatedBlockCommentIsAnError(t *testing.T) {
	if _, err := Parse("/* oops ALTER TABLE a ADD COLUMN x int;"); err == nil {
		t.Fatal("expected an error for an unterminated block comment")
	}
}

func TestLineNumbers(t *testing.T) {
	st, err := Parse("ALTER TABLE a ADD COLUMN x int;\n\nALTER TABLE b DROP COLUMN y;")
	if err != nil {
		t.Fatal(err)
	}
	if st[0].Line != 1 {
		t.Fatalf("first statement line = %d", st[0].Line)
	}
	if st[1].Line != 3 {
		t.Fatalf("second statement line = %d, want 3", st[1].Line)
	}
}

func TestAddColumnFields(t *testing.T) {
	s := parseOneT(t, "ALTER TABLE public.orders ADD COLUMN status text NOT NULL DEFAULT 'new'")
	if s.Kind != AddColumn {
		t.Fatalf("kind %s", s.Kind)
	}
	if s.Table != "public.orders" {
		t.Fatalf("table %q", s.Table)
	}
	if s.Column != "status" {
		t.Fatalf("column %q", s.Column)
	}
	if !s.NotNull {
		t.Fatal("NOT NULL not detected")
	}
	if s.Default != "'new'" {
		t.Fatalf("default %q", s.Default)
	}
	if !strings.Contains(strings.ToLower(s.Type), "text") {
		t.Fatalf("type %q", s.Type)
	}
}

func TestAddColumnIfNotExists(t *testing.T) {
	s := parseOneT(t, "ALTER TABLE orders ADD COLUMN IF NOT EXISTS status text")
	if s.Kind != AddColumn || s.Column != "status" {
		t.Fatalf("kind=%s column=%q", s.Kind, s.Column)
	}
	if !s.IfNotExists {
		t.Fatal("IF NOT EXISTS not detected")
	}
}

func TestAddColumnWithoutKeyword(t *testing.T) {
	// COLUMN is optional in PostgreSQL.
	s := parseOneT(t, "ALTER TABLE orders ADD status text")
	if s.Kind != AddColumn || s.Column != "status" {
		t.Fatalf("kind=%s column=%q", s.Kind, s.Column)
	}
}

func TestAddConstraintNotConfusedWithAddColumn(t *testing.T) {
	// `ADD <name>` and `ADD CONSTRAINT <name>` differ by one keyword and mean
	// entirely different things.
	s := parseOneT(t, "ALTER TABLE orders ADD CONSTRAINT ck_total CHECK (total >= 0)")
	if s.Kind != AddCheck {
		t.Fatalf("kind %s", s.Kind)
	}
	if s.Column != "ck_total" {
		t.Fatalf("constraint name %q", s.Column)
	}
}

func TestAddConstraintVariants(t *testing.T) {
	cases := map[string]Kind{
		"ALTER TABLE t ADD CONSTRAINT c CHECK (x > 0)":                     AddCheck,
		"ALTER TABLE t ADD CONSTRAINT c FOREIGN KEY (a) REFERENCES b (id)": AddForeignKey,
		"ALTER TABLE t ADD CONSTRAINT c UNIQUE (a)":                        AddUnique,
		"ALTER TABLE t ADD CONSTRAINT c PRIMARY KEY (a)":                   AddPrimaryKey,
		"ALTER TABLE t ADD CHECK (x > 0)":                                  AddCheck,
		"ALTER TABLE t ADD FOREIGN KEY (a) REFERENCES b (id)":              AddForeignKey,
		"ALTER TABLE t ADD UNIQUE (a)":                                     AddUnique,
		"ALTER TABLE t ADD PRIMARY KEY (a)":                                AddPrimaryKey,
		"ALTER TABLE t DROP CONSTRAINT c":                                  DropConstraint,
		"ALTER TABLE t VALIDATE CONSTRAINT c":                              ValidateConstraint,
		"ALTER TABLE t ALTER COLUMN a SET NOT NULL":                        SetNotNull,
		"ALTER TABLE t ALTER COLUMN a DROP NOT NULL":                       DropNotNull,
		"ALTER TABLE t ALTER COLUMN a SET DEFAULT 1":                       SetDefault,
		"ALTER TABLE t ALTER COLUMN a DROP DEFAULT":                        DropDefault,
		"ALTER TABLE t ALTER COLUMN a TYPE bigint":                         AlterColumnType,
		"ALTER TABLE t ALTER COLUMN a SET DATA TYPE bigint":                AlterColumnType,
		"ALTER TABLE t ALTER COLUMN a SET STATISTICS 500":                  SetStatistics,
		"ALTER TABLE t RENAME COLUMN a TO b":                               RenameColumn,
		"ALTER TABLE t RENAME TO u":                                        RenameTable,
		"ALTER TABLE t DROP COLUMN a":                                      DropColumn,
		"CREATE INDEX i ON t (a)":                                          CreateIndex,
		"CREATE UNIQUE INDEX CONCURRENTLY i ON t (a)":                      CreateIndex,
		"DROP INDEX i":                        DropIndex,
		"DROP INDEX CONCURRENTLY IF EXISTS i": DropIndex,
		"DROP TABLE t":                        DropTable,
		"CREATE TABLE t (a int)":              CreateTable,
		"CLUSTER t USING i":                   Cluster,
		"VACUUM FULL t":                       Vacuum,
		"ANALYZE t":                           Analyze,
		"ALTER TABLE t ADD CONSTRAINT c FOREIGN KEY (a) REFERENCES b NOT VALID": AddForeignKey,
	}
	for sql, want := range cases {
		s := parseOneT(t, sql)
		if s.Kind != want {
			t.Errorf("%q -> %s, want %s", sql, s.Kind, want)
		}
	}
}

func TestNotValidDetected(t *testing.T) {
	s := parseOneT(t, "ALTER TABLE t ADD CONSTRAINT c CHECK (x > 0) NOT VALID")
	if !s.NotValid {
		t.Fatal("NOT VALID not detected")
	}
	s2 := parseOneT(t, "ALTER TABLE t ADD CONSTRAINT c CHECK (x > 0)")
	if s2.NotValid {
		t.Fatal("NOT VALID falsely detected")
	}
}

func TestConcurrentlyDetected(t *testing.T) {
	if !parseOneT(t, "CREATE INDEX CONCURRENTLY i ON t (a)").Concurrently {
		t.Fatal("CREATE INDEX CONCURRENTLY not detected")
	}
	if parseOneT(t, "CREATE INDEX i ON t (a)").Concurrently {
		t.Fatal("CONCURRENTLY falsely detected")
	}
	if !parseOneT(t, "DROP INDEX CONCURRENTLY i").Concurrently {
		t.Fatal("DROP INDEX CONCURRENTLY not detected")
	}
}

func TestCreateIndexTableAndName(t *testing.T) {
	s := parseOneT(t, "CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS idx_orders_ref ON public.orders USING btree (ref)")
	if s.Index != "idx_orders_ref" {
		t.Fatalf("index %q", s.Index)
	}
	if s.Table != "public.orders" {
		t.Fatalf("table %q", s.Table)
	}
	if !s.Unique {
		t.Fatal("UNIQUE not detected")
	}
	if !s.IfNotExists {
		t.Fatal("IF NOT EXISTS not detected")
	}
}

func TestAlterColumnTypeUsing(t *testing.T) {
	s := parseOneT(t, "ALTER TABLE t ALTER COLUMN a TYPE bigint USING a::bigint")
	if s.Kind != AlterColumnType {
		t.Fatalf("kind %s", s.Kind)
	}
	if s.Type != "bigint" {
		t.Fatalf("type %q", s.Type)
	}
	if s.UsingExpr == "" {
		t.Fatal("USING expression not captured")
	}
}

func TestAlterColumnTypeWithoutUsing(t *testing.T) {
	s := parseOneT(t, "ALTER TABLE t ALTER COLUMN a TYPE bigint")
	if s.UsingExpr != "" {
		t.Fatalf("USING falsely captured: %q", s.UsingExpr)
	}
}

func TestDefaultWithCast(t *testing.T) {
	// `1::int` must survive as one expression. The lexer does not break on
	// `:`, so this exercises whether the default scanner stops in the right
	// place.
	s := parseOneT(t, "ALTER TABLE t ADD COLUMN a int DEFAULT 0::int")
	if s.Default != "0::int" {
		t.Fatalf("default %q, want %q", s.Default, "0::int")
	}
}

func TestDefaultFunctionCallWithArgs(t *testing.T) {
	s := parseOneT(t, "ALTER TABLE t ADD COLUMN a text DEFAULT coalesce(b, 'x')")
	if !strings.HasPrefix(s.Default, "coalesce") {
		t.Fatalf("default %q", s.Default)
	}
	if !strings.Contains(s.Default, "'x'") {
		t.Fatalf("default lost its argument list: %q", s.Default)
	}
}

func TestDefaultBeforeNotNull(t *testing.T) {
	// DEFAULT and NOT NULL can appear in either order.
	a := parseOneT(t, "ALTER TABLE t ADD COLUMN a int NOT NULL DEFAULT 0")
	b := parseOneT(t, "ALTER TABLE t ADD COLUMN a int DEFAULT 0 NOT NULL")
	if !a.NotNull || !b.NotNull {
		t.Fatalf("NOT NULL: a=%v b=%v", a.NotNull, b.NotNull)
	}
	if a.Default != "0" || b.Default != "0" {
		t.Fatalf("defaults: a=%q b=%q", a.Default, b.Default)
	}
}

func TestUnknownStatementIsNotDropped(t *testing.T) {
	// The critical safety property: something the parser does not understand
	// must survive as Unknown so the linter can refuse it, not vanish.
	st, err := Parse("GRANT SELECT ON t TO alice; ALTER TABLE t ADD COLUMN x int;")
	if err != nil {
		t.Fatal(err)
	}
	if len(st) != 2 {
		t.Fatalf("got %d statements, want 2", len(st))
	}
	if st[0].Kind != Unknown {
		t.Fatalf("GRANT parsed as %s, expected Unknown", st[0].Kind)
	}
	if st[0].Raw == "" {
		t.Fatal("Unknown statement lost its Raw text")
	}
}

func TestCaseInsensitive(t *testing.T) {
	a := parseOneT(t, "alter table t add column x int not null default 0")
	if a.Kind != AddColumn || !a.NotNull || a.Default != "0" {
		t.Fatalf("lowercase parse failed: %+v", a)
	}
}

func TestExtraWhitespaceAndNewlines(t *testing.T) {
	a := parseOneT(t, "ALTER\n  TABLE\n\tt\n  ADD  COLUMN   x\n  int")
	if a.Kind != AddColumn || a.Column != "x" {
		t.Fatalf("%+v", a)
	}
}

func TestKindStringsAreDistinct(t *testing.T) {
	seen := map[string]Kind{}
	for k := Unknown; k <= Analyze; k++ {
		s := k.String()
		if strings.HasPrefix(s, "Kind(") {
			t.Fatalf("kind %d has no name", int(k))
		}
		if prev, dup := seen[s]; dup {
			t.Fatalf("kinds %d and %d share the name %q", int(prev), int(k), s)
		}
		seen[s] = k
	}
}

func TestRawPreserved(t *testing.T) {
	sql := "ALTER TABLE t ADD COLUMN x int NOT NULL DEFAULT 0"
	s := parseOneT(t, sql)
	if strings.TrimSpace(s.Raw) != sql {
		t.Fatalf("Raw = %q", s.Raw)
	}
}

func TestParseIsDeterministic(t *testing.T) {
	// Regression tests for the four defects the first test run exposed. Each
	// one is a way a migration tool can be confidently, silently wrong.

	t.Run("schema qualifier survives", func(t *testing.T) {
		// The dangerous one: the parser used to strip the schema, and the
		// plan package echoes Table straight back into generated SQL.
		for _, sql := range []string{
			"ALTER TABLE public.orders ADD COLUMN x int",
			"CREATE INDEX i ON public.orders (x)",
			"DROP TABLE archive.orders",
			"CLUSTER public.orders USING i",
		} {
			s := parseOneT(t, sql)
			if Schema(s.Table) == "" {
				t.Errorf("%q lost its schema: table=%q", sql, s.Table)
			}
		}
	})

	t.Run("qualified names are distinguishable", func(t *testing.T) {
		a := parseOneT(t, "ALTER TABLE public.orders ADD COLUMN x int")
		b := parseOneT(t, "ALTER TABLE archive.orders ADD COLUMN x int")
		if a.Table == b.Table {
			t.Fatalf("two relations collapsed to one name: %q", a.Table)
		}
		if Bare(a.Table) != "orders" || Bare(b.Table) != "orders" {
			t.Fatalf("Bare: %q %q", Bare(a.Table), Bare(b.Table))
		}
		if Schema(a.Table) != "public" || Schema(b.Table) != "archive" {
			t.Fatalf("Schema: %q %q", Schema(a.Table), Schema(b.Table))
		}
	})

	t.Run("bare and schema on unqualified names", func(t *testing.T) {
		if Bare("orders") != "orders" {
			t.Fatal("Bare mangled an unqualified name")
		}
		if Schema("orders") != "" {
			t.Fatal("Schema invented a qualifier")
		}
		if Schema(".orders") != "" {
			t.Fatalf("Schema of a leading dot: %q", Schema(".orders"))
		}
	})

	t.Run("if not exists is recorded", func(t *testing.T) {
		if !parseOneT(t, "ALTER TABLE t ADD COLUMN IF NOT EXISTS x int").IfNotExists {
			t.Fatal("ADD COLUMN IF NOT EXISTS not recorded")
		}
	})

	t.Run("statement line numbers", func(t *testing.T) {
		st, err := Parse("-- header\n\nALTER TABLE a ADD COLUMN x int;\n\n\nALTER TABLE b DROP COLUMN y;")
		if err != nil {
			t.Fatal(err)
		}
		if st[0].Line != 3 || st[1].Line != 6 {
			t.Fatalf("lines = %d, %d; want 3, 6", st[0].Line, st[1].Line)
		}
	})

	t.Run("parenthesised cast default", func(t *testing.T) {
		// `)::int` is one token; matching a bare `)` left the depth counter
		// stuck above zero and the scanner consumed the rest of the statement.
		s := parseOneT(t, "ALTER TABLE t ADD COLUMN a int DEFAULT (1)::int NOT NULL")
		if !s.NotNull {
			t.Fatal("NOT NULL swallowed by the default expression")
		}
		if strings.Contains(strings.ToUpper(s.Default), "NOT NULL") {
			t.Fatalf("default ate the column constraints: %q", s.Default)
		}
	})

	t.Run("multi-token default", func(t *testing.T) {
		s := parseOneT(t, "ALTER TABLE t ALTER COLUMN a SET DEFAULT now() AT TIME ZONE 'utc'")
		if !strings.Contains(strings.ToLower(s.Default), "time zone") {
			t.Fatalf("default truncated after the function call: %q", s.Default)
		}
	})

	t.Run("infix default", func(t *testing.T) {
		s := parseOneT(t, "ALTER TABLE t ADD COLUMN a int DEFAULT 1 + 2")
		if s.Default != "1 + 2" {
			t.Fatalf("default %q, want %q", s.Default, "1 + 2")
		}
	})

	t.Run("default stops at a following constraint", func(t *testing.T) {
		s := parseOneT(t, "ALTER TABLE t ADD COLUMN a int DEFAULT 0 REFERENCES other (id)")
		if s.Default != "0" {
			t.Fatalf("default %q", s.Default)
		}
	})

	sql := "ALTER TABLE t ADD COLUMN x int NOT NULL DEFAULT 0; CREATE INDEX CONCURRENTLY i ON t (x);"
	first, _ := Parse(sql)
	for i := 0; i < 50; i++ {
		got, _ := Parse(sql)
		if len(got) != len(first) {
			t.Fatal("statement count varied")
		}
		for j := range got {
			if got[j] != first[j] {
				t.Fatalf("run %d statement %d differs:\n%+v\n%+v", i, j, got[j], first[j])
			}
		}
	}
}

// TestTransactionControlAndTriggersParse covers the statements the plan
// generator emits but the parser originally did not understand.
//
// Every one of them was previously reported as Kind Unknown, which the linter
// treats as "assume ACCESS EXCLUSIVE and refuse". The result was that the
// tool's own recommended plans -- which wrap renames in a transaction and
// clean up their sync trigger -- failed their own lint. The lesson is that a
// parser's gaps are not neutral: an "unknown" verdict is itself a claim, and
// here it was a false one.
func TestTransactionControlAndTriggersParse(t *testing.T) {
	cases := []struct {
		sql   string
		kind  Kind
		table string
		name  string
	}{
		{"BEGIN", Begin, "", ""},
		{"BEGIN;", Begin, "", ""},
		{"START TRANSACTION", Begin, "", ""},
		{"COMMIT", Commit, "", ""},
		{"END", Commit, "", ""},
		{"ROLLBACK", Rollback, "", ""},
		{"SET lock_timeout = '2s'", SetParameter, "", ""},
		{"DROP TRIGGER orders_ref_sync ON public.orders", DropTrigger, "public.orders", "orders_ref_sync"},
		{"DROP TRIGGER IF EXISTS t1 ON orders", DropTrigger, "orders", "t1"},
		{"CREATE TRIGGER t1 BEFORE INSERT ON orders FOR EACH ROW EXECUTE FUNCTION f()", CreateTrigger, "orders", "t1"},
	}
	for _, c := range cases {
		t.Run(c.sql, func(t *testing.T) {
			got, err := Parse(c.sql)
			if err != nil {
				t.Fatalf("parse: %v", err)
			}
			if len(got) != 1 {
				t.Fatalf("got %d statements, want 1", len(got))
			}
			if got[0].Kind != c.kind {
				t.Errorf("kind = %v, want %v", got[0].Kind, c.kind)
			}
			if c.table != "" && got[0].Table != c.table {
				t.Errorf("table = %q, want %q", got[0].Table, c.table)
			}
			if c.name != "" && got[0].Index != c.name {
				t.Errorf("name = %q, want %q", got[0].Index, c.name)
			}
		})
	}
}

// TestUsingIndexIsParsed pins the clause that decides whether adding a
// constraint is catalogue-only or a blocking index build. The two statements
// differ by four words and by roughly the entire risk of the migration.
func TestUsingIndexIsParsed(t *testing.T) {
	inline, err := Parse("ALTER TABLE orders ADD CONSTRAINT uq UNIQUE (ref)")
	if err != nil {
		t.Fatal(err)
	}
	if inline[0].UsingIndex != "" {
		t.Errorf("UsingIndex = %q, want empty", inline[0].UsingIndex)
	}

	adopted, err := Parse("ALTER TABLE orders ADD CONSTRAINT uq UNIQUE USING INDEX idx_uq")
	if err != nil {
		t.Fatal(err)
	}
	if adopted[0].Kind != AddUnique {
		t.Errorf("kind = %v, want AddUnique", adopted[0].Kind)
	}
	if adopted[0].UsingIndex != "idx_uq" {
		t.Errorf("UsingIndex = %q, want %q", adopted[0].UsingIndex, "idx_uq")
	}

	pk, err := Parse("ALTER TABLE orders ADD CONSTRAINT pk PRIMARY KEY USING INDEX idx_pk")
	if err != nil {
		t.Fatal(err)
	}
	if pk[0].Kind != AddPrimaryKey || pk[0].UsingIndex != "idx_pk" {
		t.Errorf("got kind=%v using=%q", pk[0].Kind, pk[0].UsingIndex)
	}
}

// TestUsingIndexScanDoesNotSwallowNotValid checks that looking ahead for
// USING INDEX leaves the cursor where the NOT VALID scan expects it. The two
// clauses are scanned independently from the same mark; a shared cursor made
// whichever ran second silently return false.
func TestUsingIndexScanDoesNotSwallowNotValid(t *testing.T) {
	got, err := Parse("ALTER TABLE orders ADD CONSTRAINT ck CHECK (amount > 0) NOT VALID")
	if err != nil {
		t.Fatal(err)
	}
	if !got[0].NotValid {
		t.Error("NOT VALID was lost")
	}
}
