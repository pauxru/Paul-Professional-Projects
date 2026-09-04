package dev.migver;

import org.junit.jupiter.api.*;

import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;

import static org.junit.jupiter.api.Assertions.*;

/** The backfill, the cutover gate, and the report itself. */
class OperationsTest {

    private static final List<Row> ROWS = Corpus.standard();

    @BeforeAll
    static void pinZone() {
        TimeZone.setDefault(TimeZone.getTimeZone("UTC"));
    }

    private static List<Backfill.Write> writes() {
        return List.of(new Backfill.Write(2, new BigDecimal("999.0000"), 0),
                new Backfill.Write(3, new BigDecimal("888.0000"), 1),
                new Backfill.Write(15, new BigDecimal("777.0000"), 2),
                new Backfill.Write(27, new BigDecimal("666.0000"), 5));
    }

    // ---------------------------------------------------------- backfill

    @Test
    void aBackfillWithNoConcurrentWritesIsClean() {
        try (Backfill b = Backfill.start(ROWS, 5)) {
            assertTrue(b.run(List.of(), false).isEmpty());
        }
    }

    @Test
    void theWatermarkLeavesRowsStaleWhenWritesLandBehindIt() {
        try (Backfill b = Backfill.start(ROWS, 5)) {
            assertFalse(b.run(writes(), false).isEmpty());
        }
    }

    @Test
    void theRowCountAgreesWhileRowsAreStale() {
        try (Backfill b = Backfill.start(ROWS, 5)) {
            b.run(writes(), false);
            assertEquals(b.sourceCount(), b.targetCount(),
                    "which is the entire reason a count is not a verification");
        }
    }

    @Test
    void theSecondPassRepairsTheStaleRows() {
        try (Backfill b = Backfill.start(ROWS, 5)) {
            assertTrue(b.run(writes(), true).isEmpty());
        }
    }

    @Test
    void aWriteBeforeItsBatchIsCopiedCorrectlyWithoutASecondPass() {
        // Id 2 is written during batch 0 and copied during batch 0. Whether that
        // is safe depends on the order within the batch, which the watermark does
        // not record; here the copy happens first, so it is not safe.
        try (Backfill b = Backfill.start(ROWS, 5)) {
            Set<Long> stale = b.run(List.of(new Backfill.Write(2, new BigDecimal("999.0000"), 0)), false);
            assertEquals(Set.of(2L), stale);
        }
    }

    @Test
    void aWriteAfterTheWholeBackfillIsStillCaughtBySecondPass() {
        try (Backfill b = Backfill.start(ROWS, 5)) {
            assertTrue(b.run(List.of(new Backfill.Write(1, new BigDecimal("5.0000"), 5)), true).isEmpty());
        }
    }

    @Test
    void smallerBatchesShrinkTheWindowAndCanCloseItEntirely() {
        Map<Integer, Integer> stalePerBatchSize = new LinkedHashMap<>();
        for (int size : new int[]{1, 3, 5, 10, 29}) {
            try (Backfill b = Backfill.start(ROWS, size)) {
                stalePerBatchSize.put(size, b.run(writes(), false).size());
            }
        }
        assertEquals(0, stalePerBatchSize.get(1),
                "with one row per batch each write lands before the row it targets is copied");
        assertTrue(stalePerBatchSize.get(29) > stalePerBatchSize.get(1),
                "one big batch is the worst case: " + stalePerBatchSize);
    }

    @Test
    void aSingleBatchIsTheWorstCase() {
        try (Backfill b = Backfill.start(ROWS, ROWS.size())) {
            assertFalse(b.run(writes(), false).isEmpty(),
                    "copying the whole table in one batch does not remove the window, it "
                            + "concentrates it");
        }
    }

    // -------------------------------------------------------------- gate

    @Test
    void aBlindVerifierIsRejectedEvenThoughItReportsNothing() {
        CutoverGate.Result r = CutoverGate.evaluate(new Comparison.RowCount(), ROWS, Migrator.defective());
        assertEquals(CutoverGate.Decision.NO_GO_BLIND, r.decision());
        assertEquals(0, r.reported(), "it is rejected precisely because it found nothing");
        assertEquals(0, r.controlsCaught());
    }

    @Test
    void anUnusableVerifierIsRejectedEvenThoughItCatchesEveryControl() {
        CutoverGate.Result r = CutoverGate.evaluate(new Comparison.NaiveChecksum(), ROWS, Migrator.defective());
        assertEquals(CutoverGate.Decision.NO_GO_UNUSABLE, r.decision());
        assertEquals(Migrator.defective().size(), r.controlsCaught(),
                "perfect recall does not make a verifier operable");
    }

    @Test
    void thePreciseVerifierIsRejectedAsBlindEvenOnACorruptedMigration() {
        // Ordering matters here and is deliberate. The full rule set does find
        // real corruption in this corpus, but it also fails its controls, and a
        // verifier that cannot be shown to work is not permitted to be believed
        // when it does report something.
        CutoverGate.Result r = CutoverGate.evaluate(
                new Comparison.Canonicalising(Rule.Set.all()), ROWS, Migrator.defective());
        assertEquals(CutoverGate.Decision.NO_GO_BLIND, r.decision(), r.reason());
    }

    @Test
    void aVerifierThatPassesItsControlsBlocksACorruptedMigration() {
        Rule.Set injectiveOnly = Rule.Set.of(Rule.NUMERIC, Rule.BOOLEAN, Rule.TEMPORAL, Rule.IDENTIFIER);
        CutoverGate.Result r = CutoverGate.evaluate(
                new Comparison.Canonicalising(injectiveOnly), ROWS, Migrator.defective());
        assertEquals(CutoverGate.Decision.NO_GO_CORRUPTION, r.decision(), r.reason());
        assertEquals(r.controlsPlanted(), r.controlsCaught());
        assertTrue(r.reported() > 0);
    }

    @Test
    void thePreciseConfigurationIsAlsoTheBlindOne() {
        // The rule set with perfect precision on a clean migration catches barely
        // any planted defect, because the rules that bought the precision are the
        // rules that hide the defects. This is the blindfold result restated as
        // an operational decision, and it is why the gate exists.
        List<Row> clean = ROWS.stream().filter(r -> !r.corrupting()).toList();
        CutoverGate.Result r = CutoverGate.evaluate(
                new Comparison.Canonicalising(Rule.Set.all()), clean, Migrator.defective());
        assertEquals(CutoverGate.Decision.NO_GO_BLIND, r.decision(), r.reason());
        assertTrue(r.controlsCaught() < Migrator.defective().size(), r.reason());
    }

    @Test
    void droppingTheValueCollapsingRulesRestoresTheControls() {
        List<Row> clean = ROWS.stream().filter(r -> !r.corrupting()).toList();
        Rule.Set injectiveOnly = Rule.Set.of(Rule.NUMERIC, Rule.BOOLEAN, Rule.TEMPORAL, Rule.IDENTIFIER);
        CutoverGate.Result r = CutoverGate.evaluate(
                new Comparison.Canonicalising(injectiveOnly), clean, Migrator.defective());
        assertEquals(r.controlsPlanted(), r.controlsCaught(),
                "the injective rules alone are precise and sensitive: " + r.reason());
        assertTrue(r.controlsPlanted() >= 4, r.reason());
        assertEquals(CutoverGate.Decision.GO, r.decision(), r.reason());
    }

    @Test
    void theGateWithNoControlsDegradesToTheCheckItReplaces() {
        List<Row> clean = ROWS.stream().filter(r -> !r.corrupting()).toList();
        CutoverGate.Result r = CutoverGate.evaluate(new Comparison.RowCount(), clean, List.of());
        assertEquals(CutoverGate.Decision.GO, r.decision(),
                "with no controls the gate cannot tell a working verifier from a blind one, "
                        + "which is what makes the controls the whole mechanism");
    }

    @Test
    void everyGateDecisionCarriesAReason() {
        for (Comparison c : List.of(new Comparison.RowCount(), new Comparison.NaiveChecksum(),
                new Comparison.Canonicalising(Rule.Set.all()))) {
            CutoverGate.Result r = CutoverGate.evaluate(c, ROWS, Migrator.defective());
            assertTrue(r.reason().length() > 20, c.name() + " gave no reason");
        }
    }

    @Test
    void theNoiseCeilingIsWhatSeparatesUnusableFromWorking() {
        CutoverGate.Result noisy = CutoverGate.evaluate(new Comparison.NaiveChecksum(), ROWS,
                Migrator.defective());
        assertTrue(noisy.reason().contains("%"), "the ceiling must be stated in the reason");
        assertTrue(CutoverGate.NOISE_CEILING > 0 && CutoverGate.NOISE_CEILING < 0.5);
    }

    // ------------------------------------------------------------ report

    @Test
    void theCommittedReportMatchesAFreshRun() throws Exception {
        Path p = Paths.get("docs", "results.md");
        assertTrue(Files.exists(p), "docs/results.md is missing; run Main to generate it");
        String committed = Files.readString(p, StandardCharsets.UTF_8);
        String fresh = Experiments.run();
        if (!committed.equals(fresh)) {
            String[] a = committed.split("\n", -1);
            String[] b = fresh.split("\n", -1);
            for (int i = 0; i < Math.max(a.length, b.length); i++) {
                String x = i < a.length ? a[i] : "(end)";
                String y = i < b.length ? b[i] : "(end)";
                if (!x.equals(y)) {
                    fail("docs/results.md is stale at line " + (i + 1)
                            + "\n  committed: " + x + "\n  fresh:     " + y);
                }
            }
            fail("docs/results.md differs in length only");
        }
    }

    @Test
    void theReportIsDeterministic() {
        assertEquals(Experiments.run(), Experiments.run(),
                "a report that changes between runs cannot be reviewed in a diff");
    }

    @Test
    void mostPredictionsWereContradicted() {
        Report r = new Report();
        r.expect("x", "c");
        r.found("x", false, "o");
        assertEquals(1, r.contradicted());

        String out = Experiments.run();
        int held = countOccurrences(out, "> **Held (");
        int contradicted = countOccurrences(out, "> **Contradicted (");
        assertTrue(contradicted > held,
                "a perfect record is evidence the predictions were written afterwards; held="
                        + held + " contradicted=" + contradicted);
    }

    @Test
    void everyPredictionIsSettled() {
        String out = Experiments.run();
        int predicted = countOccurrences(out, "> **Predicted (");
        int settled = countOccurrences(out, "> **Held (") + countOccurrences(out, "> **Contradicted (");
        assertEquals(predicted, settled);
    }

    @Test
    void theReportRefusesToRenderWithAnOpenPrediction() {
        Report r = new Report();
        r.expect("open", "never settled");
        IllegalStateException e = assertThrows(IllegalStateException.class, r::render);
        assertTrue(e.getMessage().contains("open"));
    }

    @Test
    void aPredictionCannotBeSettledTwice() {
        Report r = new Report();
        r.expect("a", "c");
        r.found("a", true, "o");
        assertThrows(IllegalStateException.class, () -> r.found("a", false, "again"));
    }

    @Test
    void aPredictionCannotBeRegisteredTwice() {
        Report r = new Report();
        r.expect("a", "c");
        assertThrows(IllegalStateException.class, () -> r.expect("a", "different"));
    }

    @Test
    void settlingAnUnregisteredPredictionFails() {
        assertThrows(IllegalStateException.class, () -> new Report().found("nope", true, "o"));
    }

    @Test
    void theReportContainsNoPlaceholderNumbers() {
        String out = Experiments.run();
        for (String bad : List.of("null of", "of null", " null rule", "NaN", "Infinity", "TODO", "XXX")) {
            assertFalse(out.contains(bad), "report contains '" + bad + "'");
        }
    }

    @Test
    void theReportCoversEveryHazardByName() {
        String out = Experiments.run();
        for (Hazard h : Hazard.values()) {
            assertTrue(out.contains(h.name()), h + " is never mentioned in the report");
        }
    }

    @Test
    void theReportCoversEveryRuleAndMigratorByName() {
        String out = Experiments.run();
        for (Rule r : Rule.values()) {
            assertTrue(out.contains("`" + r.id() + "`"), r.id() + " is never mentioned");
        }
        for (Migrator m : Migrator.all()) {
            assertTrue(out.contains("`" + m.name() + "`"), m.name() + " is never mentioned");
        }
    }

    private static int countOccurrences(String haystack, String needle) {
        int n = 0;
        int i = 0;
        while ((i = haystack.indexOf(needle, i)) >= 0) {
            n++;
            i += needle.length();
        }
        return n;
    }
}
