package dev.migver;

/**
 * The classes of silent corruption this verifier knows how to look for.
 *
 * <p>The taxonomy is the actual content of the project. Comparing row counts is
 * something anyone can write in an afternoon; knowing that SQLite's {@code
 * NOCASE} collation folds only the 26 ASCII letters, and that this turns a
 * case-insensitive unique index into a source of duplicate rows the moment a
 * customer is called Äpfel, is the part that takes having been burned.
 *
 * <p>Each hazard names a specific mechanism, not a symptom. "Data looks wrong"
 * is not a hazard; "the target's declared DECIMAL scale is not enforced, so
 * values keep more precision than the source allowed and sums diverge by
 * fractions of a cent" is.
 *
 * <p>Every one of these was measured against real H2 and SQLite connections
 * before it was written down. None are hypothetical.
 */
public enum Hazard {

    /**
     * The target's case-insensitive collation folds a smaller alphabet than the
     * source's.
     *
     * <p>SQLite's {@code NOCASE} folds exactly {@code A-Z}. 'Äpfel' and 'äpfel'
     * are distinct under it and identical under SQL Server's
     * {@code Latin1_General_CI_AS}. A case-insensitive unique index therefore
     * accepts a pair of rows on the target that the source considered a
     * duplicate-key violation -- so the corruption is not a changed value, it is
     * an <em>extra row</em>, and row counts are the check most likely to be run
     * and the one that will notice it least.
     */
    COLLATION_FOLDS_LESS,

    /**
     * The declared numeric scale is not enforced, so values retain precision the
     * source rounded away.
     *
     * <p>H2's {@code DECIMAL(10,2)} stores 2.345 as 2.35. SQLite's stores it as
     * 2.345, because SQLite's column types are advisory. Every individual row
     * looks approximately right. Only the aggregate diverges, by an amount that
     * grows with row count and that finance will find before engineering does.
     */
    SCALE_NOT_ENFORCED,

    /**
     * Exact decimal becomes binary floating point.
     *
     * <p>A DECIMAL column read back from SQLite arrives as a {@code Double},
     * from H2 as a {@code BigDecimal}. 0.1 + 0.2 is 0.3 in one and
     * 0.30000000000000004 in the other. This is the hazard everyone has heard of
     * and still ships, because it is invisible at single-row granularity.
     */
    DECIMAL_TO_BINARY_FLOAT,

    /**
     * Type affinity coerces a string into a number and discards the difference.
     *
     * <p>Insert '007' into a SQLite INTEGER column and it is stored as the
     * integer 7. The leading zeros are gone and no error was raised. If that
     * column held account numbers, sort codes, or anything else where the
     * rendering is part of the identity, the data is destroyed silently and
     * irreversibly on write.
     */
    AFFINITY_COERCION,

    /**
     * Fixed-width character semantics differ: one engine pads, the other does
     * not.
     *
     * <p>H2 stores 'ab' in a {@code CHAR(10)} as 'ab' followed by eight spaces;
     * SQLite stores 'ab'. Equality comparisons, hash joins, and checksums all
     * disagree afterwards, and the disagreement is a rendering difference rather
     * than corruption -- which makes it the single largest source of false
     * positives in a naive verifier.
     */
    CHAR_PADDING,

    /**
     * The same text in two Unicode normalisation forms.
     *
     * <p>'café' can be four code points (NFC, é as U+00E9) or five (NFD, e
     * followed by U+0301). They render identically in every terminal and every
     * browser. They are different rows, different checksums, and different index
     * entries. A source system fed by macOS filenames will contain both.
     */
    UNICODE_NORMALISATION,

    /**
     * Booleans have no portable representation.
     *
     * <p>H2 returns TRUE/FALSE, SQLite returns the integers 1/0. Both are
     * correct. A string checksum over the two disagrees on every row of the
     * table, which is loud enough to be caught -- and that is the problem, because
     * the noise it generates is what teaches people to relax their comparison
     * until it stops catching things.
     */
    BOOLEAN_REPRESENTATION,

    /**
     * Timestamps lose their type and become text.
     *
     * <p>SQLite has no date type; a TIMESTAMP column holds whatever string was
     * inserted. No normalisation, no validation, no zone. '2024-03-31 02:30:00'
     * survives verbatim -- including when that local time does not exist, because
     * British Summer Time began at 01:00 and the clocks jumped to 02:00. The
     * source rejected it or shifted it; the target kept it.
     */
    TIMESTAMP_LOSES_TYPE,

    /**
     * Empty string and NULL are conflated by one engine and not the other.
     *
     * <p>Oracle is the famous offender. H2 and SQLite both keep them distinct,
     * which is why this hazard is in the taxonomy with a verifier that can detect
     * it rather than a demonstration that it occurs here: the check has to exist
     * before the migration that needs it, not after.
     */
    NULL_VS_EMPTY,

    /**
     * Trailing whitespace is significant in one engine and trimmed by the other.
     *
     * <p>Distinct from {@link #CHAR_PADDING}: that one is about a fixed-width
     * type's storage, this one is about whether 'ab ' and 'ab' are the same
     * value. When they are the same on the source and different on the target,
     * two source rows become two target rows that should have been one.
     */
    TRAILING_WHITESPACE,

    /**
     * An integer at the edge of the type's range.
     *
     * <p>Included because it is the hazard everyone tests for and it is, in this
     * engine pair, not a hazard at all -- both handle the full signed 64-bit
     * range. A taxonomy that only contains real dangers teaches nothing about
     * which dangers are real. This one is here to be measured and dismissed.
     */
    INTEGER_BOUNDARY;

    /** Whether corruption of this class destroys information irrecoverably. */
    public boolean isIrreversible() {
        return switch (this) {
            case AFFINITY_COERCION, SCALE_NOT_ENFORCED, DECIMAL_TO_BINARY_FLOAT,
                 COLLATION_FOLDS_LESS, TRAILING_WHITESPACE, NULL_VS_EMPTY -> true;
            case CHAR_PADDING, BOOLEAN_REPRESENTATION, TIMESTAMP_LOSES_TYPE,
                 UNICODE_NORMALISATION, INTEGER_BOUNDARY -> false;
        };
    }

    /**
     * A one-line statement of the mechanism, for the report. Deliberately about
     * the cause rather than the symptom.
     */
    public String mechanism() {
        return switch (this) {
            case COLLATION_FOLDS_LESS -> "target collation folds a smaller alphabet than the source's";
            case SCALE_NOT_ENFORCED -> "declared numeric scale is advisory, not enforced";
            case DECIMAL_TO_BINARY_FLOAT -> "exact decimal is stored as binary floating point";
            case AFFINITY_COERCION -> "type affinity rewrites a string as a number";
            case CHAR_PADDING -> "fixed-width columns pad on one engine only";
            case UNICODE_NORMALISATION -> "the same grapheme has two code point sequences";
            case BOOLEAN_REPRESENTATION -> "booleans render as TRUE/FALSE or as 1/0";
            case TIMESTAMP_LOSES_TYPE -> "the target has no date type; timestamps are text";
            case NULL_VS_EMPTY -> "one engine conflates the empty string with NULL";
            case TRAILING_WHITESPACE -> "trailing spaces are significant on one side only";
            case INTEGER_BOUNDARY -> "an integer at the limit of the type's range";
        };
    }
}
