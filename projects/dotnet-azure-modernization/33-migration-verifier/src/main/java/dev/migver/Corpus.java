package dev.migver;

import java.math.BigDecimal;
import java.util.*;

/**
 * The test data.
 *
 * <p>Every row is built to exercise one hazard, and is labelled with whether the
 * <em>engine pair alone</em> destroys information for that row -- that is, under
 * a migrator that copies values with perfect fidelity. Damage caused by a
 * defective {@link Migrator} is a separate question, answered per migrator by
 * {@link Migrator#damages}, because it depends on the migrator and not on the
 * row.
 *
 * <p>The word "destroys" is doing real work and is worth pinning down. It does
 * not mean "the two engines will disagree about this row" -- they disagree about
 * nearly every row, because they render values differently. It means the source
 * value cannot be recovered from the target value: the mapping is not injective.
 *
 * <p>{@code 0.1} arriving as {@code 0.1000} is not corruption; the mapping is
 * reversible and a verifier that flags it is generating noise that someone will
 * eventually silence. {@code '0000007'} arriving as {@code 7} is corruption;
 * {@code '0000007'}, {@code '007'} and {@code '7'} all land on the same target
 * value and nothing downstream can tell them apart again.
 *
 * <p>Getting this backwards in either direction is fatal. Treat rendering
 * differences as corruption and the verifier cries wolf until it is relaxed into
 * uselessness. Treat corruption as a rendering difference and it ships.
 */
public final class Corpus {

    public static final String TABLE = "customer";

    private Corpus() {
    }

    public static List<Row> standard() {
        List<Row> rows = new ArrayList<>();
        long id = 1;

        for (int i = 0; i < 8; i++) {
            rows.add(Row.benign(id++, "customer-" + i));
        }

        // COLLATION_FOLDS_LESS. Not corrupting at the row level: both values
        // survive intact and each is recoverable. What is lost is a *set-level*
        // invariant. The source's IGNORECASE folds these to one name; SQLite's
        // NOCASE folds ASCII only and keeps them apart. A UNIQUE(name) index that
        // held on the source no longer constrains anything on the target, and
        // every row-by-row comparison in this project reports a clean migration.
        //
        // The ASCII pair below is the control: NOCASE folds those, so the same
        // hazard class produces no divergence at all. Which is to say the defect
        // is invisible to any test corpus written by an English-speaking team.
        rows.add(named(id++, "\u00c4pfel GmbH", "DE", Hazard.COLLATION_FOLDS_LESS, false));
        rows.add(named(id++, "\u00e4pfel gmbh", "DE", Hazard.COLLATION_FOLDS_LESS, false));
        rows.add(named(id++, "ACME LTD", "GB", Hazard.COLLATION_FOLDS_LESS, false));
        rows.add(named(id++, "acme ltd", "GB", Hazard.COLLATION_FOLDS_LESS, false));

        // DECIMAL_TO_BINARY_FLOAT. The interesting property is that whether this
        // destroys information depends on magnitude, not on the column type. A
        // double carries about 15-16 significant decimal digits; below that the
        // value round-trips exactly and the difference is pure rendering noise.
        // Above it, digits are gone.
        //
        // So a test corpus of realistic-looking small amounts passes, and the
        // production ledger -- which has a few large settlement rows in it --
        // does not. This is the shape of most migration incidents.
        rows.add(amount(id++, "float-lossy", new BigDecimal("0.1000"), Hazard.DECIMAL_TO_BINARY_FLOAT, false));
        rows.add(amount(id++, "float-exact", new BigDecimal("0.2500"), Hazard.DECIMAL_TO_BINARY_FLOAT, false));
        rows.add(amount(id++, "float-huge", new BigDecimal("99999999999999.1234"),
                Hazard.DECIMAL_TO_BINARY_FLOAT, true));

        // SCALE_NOT_ENFORCED. The source column is DECIMAL(18,4) and enforces it;
        // the target's declared scale is advisory. That asymmetry does not hurt a
        // faithful migrator, which only ever writes values the source already
        // rounded. It hurts the moment anything writes to the target directly --
        // during dual-write, during a backfill retry, during the incident where
        // someone patches a row by hand.
        rows.add(amount(id++, "scale-boundary", new BigDecimal("2.3450"), Hazard.SCALE_NOT_ENFORCED, false));

        // AFFINITY_COERCION. The worst of them, because the damage happens on
        // write: by the time any verifier runs, the target no longer contains the
        // evidence that anything was lost.
        rows.add(account(id++, "leading-zeros", "0000007", Hazard.AFFINITY_COERCION, true));
        rows.add(account(id++, "plain-digits", "7", Hazard.AFFINITY_COERCION, false));

        // CHAR_PADDING. Not corrupting -- trimming recovers the value from either
        // side. This row's job in the corpus is to generate false positives, so
        // that the cost of suppressing them can be measured.
        rows.add(named(id++, "padded-code", "AB", Hazard.CHAR_PADDING, false));

        // UNICODE_NORMALISATION. Not corrupting per row: both spellings survive
        // and each is recoverable. Like the collation hazard it bites at the
        // index level, where one customer has two entries.
        rows.add(named(id++, "caf\u00e9", "PT", Hazard.UNICODE_NORMALISATION, false));
        rows.add(named(id++, "cafe\u0301", "PT", Hazard.UNICODE_NORMALISATION, false));

        // BOOLEAN_REPRESENTATION. TRUE and 1 carry the same information. Here to
        // show how much noise a naive comparison manufactures from a difference
        // that matters to nobody.
        rows.add(new Row(id++, "flag-false", "NL", "70001", new BigDecimal("1.0000"),
                false, "2024-02-07 10:00:00", Hazard.BOOLEAN_REPRESENTATION, false));

        // TIMESTAMP_LOSES_TYPE. 02:30 on 2024-03-31 does not exist in
        // Europe/London. The target keeps it verbatim because the target has no
        // date type with which to object. Not corrupting -- the text is intact --
        // but it means the target will accept times the source would reject, and
        // the first thing to notice will be a report that sums to the wrong day.
        rows.add(new Row(id++, "dst-gap", "GB", "80001", new BigDecimal("4.0000"),
                true, "2024-03-31 02:30:00", Hazard.TIMESTAMP_LOSES_TYPE, false));

        // NULL_VS_EMPTY. Measured and dismissed: both engines keep them distinct.
        // Kept in the corpus as an honest negative -- a hazard that is real in
        // other engine pairs and simply does not occur in this one.
        rows.add(new Row(id++, "", "NU", "90001", new BigDecimal("5.0000"),
                true, "2024-02-08 10:00:00", Hazard.NULL_VS_EMPTY, false));
        rows.add(new Row(id++, null, "NU", "90002", new BigDecimal("5.0000"),
                true, "2024-02-08 10:00:00", Hazard.NULL_VS_EMPTY, false));

        // TRAILING_WHITESPACE. Both engines preserve it, so under a faithful
        // migrator nothing happens. It is here because a *migrator* that trims is
        // extremely common, and this is the row it destroys.
        rows.add(named(id++, "Smith", "TW", Hazard.TRAILING_WHITESPACE, false));
        rows.add(named(id++, "Smith ", "TW", Hazard.TRAILING_WHITESPACE, false));

        // INTEGER_BOUNDARY. Measured, and the measurement changed the design.
        // H2's INTEGER is 32-bit; SQLite's INTEGER is up to 64-bit. The same type
        // name means different widths in the two engines, which is a real hazard
        // and one that runs the *safe* direction here: the target is wider than
        // the source, so nothing overflows on the way across.
        //
        // Attempting the unsafe direction is instructive. Putting 2^63-1 into the
        // source throws "Data conversion error" immediately -- a hazard that
        // announces itself is not a hazard, it is a bug report, and it will be
        // fixed on the day it appears. Everything else in this corpus is
        // dangerous precisely because it stays quiet. The boundary rows below sit
        // exactly on the source's limit and cross without complaint.
        rows.add(account(id++, "int-max", "2147483647", Hazard.INTEGER_BOUNDARY, false));
        rows.add(account(id, "int-min", "-2147483648", Hazard.INTEGER_BOUNDARY, false));

        return List.copyOf(rows);
    }

    private static Row named(long id, String name, String code, Hazard h, boolean corrupting) {
        return new Row(id, name, code, "1" + String.format("%06d", id), new BigDecimal("55.0000"),
                true, "2024-02-01 10:00:00", h, corrupting);
    }

    private static Row amount(long id, String name, BigDecimal amount, Hazard h, boolean corrupting) {
        return new Row(id, name, "AM", "2" + String.format("%06d", id), amount,
                true, "2024-02-02 10:00:00", h, corrupting);
    }

    private static Row account(long id, String name, String account, Hazard h, boolean corrupting) {
        return new Row(id, name, "AC", account, new BigDecimal("12.0000"),
                true, "2024-02-04 10:00:00", h, corrupting);
    }

    /** Rows the engine pair alone destroys. The denominator for engine-level recall. */
    public static List<Row> corrupted(List<Row> rows) {
        return rows.stream().filter(Row::corrupting).toList();
    }

    public static Map<Hazard, List<Row>> byHazard(List<Row> rows) {
        Map<Hazard, List<Row>> out = new EnumMap<>(Hazard.class);
        for (Row r : rows) {
            if (r.hazard() != null) {
                out.computeIfAbsent(r.hazard(), k -> new ArrayList<>()).add(r);
            }
        }
        return out;
    }
}
