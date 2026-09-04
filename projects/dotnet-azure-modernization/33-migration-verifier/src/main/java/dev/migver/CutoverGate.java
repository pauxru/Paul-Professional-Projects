package dev.migver;

import java.util.*;

/**
 * The decision procedure that decides whether to cut over.
 *
 * <p>The usual gate is "the verifier reported no differences". That is not
 * evidence of correctness. It is consistent with a correct migration and it is
 * equally consistent with a verifier that cannot detect anything -- a query that
 * silently returned no rows, a canonicalisation rule set that has been relaxed
 * until it agrees with everything, a comparison that was pointed at the wrong
 * table. All of those produce exactly the same clean report, and all of them
 * have shipped.
 *
 * <p>So the gate here refuses to accept an absence of evidence. It requires a
 * <em>positive control</em>: before the verifier's silence is allowed to mean
 * anything, a known corruption is injected and the verifier must be observed
 * catching it. A verifier that passes the real data and fails the control is
 * working. A verifier that passes both is not a verifier.
 *
 * <p>This is the same idea as a smoke test in a wet lab, and the same idea as
 * mutation testing, and it is uncontroversial in both. It is almost never done
 * for migrations.
 */
public final class CutoverGate {

    public enum Decision {
        /** The verifier demonstrated it can detect corruption and found none. */
        GO,
        /** The verifier found corruption. The migration is wrong. */
        NO_GO_CORRUPTION,
        /** The verifier found nothing, and also could not find the planted defect. */
        NO_GO_BLIND,
        /** The verifier objects to so much that its objections carry no information. */
        NO_GO_UNUSABLE
    }

    public record Result(Decision decision, String reason, int reported, int controlsCaught,
                         int controlsPlanted, double precision) {

        public boolean go() {
            return decision == Decision.GO;
        }
    }

    /**
     * The share of rows a verifier may object to before its objections stop being
     * actionable. Above this, nobody triages the output; they suppress it.
     */
    public static final double NOISE_CEILING = 0.05;

    private CutoverGate() {
    }

    /**
     * @param comparison   the verifier under evaluation
     * @param corpus       the data being migrated
     * @param controls     defects to plant, one migration each; the verifier must
     *                     catch every one
     */
    public static Result evaluate(Comparison comparison, List<Row> corpus, List<Migrator> controls) {
        int caught = 0;
        int applicable = 0;
        for (Migrator control : controls) {
            java.util.Set<Long> planted = new TreeSet<>();
            for (Row row : corpus) {
                if (control.damages(row)) {
                    planted.add(row.id());
                }
            }
            if (planted.isEmpty()) {
                // This control cannot damage anything in this corpus, so it can
                // neither be caught nor missed. Counting it as missed would make
                // the corpus's coverage look like the verifier's blindness.
                continue;
            }
            applicable++;
            try (Migration m = Migration.run(control, corpus)) {
                // The control counts as caught only if the verifier flagged one of
                // the rows this control specifically damaged. An earlier version
                // asked only whether the verifier had any true positive at all,
                // and every control scored a hit because the corpus contains two
                // rows the engines corrupt regardless of the migrator. The
                // verifier was being credited for a detection it would have made
                // with the control absent -- a positive control that is positive
                // whatever you do is not a control.
                java.util.Set<Long> reported = comparison.mismatches(m.sourceRows(), m.targetRows());
                if (!Collections.disjoint(reported, planted)) {
                    caught++;
                }
            }
        }

        try (Migration real = Migration.run(new Migrator.Faithful(), corpus)) {
            Confusion c = real.evaluate(comparison);
            double noise = (double) c.fp() / Math.max(1, real.allIds().size());

            if (noise > NOISE_CEILING) {
                return new Result(Decision.NO_GO_UNUSABLE,
                        String.format("objects to %.0f%% of rows; above the %.0f%% ceiling its output is not triaged",
                                noise * 100, NOISE_CEILING * 100),
                        c.tp() + c.fp(), caught, applicable, c.precision());
            }
            if (caught < applicable) {
                return new Result(Decision.NO_GO_BLIND,
                        String.format("caught %d of %d planted defects; its silence on the real data means nothing",
                                caught, applicable),
                        c.tp() + c.fp(), caught, applicable, c.precision());
            }
            if (c.tp() + c.fp() > 0) {
                return new Result(Decision.NO_GO_CORRUPTION,
                        "reported " + (c.tp() + c.fp()) + " difference"
                                + (c.tp() + c.fp() == 1 ? "" : "s") + " on the real data",
                        c.tp() + c.fp(), caught, applicable, c.precision());
            }
            return new Result(Decision.GO,
                    "caught all " + applicable + " planted defects and reported nothing on the real data",
                    0, caught, applicable, c.precision());
        }
    }
}
