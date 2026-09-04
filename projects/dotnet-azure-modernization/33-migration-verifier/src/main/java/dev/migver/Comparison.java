package dev.migver;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.*;

/**
 * A strategy for deciding whether a migration is correct.
 *
 * <p>The four strategies here are, roughly, the four answers you get if you ask
 * four engineers how to verify a migration. They are ordered by how much work
 * they are, and -- as {@link Experiments} measures -- that ordering has almost
 * nothing to do with how much they detect.
 */
public interface Comparison {

    String name();

    /** Ids the strategy reports as mismatched. Empty means "this migration is clean". */
    java.util.Set<Long> mismatches(List<Map<String, Object>> source, List<Map<String, Object>> target);

    /**
     * "The counts match, so we're good." The strawman, and by a wide margin the
     * most common verification actually performed on a production migration.
     */
    final class RowCount implements Comparison {
        @Override
        public String name() {
            return "row-count";
        }

        @Override
        public java.util.Set<Long> mismatches(List<Map<String, Object>> source, List<Map<String, Object>> target) {
            // A count comparison has no way to name a row, so when it fires it
            // implicates everything and when it does not it exonerates
            // everything. Both halves of that are the problem.
            if (source.size() == target.size()) {
                return java.util.Set.of();
            }
            java.util.Set<Long> all = new TreeSet<>();
            for (Map<String, Object> r : source) {
                all.add(((Number) r.get("id")).longValue());
            }
            return all;
        }
    }

    /**
     * Concatenate every column into a string, hash it, compare the hashes. The
     * standard "checksum the table" approach.
     *
     * <p>Its failure mode is not subtlety, it is the opposite: {@code String.valueOf}
     * on a driver-native value is a rendering, and the two drivers render almost
     * everything differently, so this fires on nearly every row of a perfectly
     * faithful migration. It is not that it misses things. It is that it is so
     * loud that it gets switched off, and the thing it would have caught goes
     * with it.
     */
    final class NaiveChecksum implements Comparison {
        private static final String[] COLS = {"id", "name", "code", "account", "amount", "active", "seen"};

        @Override
        public String name() {
            return "naive-checksum";
        }

        @Override
        public java.util.Set<Long> mismatches(List<Map<String, Object>> source, List<Map<String, Object>> target) {
            Map<Long, String> t = new HashMap<>();
            for (Map<String, Object> r : target) {
                t.put(((Number) r.get("id")).longValue(), digest(r));
            }
            java.util.Set<Long> bad = new TreeSet<>();
            for (Map<String, Object> r : source) {
                long id = ((Number) r.get("id")).longValue();
                if (!digest(r).equals(t.get(id))) {
                    bad.add(id);
                }
            }
            return bad;
        }

        private static String digest(Map<String, Object> row) {
            StringBuilder sb = new StringBuilder();
            for (String c : COLS) {
                sb.append(String.valueOf(row.get(c))).append('\u001f');
            }
            try {
                MessageDigest md = MessageDigest.getInstance("MD5");
                byte[] h = md.digest(sb.toString().getBytes(StandardCharsets.UTF_8));
                return HexFormat.of().formatHex(h);
            } catch (NoSuchAlgorithmException e) {
                throw new IllegalStateException(e);
            }
        }
    }

    /**
     * Compare column by column with a configurable {@link Rule.Set}, reporting
     * which columns differ rather than only that the row differs.
     *
     * <p>This is the honest strategy, and the whole argument of the project is
     * about how to configure it. With no rules it behaves much like the naive
     * checksum. With every rule it is quiet, agreeable and nearly blind. The
     * useful configurations are in between, and which ones they are is an
     * empirical question rather than a matter of taste.
     */
    final class Canonicalising implements Comparison {
        private static final String[] COLS = {"name", "code", "account", "amount", "active", "seen"};

        private final Rule.Set rules;

        public Canonicalising(Rule.Set rules) {
            this.rules = rules;
        }

        public Rule.Set rules() {
            return rules;
        }

        @Override
        public String name() {
            return "canonicalising[" + rules.label() + "]";
        }

        @Override
        public java.util.Set<Long> mismatches(List<Map<String, Object>> source, List<Map<String, Object>> target) {
            Map<Long, Map<String, Object>> t = new HashMap<>();
            for (Map<String, Object> r : target) {
                t.put(((Number) r.get("id")).longValue(), r);
            }
            java.util.Set<Long> bad = new TreeSet<>();
            for (Map<String, Object> s : source) {
                long id = ((Number) s.get("id")).longValue();
                Map<String, Object> other = t.get(id);
                if (other == null) {
                    bad.add(id);
                    continue;
                }
                for (String c : COLS) {
                    if (!Objects.equals(rules.canonicalise(c, s.get(c)), rules.canonicalise(c, other.get(c)))) {
                        bad.add(id);
                        break;
                    }
                }
            }
            return bad;
        }

        /** Which columns differ, for reporting rather than for the verdict. */
        public Map<String, Integer> byColumn(List<Map<String, Object>> source, List<Map<String, Object>> target) {
            Map<Long, Map<String, Object>> t = new HashMap<>();
            for (Map<String, Object> r : target) {
                t.put(((Number) r.get("id")).longValue(), r);
            }
            Map<String, Integer> counts = new LinkedHashMap<>();
            for (String c : COLS) {
                counts.put(c, 0);
            }
            for (Map<String, Object> s : source) {
                Map<String, Object> other = t.get(((Number) s.get("id")).longValue());
                if (other == null) {
                    continue;
                }
                for (String c : COLS) {
                    if (!Objects.equals(rules.canonicalise(c, s.get(c)), rules.canonicalise(c, other.get(c)))) {
                        counts.merge(c, 1, Integer::sum);
                    }
                }
            }
            return counts;
        }
    }
}
