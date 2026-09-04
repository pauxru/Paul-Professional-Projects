package dev.migver;

import org.junit.jupiter.api.*;

import java.math.BigDecimal;
import java.util.*;

import static org.junit.jupiter.api.Assertions.*;

/** The taxonomy, the corpus, and the ground truth the measurements rest on. */
class CorpusTest {

    private static final List<Row> ROWS = Corpus.standard();

    @Test
    void everyHazardHasAtLeastOneRowExercisingIt() {
        Map<Hazard, List<Row>> byHazard = Corpus.byHazard(ROWS);
        for (Hazard h : Hazard.values()) {
            assertTrue(byHazard.containsKey(h),
                    h + " is in the taxonomy but nothing in the corpus exercises it, so the "
                            + "claim that it was measured is unsupported");
        }
    }

    @Test
    void everyHazardNamesAMechanismRatherThanASymptom() {
        for (Hazard h : Hazard.values()) {
            assertTrue(h.mechanism().length() > 15, h + " has no mechanism description");
            assertFalse(h.mechanism().toLowerCase(Locale.ROOT).contains("data loss"),
                    h + " describes a symptom, not a mechanism");
        }
    }

    @Test
    void idsAreUniqueAndContiguous() {
        List<Long> ids = ROWS.stream().map(Row::id).sorted().toList();
        assertEquals(ids.size(), new HashSet<>(ids).size(), "duplicate ids");
        for (int i = 0; i < ids.size(); i++) {
            assertEquals(i + 1L, ids.get(i), "ids must be 1..n so batch arithmetic is readable");
        }
    }

    @Test
    void thereAreBenignRowsToProvideADenominator() {
        long benign = ROWS.stream().filter(r -> r.hazard() == null).count();
        assertTrue(benign >= 5, "a corpus of nothing but hazards cannot measure a false positive");
    }

    @Test
    void someRowsAreLabelledCorrupting() {
        assertFalse(Corpus.corrupted(ROWS).isEmpty(),
                "with no corrupting rows every recall figure would be vacuously 1.0");
    }

    @Test
    void mostHazardRowsAreNotCorrupting() {
        long hazardRows = ROWS.stream().filter(r -> r.hazard() != null).count();
        long corrupting = Corpus.corrupted(ROWS).size();
        assertTrue(corrupting * 2 < hazardRows,
                "the point of the taxonomy is that most divergences are harmless; got "
                        + corrupting + " of " + hazardRows);
    }

    @Test
    void atLeastOneHazardIsSplitBetweenCorruptingAndHarmless() {
        boolean split = Corpus.byHazard(ROWS).values().stream().anyMatch(rs ->
                rs.stream().anyMatch(Row::corrupting) && rs.stream().anyMatch(r -> !r.corrupting()));
        assertTrue(split, "if every hazard were uniform, per-row labelling would be pointless");
    }

    @Test
    void theDecimalHazardIsSplitByMagnitudeNotByType() {
        List<Row> rs = Corpus.byHazard(ROWS).get(Hazard.DECIMAL_TO_BINARY_FLOAT);
        Row worst = rs.stream().max(Comparator.comparing(r -> r.amount().abs())).orElseThrow();
        Row least = rs.stream().min(Comparator.comparing(r -> r.amount().abs())).orElseThrow();
        assertTrue(worst.corrupting(), "the large amount must be the corrupting one");
        assertFalse(least.corrupting(), "the small amount must be recoverable");
    }

    @Test
    void theCollationHazardHasBothANonAsciiPairAndAnAsciiControl() {
        List<Row> rs = Corpus.byHazard(ROWS).get(Hazard.COLLATION_FOLDS_LESS);
        assertEquals(4, rs.size());
        assertTrue(rs.stream().anyMatch(r -> r.name().chars().anyMatch(c -> c > 127)),
                "without a non-ASCII pair the hazard cannot fire");
        assertTrue(rs.stream().anyMatch(r -> r.name().chars().allMatch(c -> c < 128)),
                "without an ASCII control there is nothing to show the hazard is alphabet-dependent");
    }

    @Test
    void theAffinityHazardHasALeadingZeroCaseAndAPlainCase() {
        List<Row> rs = Corpus.byHazard(ROWS).get(Hazard.AFFINITY_COERCION);
        assertTrue(rs.stream().anyMatch(r -> r.account().startsWith("0")));
        assertTrue(rs.stream().anyMatch(r -> !r.account().startsWith("0")));
    }

    @Test
    void theIntegerBoundaryRowsSitOnTheSourcesLimitNotTheTargets() {
        List<Row> rs = Corpus.byHazard(ROWS).get(Hazard.INTEGER_BOUNDARY);
        for (Row r : rs) {
            long v = Long.parseLong(r.account());
            assertTrue(v == Integer.MAX_VALUE || v == Integer.MIN_VALUE,
                    "a wider value throws on insert, which disqualifies it as a silent hazard");
        }
    }

    @Test
    void theCorpusContainsATimeThatDoesNotExistInEveryZone() {
        assertTrue(ROWS.stream().anyMatch(r -> "2024-03-31 02:30:00".equals(r.seen())));
    }

    @Test
    void theCorpusContainsBothNullAndEmptyNames() {
        assertTrue(ROWS.stream().anyMatch(r -> r.name() == null));
        assertTrue(ROWS.stream().anyMatch(r -> "".equals(r.name())));
    }

    @Test
    void everyRowSurvivesInsertionIntoBothEngines() {
        try (Engine s = Engine.open(Engine.Kind.H2, "corpA");
             Engine t = Engine.open(Engine.Kind.SQLITE, "corpB")) {
            s.createTable("t");
            t.createTable("t");
            for (Row r : ROWS) {
                s.insert("t", r);
                t.insert("t", r);
            }
            assertEquals(ROWS.size(), s.count("t"));
            assertEquals(ROWS.size(), t.count("t"),
                    "a row the source rejects is a loud failure and does not belong here");
        }
    }

    @Test
    void benignRowsAreActuallyBenign() {
        Row r = Row.benign(1, "x");
        assertNull(r.hazard());
        assertFalse(r.corrupting());
    }

    @Test
    void withHazardPreservesEveryOtherField() {
        Row a = Row.benign(1, "x");
        Row b = a.withHazard(Hazard.CHAR_PADDING, true);
        assertEquals(a.name(), b.name());
        assertEquals(a.amount(), b.amount());
        assertEquals(Hazard.CHAR_PADDING, b.hazard());
        assertTrue(b.corrupting());
    }

    @Test
    void byHazardExcludesRowsWithNoHazard() {
        Map<Hazard, List<Row>> m = Corpus.byHazard(List.of(Row.benign(1, "x")));
        assertTrue(m.isEmpty());
    }

    @Test
    void irreversibilityIsDeclaredForEveryHazard() {
        long irreversible = Arrays.stream(Hazard.values()).filter(Hazard::isIrreversible).count();
        assertTrue(irreversible > 0 && irreversible < Hazard.values().length,
                "a taxonomy where every entry has the same flag is not classifying anything");
    }

    @Test
    void theCorpusIsImmutable() {
        assertThrows(UnsupportedOperationException.class,
                () -> Corpus.standard().add(Row.benign(999, "x")));
    }

    @Test
    void amountsAreAllAtTheDeclaredScaleOrBeyondIt() {
        for (Row r : ROWS) {
            assertNotNull(r.amount(), "a null amount would silently skip the numeric comparison");
            assertTrue(r.amount().scale() <= 4 || r.hazard() == Hazard.SCALE_NOT_ENFORCED
                            || r.hazard() == Hazard.DECIMAL_TO_BINARY_FLOAT,
                    "row " + r.id() + " has scale " + r.amount().scale() + " for no stated reason");
        }
    }

    @Test
    void theHugeAmountReallyDoesExceedDoublePrecision() {
        BigDecimal huge = ROWS.stream().map(Row::amount)
                .max(Comparator.naturalOrder()).orElseThrow();
        assertNotEquals(0, huge.compareTo(new BigDecimal(Double.toString(huge.doubleValue()))),
                "the ground-truth label for this row depends on the value not fitting in a double");
    }
}
