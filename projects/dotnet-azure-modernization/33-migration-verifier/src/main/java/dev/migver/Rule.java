package dev.migver;

import java.math.BigDecimal;
import java.text.Normalizer;
import java.util.*;

/**
 * A canonicalisation rule: a transformation applied to both sides before
 * comparison so that a known-harmless difference stops being reported.
 *
 * <p>Every rule here was added for a good reason and every rule here blinds the
 * verifier to something. That is not a flaw in the rules, it is the structure of
 * the problem: a rule that suppresses a difference cannot distinguish between
 * the harmless cause it was written for and a harmful cause that produces the
 * same difference. The verifier's precision and its recall are traded against
 * each other one rule at a time, and nobody making the trade is usually looking
 * at the other side of it.
 *
 * <p>{@link Experiments} measures both sides.
 */
public enum Rule {

    /** Strip leading and trailing whitespace. Written for CHAR(10) padding. */
    TRIM("trim", "CHAR(10) padding on the source renders as trailing spaces the target does not have.", "name", "code"),

    /** Unicode NFC. Written for accented names that compare unequal byte-wise. */
    NFC("nfc", "Composed and decomposed spellings of the same name compare unequal.", "name"),

    /** Compare numbers by value, not by representation. Written for BigDecimal vs Double. */
    NUMERIC("numeric", "DECIMAL arrives as BigDecimal from one driver and Double from the other.", "amount"),

    /** Coerce booleans to a common form. Written for TRUE vs 1. */
    BOOLEAN("boolean", "BOOLEAN arrives as Boolean from one driver and Integer from the other.", "active"),

    /** Case-fold text. Written to match the target's case-insensitive collation. */
    CASEFOLD("casefold", "The target collation is case-insensitive, so case differences are not real differences.", "name"),

    /**
     * Compare instants, not renderings. Written because the target stores
     * timestamps as epoch milliseconds and the source hands back a
     * {@link java.sql.Timestamp}.
     *
     * <p>This rule is the one that makes the others worth having; see the
     * lattice sweep in {@link Experiments}. It is also the most dangerous,
     * because the conversion it inverts is the one place in the pipeline where
     * the result depends on something that is not in either database.
     */
    TEMPORAL("temporal", "TIMESTAMP arrives as java.sql.Timestamp from one driver and epoch millis from the other.", "seen"),

    /**
     * Compare identifiers as text. Written because the source stores account
     * numbers as VARCHAR and the target tightened the column to INTEGER, so the
     * drivers hand back a String and an Integer for the same account.
     *
     * <p>Note what it does <em>not</em> do. The obvious way to reconcile a String
     * with an Integer is to parse both as numbers, and that version of this rule
     * silences exactly the same noise while destroying the ability to see that
     * {@code '0000007'} became {@code 7}. Comparing as text is injective;
     * comparing as numbers is not. That distinction is the whole of section 11.
     */
    IDENTIFIER("identifier", "account is VARCHAR on the source and INTEGER on the target.", "account");

    private final String id;
    private final String motivation;
    private final java.util.Set<String> columns;

    Rule(String id, String motivation, String... columns) {
        this.id = id;
        this.motivation = motivation;
        // LinkedHashSet, not Set.of. Set.of randomises iteration order per JVM
        // via ImmutableCollections.SALT, so a rule's column list rendered into
        // the report came out as [name, code] in one JVM and [code, name] in
        // the next. The determinism stage of test.ps1 runs three JVMs and did
        // not catch it -- the identity hashes happened to line up. The
        // report-freshness test, run as part of a full suite where surefire
        // loads classes in a different order, caught it about half the time.
        // A report generator with a coin flip in it is not evidence of
        // anything, and this is the kind of defect that hides for months.
        this.columns = java.util.Collections.unmodifiableSet(
                new java.util.LinkedHashSet<>(Arrays.asList(columns)));
    }

    /**
     * The columns this rule governs.
     *
     * <p>Scoping rules to columns rather than to value types is not cosmetic. An
     * earlier version applied each rule to every value it recognised, and the
     * NUMERIC rule -- which normalises integers -- silently consumed the
     * epoch-millisecond timestamps before the TEMPORAL rule could see them, so
     * TEMPORAL stopped working whenever NUMERIC was enabled. Nothing failed; the
     * verifier simply reported more differences, and the obvious diagnosis was
     * that TEMPORAL was not worth having.
     *
     * <p>Value-scoped rules compose by accident. Column-scoped rules compose by
     * construction, which is the only kind of composition you can reason about.
     */
    public java.util.Set<String> columns() {
        return columns;
    }

    public boolean governs(String column) {
        return columns.contains(column);
    }

    public String id() {
        return id;
    }

    /** The true, correct observation that justified adding this rule. */
    public String motivation() {
        return motivation;
    }

    /** Apply this rule to one value, or return it unchanged if the rule does not apply. */
    public Object apply(Object v) {
        if (v == null) {
            return null;
        }
        return switch (this) {
            case TRIM -> v instanceof String s ? s.trim() : v;
            case NFC -> v instanceof String s ? Normalizer.normalize(s, Normalizer.Form.NFC) : v;
            case NUMERIC -> numeric(v);
            case BOOLEAN -> bool(v);
            case CASEFOLD -> v instanceof String s ? s.toUpperCase(Locale.ROOT) : v;
            case TEMPORAL -> temporal(v);
            case IDENTIFIER -> String.valueOf(v);
        };
    }

    /**
     * Whether this rule is injective: whether two values it maps together were
     * necessarily the same value to begin with.
     *
     * <p>This is the same criterion the corpus uses to decide whether a hazard
     * corrupts, applied to the verifier instead of to the data, and it predicts
     * the blindfold matrix exactly. A rule that merely reconciles two
     * representations of one value cannot hide a defect, because a defect would
     * have produced two different values and the rule maps different values to
     * different results. A rule that decides two <em>different</em> values should
     * count as equal will hide every defect that produces one of them from the
     * other, and there is no way to have the one property without the other.
     *
     * <p>{@link #TEMPORAL} is marked non-injective and deserves its own sentence.
     * As a function on longs it is injective. What it inverts is not injective:
     * the driver's wall-clock-to-instant conversion depends on a time zone that
     * is not recorded in either database, so equal epoch milliseconds do not
     * imply the source and target agree about what time it was.
     */
    public boolean isInjective() {
        return switch (this) {
            case TRIM, NFC, CASEFOLD, TEMPORAL -> false;
            case NUMERIC, BOOLEAN, IDENTIFIER -> true;
        };
    }

    private static Object temporal(Object v) {
        if (v instanceof java.sql.Timestamp t) {
            return "@" + t.getTime();
        }
        if (v instanceof java.util.Date d) {
            return "@" + d.getTime();
        }
        if (v instanceof Long l && l > 1_000_000_000_000L) {
            // Heuristic, and worth being uncomfortable about: any integer past
            // 2001 in epoch milliseconds is treated as a timestamp. A genuine
            // account number of that magnitude would be silently reinterpreted.
            // Canonicalisation rules are guesses about intent, and this is what
            // one looks like when written honestly.
            return "@" + l;
        }
        return v;
    }

    private static Object numeric(Object v) {
        // Comparing as BigDecimal via a canonical plain string, rather than as
        // double, because going through double would itself lose the digits that
        // DECIMAL_TO_BINARY_FLOAT is about -- the canonicaliser would then be
        // committing the very error it is meant to be tolerant of.
        if (v instanceof BigDecimal d) {
            return d.stripTrailingZeros().toPlainString();
        }
        if (v instanceof Double d) {
            return new BigDecimal(d.toString()).stripTrailingZeros().toPlainString();
        }
        if (v instanceof Float f) {
            return new BigDecimal(f.toString()).stripTrailingZeros().toPlainString();
        }
        if (v instanceof Number n && !(v instanceof Long) && !(v instanceof Integer)
                && !(v instanceof Short)) {
            return new BigDecimal(n.toString()).stripTrailingZeros().toPlainString();
        }
        // Integers included, because SQLite's affinity stores DECIMAL 10.0000 as
        // the integer 10 and a rule that ignored integers would report every
        // whole-pound amount as a difference. This is safe only because the rule
        // is scoped to the amount column; when it applied to every value it also
        // consumed the epoch-millisecond timestamps and disabled TEMPORAL.
        if (v instanceof Long || v instanceof Integer || v instanceof Short) {
            return new BigDecimal(v.toString()).stripTrailingZeros().toPlainString();
        }
        return v;
    }

    private static Object bool(Object v) {
        if (v instanceof Boolean b) {
            return b ? "1" : "0";
        }
        if (v instanceof Number n && (n.intValue() == 0 || n.intValue() == 1)) {
            return n.intValue() == 1 ? "1" : "0";
        }
        return v;
    }

    /** A set of rules, applied left to right in enum order. */
    public record Set(EnumSet<Rule> enabled) {

        public static Set none() {
            return new Set(EnumSet.noneOf(Rule.class));
        }

        public static Set of(Rule... rules) {
            return new Set(rules.length == 0 ? EnumSet.noneOf(Rule.class)
                    : EnumSet.copyOf(Arrays.asList(rules)));
        }

        public static Set all() {
            return new Set(EnumSet.allOf(Rule.class));
        }

        public Set plus(Rule r) {
            EnumSet<Rule> e = EnumSet.copyOf(enabled);
            e.add(r);
            return new Set(e);
        }

        public Set minus(Rule r) {
            EnumSet<Rule> e = EnumSet.copyOf(enabled);
            e.remove(r);
            return new Set(e);
        }

        public boolean has(Rule r) {
            return enabled.contains(r);
        }

        public Object canonicalise(String column, Object v) {
            Object out = v;
            for (Rule r : enabled) {
                if (r.governs(column)) {
                    out = r.apply(out);
                }
            }
            return out;
        }

        public String label() {
            if (enabled.isEmpty()) {
                return "(none)";
            }
            return String.join("+", enabled.stream().map(Rule::id).toList());
        }
    }
}
