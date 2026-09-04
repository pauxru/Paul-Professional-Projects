package dev.migver;

import org.junit.jupiter.api.*;

import java.math.BigDecimal;
import java.util.*;

import static org.junit.jupiter.api.Assertions.*;

/** The migrators, the comparison strategies, and the confusion arithmetic. */
class VerifierTest {

    private static final List<Row> ROWS = Corpus.standard();

    @BeforeAll
    static void pinZone() {
        TimeZone.setDefault(TimeZone.getTimeZone("UTC"));
    }

    // ---------------------------------------------------------- migrators

    @Test
    void everyMigratorHasARationaleThatIsNotAnAdmissionOfGuilt() {
        for (Migrator m : Migrator.all()) {
            assertTrue(m.rationale().length() > 15, m.name() + " has no rationale");
            assertFalse(m.rationale().toLowerCase(Locale.ROOT).contains("bug"),
                    m.name() + " reads as a mistake; real ones read as good ideas");
        }
    }

    @Test
    void onlyOneMigratorIsFaithful() {
        assertEquals(1, Migrator.all().stream().filter(Migrator::isFaithful).count());
        assertEquals(Migrator.all().size() - 1, Migrator.defective().size());
    }

    @Test
    void theFaithfulMigratorDamagesNothing() {
        Migrator f = new Migrator.Faithful();
        for (Row r : ROWS) {
            assertFalse(f.damages(r), "row " + r.id());
        }
    }

    @Test
    void everyDefectiveMigratorDamagesAtLeastOneRow() {
        for (Migrator m : Migrator.defective()) {
            assertTrue(ROWS.stream().anyMatch(m::damages),
                    m.name() + " damages nothing in this corpus, so it proves nothing");
        }
    }

    @Test
    void trimmingDamagesExactlyTheTrailingSpaceRow() {
        Migrator m = new Migrator.Trimming();
        List<Long> hit = ROWS.stream().filter(m::damages).map(Row::id).toList();
        assertEquals(1, hit.size(), "expected one row, got " + hit);
        Row r = ROWS.stream().filter(x -> x.id() == hit.get(0)).findFirst().orElseThrow();
        assertEquals(Hazard.TRAILING_WHITESPACE, r.hazard());
    }

    @Test
    void normalisingDamagesOnlyTheDecomposedSpelling() {
        Migrator m = new Migrator.Normalising();
        for (Row r : ROWS) {
            if (m.damages(r)) {
                assertEquals(Hazard.UNICODE_NORMALISATION, r.hazard(), "row " + r.id());
            }
        }
    }

    @Test
    void roundingDamagesOnlyValuesWithMoreThanTwoDecimals() {
        Migrator m = new Migrator.Rounding();
        for (Row r : ROWS) {
            assertEquals(r.amount().scale() > 2 && r.amount().stripTrailingZeros().scale() > 2,
                    m.damages(r), "row " + r.id() + " amount " + r.amount());
        }
    }

    @Test
    void numericAccountDamagesOnlyLeadingZeroAccounts() {
        Migrator m = new Migrator.NumericAccount();
        for (Row r : ROWS) {
            boolean expected = r.account() != null && r.account().length() > 1
                    && r.account().startsWith("0");
            assertEquals(expected, m.damages(r), "row " + r.id() + " account " + r.account());
        }
    }

    @Test
    void uppercasingDamagesEveryRowWithALowercaseLetter() {
        Migrator m = new Migrator.Uppercasing();
        long hit = ROWS.stream().filter(m::damages).count();
        assertTrue(hit > 20, "expected broad damage, got " + hit);
    }

    @Test
    void damagesIsDecidedFromTheMigratorNotFromObservedOutput() {
        // If damages() were computed by running the migration, the confusion
        // matrices would be measuring the verifier against itself.
        Migrator m = new Migrator.Trimming();
        Row synthetic = new Row(999, "padded ", "C", "1", BigDecimal.ONE, true,
                "2024-01-01 00:00:00", null, false);
        assertTrue(m.damages(synthetic), "must work on a row that was never inserted anywhere");
    }

    // ------------------------------------------------------- comparisons

    @Test
    void rowCountSeesNothingWhenNoRowsAreMissing() {
        try (Migration m = Migration.run(new Migrator.Faithful(), ROWS)) {
            assertTrue(new Comparison.RowCount().mismatches(m.sourceRows(), m.targetRows()).isEmpty());
        }
    }

    @Test
    void rowCountImplicatesEverythingWhenCountsDiffer() {
        try (Migration m = Migration.run(new Migrator.Faithful(), ROWS)) {
            List<Map<String, Object>> shorter = m.targetRows().subList(0, 5);
            assertEquals(ROWS.size(),
                    new Comparison.RowCount().mismatches(m.sourceRows(), shorter).size(),
                    "a count has no way to name a row, so it names all of them");
        }
    }

    @Test
    void naiveChecksumFlagsAlmostEveryRowOfAFaithfulMigration() {
        try (Migration m = Migration.run(new Migrator.Faithful(), ROWS)) {
            Confusion c = m.evaluate(new Comparison.NaiveChecksum());
            assertTrue(c.precision() < 0.15, "precision was " + c.precision());
            assertEquals(1.0, c.recall(), "and yet it misses nothing");
        }
    }

    @Test
    void theFullRuleSetHasPerfectPrecisionOnAFaithfulCopy() {
        try (Migration m = Migration.run(new Migrator.Faithful(), ROWS)) {
            assertEquals(0, m.evaluate(new Comparison.Canonicalising(Rule.Set.all())).fp());
        }
    }

    @Test
    void noRuleSetIsCleanWithoutAllFourReconcilingRules() {
        Rule[] required = {Rule.NUMERIC, Rule.BOOLEAN, Rule.TEMPORAL, Rule.IDENTIFIER};
        try (Migration m = Migration.run(new Migrator.Faithful(), ROWS)) {
            for (Rule r : required) {
                Confusion c = m.evaluate(new Comparison.Canonicalising(Rule.Set.all().minus(r)));
                assertTrue(c.fp() > 20,
                        "removing " + r.id() + " should return the verifier to total noise, got fp="
                                + c.fp());
            }
        }
    }

    @Test
    void theThreeValueCollapsingRulesBuyNothingOnACleanMigration() {
        Rule.Set minimal = Rule.Set.of(Rule.NUMERIC, Rule.BOOLEAN, Rule.TEMPORAL, Rule.IDENTIFIER);
        try (Migration m = Migration.run(new Migrator.Faithful(), ROWS)) {
            Confusion a = m.evaluate(new Comparison.Canonicalising(minimal));
            Confusion b = m.evaluate(new Comparison.Canonicalising(Rule.Set.all()));
            assertEquals(a.fp(), b.fp(), "trim, nfc and casefold suppress nothing that is left");
            assertEquals(a.tp(), b.tp());
        }
    }

    @Test
    void aMissingTargetRowIsReportedRatherThanSkipped() {
        try (Migration m = Migration.run(new Migrator.Faithful(), ROWS)) {
            List<Map<String, Object>> partial = new ArrayList<>(m.targetRows());
            partial.remove(0);
            Set<Long> bad = new Comparison.Canonicalising(Rule.Set.all())
                    .mismatches(m.sourceRows(), partial);
            assertTrue(bad.contains(1L), "an absent row must not be silently treated as matching");
        }
    }

    @Test
    void byColumnAttributesDifferencesToTheRightColumn() {
        try (Migration m = Migration.run(new Migrator.Faithful(), ROWS)) {
            Map<String, Integer> raw = new Comparison.Canonicalising(Rule.Set.none())
                    .byColumn(m.sourceRows(), m.targetRows());
            assertEquals(0, raw.get("name"), "names survive a faithful copy untouched");
            assertEquals(ROWS.size(), raw.get("active"), "booleans differ on every row");
            assertEquals(ROWS.size(), raw.get("seen"), "timestamps differ on every row");
        }
    }

    @Test
    void comparisonNamesAreDistinctAndDescribeTheirRules() {
        assertNotEquals(new Comparison.Canonicalising(Rule.Set.none()).name(),
                new Comparison.Canonicalising(Rule.Set.all()).name());
        assertTrue(new Comparison.Canonicalising(Rule.Set.of(Rule.TRIM)).name().contains("trim"));
    }

    // --------------------------------------------------------- confusion

    @Test
    void confusionPartitionsTheUniverse() {
        Set<Long> universe = Set.of(1L, 2L, 3L, 4L);
        Confusion c = Confusion.of(Set.of(1L, 2L), Set.of(2L, 3L), universe);
        assertEquals(Set.of(2L), c.truePositives());
        assertEquals(Set.of(1L), c.falsePositives());
        assertEquals(Set.of(3L), c.falseNegatives());
        assertEquals(Set.of(4L), c.trueNegatives());
    }

    @Test
    void precisionAndRecallAreOneWhenThereIsNothingToJudge() {
        Confusion c = Confusion.of(Set.of(), Set.of(), Set.of(1L));
        assertEquals(1.0, c.precision());
        assertEquals(1.0, c.recall());
    }

    @Test
    void blockingCorrectlyRequiresATruePositive() {
        Confusion noisy = Confusion.of(Set.of(1L), Set.of(), Set.of(1L, 2L));
        assertTrue(noisy.wouldBlock());
        assertFalse(noisy.blocksCorrectly(), "blocking on a false positive is not safety");
    }

    @Test
    void trulyCorruptedIsTheUnionOfEngineAndMigratorDamage() {
        try (Migration m = Migration.run(new Migrator.Trimming(), ROWS)) {
            Set<Long> engineOnly;
            try (Migration f = Migration.run(new Migrator.Faithful(), ROWS)) {
                engineOnly = f.trulyCorrupted();
            }
            assertTrue(m.trulyCorrupted().containsAll(engineOnly),
                    "a defective migrator cannot un-corrupt what the engines already destroyed");
            assertTrue(m.trulyCorrupted().size() > engineOnly.size());
        }
    }

    @Test
    void theSetLevelCheckSeesWhatNoRowComparisonCan() {
        try (Migration m = Migration.run(new Migrator.Faithful(), ROWS)) {
            long[] dn = m.distinctNames();
            assertNotEquals(dn[0], dn[1], "the collation divergence must be visible here");
            Map<String, Integer> cols = new Comparison.Canonicalising(Rule.Set.all())
                    .byColumn(m.sourceRows(), m.targetRows());
            assertEquals(0, cols.get("name"), "and invisible in every name comparison");
        }
    }

    @Test
    void theSetLevelCheckIsDefeatedByACompensatingDefect() {
        try (Migration m = Migration.run(new Migrator.Trimming(), ROWS)) {
            long[] dn = m.distinctNames();
            assertEquals(dn[0], dn[1],
                    "two unrelated defects cancel and the aggregate reports clean");
            assertFalse(m.trulyCorrupted().isEmpty(), "while the data really is damaged");
        }
    }

    @Test
    void dualWriteAndCopyExposeDifferentColumns() {
        Comparison.Canonicalising c = new Comparison.Canonicalising(Rule.Set.all());
        Map<String, Integer> copy;
        Map<String, Integer> dual;
        try (Migration m = Migration.run(new Migrator.Faithful(), ROWS)) {
            copy = c.byColumn(m.sourceRows(), m.targetRows());
        }
        try (DualWrite d = DualWrite.run(ROWS)) {
            dual = c.byColumn(d.leftRows(), d.rightRows());
        }
        assertEquals(0, copy.get("seen"));
        assertEquals(ROWS.size(), dual.get("seen"),
                "the same rule set works on one path and not the other");
    }
}
