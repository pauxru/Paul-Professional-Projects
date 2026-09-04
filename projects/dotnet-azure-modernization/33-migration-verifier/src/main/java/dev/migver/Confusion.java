package dev.migver;

import java.util.Set;
import java.util.TreeSet;

/**
 * The four numbers that decide whether a verifier is worth running.
 *
 * <p>Migration verifiers are almost always reported on with one number -- "we
 * checked 40 million rows and found 12 differences" -- which is a count of true
 * plus false positives with no denominator and no statement at all about what
 * was missed. The interesting number is {@link #falseNegatives}, and it is the
 * one you cannot obtain without knowing the ground truth, which is why this
 * project constructs the ground truth rather than measuring a real migration.
 */
public record Confusion(Set<Long> truePositives, Set<Long> falsePositives,
                        Set<Long> falseNegatives, Set<Long> trueNegatives) {

    public static Confusion of(Set<Long> reported, Set<Long> actuallyCorrupted, Set<Long> universe) {
        Set<Long> tp = new TreeSet<>(reported);
        tp.retainAll(actuallyCorrupted);
        Set<Long> fp = new TreeSet<>(reported);
        fp.removeAll(actuallyCorrupted);
        Set<Long> fn = new TreeSet<>(actuallyCorrupted);
        fn.removeAll(reported);
        Set<Long> tn = new TreeSet<>(universe);
        tn.removeAll(reported);
        tn.removeAll(actuallyCorrupted);
        return new Confusion(tp, fp, fn, tn);
    }

    public int tp() {
        return truePositives.size();
    }

    public int fp() {
        return falsePositives.size();
    }

    public int fn() {
        return falseNegatives.size();
    }

    public int tn() {
        return trueNegatives.size();
    }

    /** Of what it reported, how much was real. Low precision is what gets a verifier switched off. */
    public double precision() {
        int d = tp() + fp();
        return d == 0 ? 1.0 : (double) tp() / d;
    }

    /** Of what was real, how much it reported. Low recall is what gets a company sued. */
    public double recall() {
        int d = tp() + fn();
        return d == 0 ? 1.0 : (double) tp() / d;
    }

    /**
     * Whether this verifier would have blocked the cutover.
     *
     * <p>Note that a verifier with terrible precision blocks the cutover for the
     * wrong reason, and blocking for the wrong reason is not safety -- it is the
     * mechanism by which the verifier is eventually removed.
     */
    public boolean wouldBlock() {
        return tp() + fp() > 0;
    }

    /** Blocked, and blocked because it found something real. */
    public boolean blocksCorrectly() {
        return tp() > 0;
    }

    public String summary() {
        return String.format("tp=%d fp=%d fn=%d tn=%d precision=%.2f recall=%.2f",
                tp(), fp(), fn(), tn(), precision(), recall());
    }
}
