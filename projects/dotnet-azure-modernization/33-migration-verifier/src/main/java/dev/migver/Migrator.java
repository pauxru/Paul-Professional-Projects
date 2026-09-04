package dev.migver;

import java.math.BigDecimal;
import java.math.RoundingMode;
import java.text.Normalizer;
import java.util.*;
import java.util.function.UnaryOperator;

/**
 * A migration step: reads a row out of the source and decides what to write to
 * the target.
 *
 * <p>Real migrators are not identity functions. They are written under deadline
 * by someone who has just spent a week fighting encoding problems, and they
 * accumulate small helpful normalisations -- trim the strings, coerce the flags,
 * round the money -- each of which looked reasonable on the day. Every defective
 * migrator here is one I have seen in production code, and each destroys
 * information in a way that a row count and a checksum will not notice.
 *
 * <p>Each variant declares, by construction, which corpus rows it damages. That
 * declaration is the ground truth the confusion matrices are computed against;
 * it is derived from the migrator's own logic and not from any verifier's
 * opinion, which is what keeps the measurement from being circular.
 */
public abstract class Migrator {

    private final String name;
    private final String rationale;

    protected Migrator(String name, String rationale) {
        this.name = name;
        this.rationale = rationale;
    }

    public String name() {
        return name;
    }

    /** The reason someone wrote it this way. Never "to break things". */
    public String rationale() {
        return rationale;
    }

    /** Transform one source row on its way to the target. */
    public abstract Map<String, Object> apply(Map<String, Object> source);

    /**
     * Whether this migrator destroys information for this corpus row, decided
     * from the migrator's own definition rather than from observed output.
     */
    public abstract boolean damages(Row row);

    public boolean isFaithful() {
        return false;
    }

    public static List<Migrator> all() {
        return List.of(new Faithful(), new Trimming(), new Normalising(),
                new Rounding(), new NumericAccount(), new Uppercasing());
    }

    public static List<Migrator> defective() {
        return all().stream().filter(m -> !m.isFaithful()).toList();
    }

    private static Map<String, Object> mapString(Map<String, Object> src, String col, UnaryOperator<String> f) {
        Map<String, Object> out = new LinkedHashMap<>(src);
        Object v = src.get(col);
        if (v instanceof String s) {
            out.put(col, f.apply(s));
        }
        return out;
    }

    /** Copies every value through untouched. The control. */
    public static final class Faithful extends Migrator {
        public Faithful() {
            super("faithful", "Copies every value through untouched.");
        }

        @Override
        public Map<String, Object> apply(Map<String, Object> source) {
            return source;
        }

        @Override
        public boolean damages(Row row) {
            return false;
        }

        @Override
        public boolean isFaithful() {
            return true;
        }
    }

    /**
     * Trims whitespace. Written to stop the CHAR(10) padding from producing
     * millions of spurious diffs -- which it does, and the fix works. It also
     * merges 'Smith' and 'Smith ' into one customer.
     */
    public static final class Trimming extends Migrator {
        public Trimming() {
            super("trimming", "Strips whitespace so CHAR padding stops producing diffs.");
        }

        @Override
        public Map<String, Object> apply(Map<String, Object> source) {
            Map<String, Object> out = mapString(source, "name", String::trim);
            return mapString(out, "code", String::trim);
        }

        @Override
        public boolean damages(Row row) {
            return row.name() != null && !row.name().equals(row.name().trim());
        }
    }

    /**
     * NFC-normalises text. Written because the target's search index was
     * returning nothing for accented names, which was true and which this fixed.
     * It also merges the two spellings of a name into one, which is exactly what
     * makes the search work and exactly what loses a customer record.
     */
    public static final class Normalising extends Migrator {
        public Normalising() {
            super("normalising", "NFC-normalises text so accented names are searchable.");
        }

        @Override
        public Map<String, Object> apply(Map<String, Object> source) {
            return mapString(source, "name", s -> Normalizer.normalize(s, Normalizer.Form.NFC));
        }

        @Override
        public boolean damages(Row row) {
            return row.name() != null
                    && !row.name().equals(Normalizer.normalize(row.name(), Normalizer.Form.NFC));
        }
    }

    /**
     * Rounds money to two decimal places, because the reports are in pence and
     * the four-decimal values were rendering as 12.3400 in the UI.
     */
    public static final class Rounding extends Migrator {
        public Rounding() {
            super("rounding", "Rounds money to 2dp so reports render in pence.");
        }

        @Override
        public Map<String, Object> apply(Map<String, Object> source) {
            Map<String, Object> out = new LinkedHashMap<>(source);
            if (source.get("amount") instanceof BigDecimal d) {
                out.put("amount", d.setScale(2, RoundingMode.HALF_UP));
            }
            return out;
        }

        @Override
        public boolean damages(Row row) {
            return row.amount() != null
                    && row.amount().compareTo(row.amount().setScale(2, RoundingMode.HALF_UP)) != 0;
        }
    }

    /**
     * Parses the account column as a number, because the target column is
     * INTEGER and the driver was complaining. It stopped complaining.
     */
    public static final class NumericAccount extends Migrator {
        public NumericAccount() {
            super("numeric-account", "Parses account as a long to match the target's INTEGER column.");
        }

        @Override
        public Map<String, Object> apply(Map<String, Object> source) {
            Map<String, Object> out = new LinkedHashMap<>(source);
            Object v = source.get("account");
            if (v != null) {
                try {
                    out.put("account", Long.parseLong(v.toString().trim()));
                } catch (NumberFormatException e) {
                    out.put("account", null);
                }
            }
            return out;
        }

        @Override
        public boolean damages(Row row) {
            if (row.account() == null) {
                return false;
            }
            try {
                return !Long.toString(Long.parseLong(row.account().trim())).equals(row.account());
            } catch (NumberFormatException e) {
                return true;
            }
        }
    }

    /**
     * Upper-cases names so that the target's case-insensitive lookups behave
     * consistently. They do. The customer's name is now shouting.
     */
    public static final class Uppercasing extends Migrator {
        public Uppercasing() {
            super("uppercasing", "Upper-cases names for consistent case-insensitive lookup.");
        }

        @Override
        public Map<String, Object> apply(Map<String, Object> source) {
            return mapString(source, "name", s -> s.toUpperCase(Locale.ROOT));
        }

        @Override
        public boolean damages(Row row) {
            return row.name() != null && !row.name().equals(row.name().toUpperCase(Locale.ROOT));
        }
    }
}
