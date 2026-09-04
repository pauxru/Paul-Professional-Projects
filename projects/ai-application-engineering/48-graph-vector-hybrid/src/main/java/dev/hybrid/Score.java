package dev.hybrid;

import java.util.*;

/**
 * Set-valued answer scoring.
 *
 * <p>Exact-match accuracy is the wrong metric for questions whose answer is a
 * set: it scores "two of the three sanctioned suppliers" identically to "none
 * of them", and the difference between those two is the difference between a
 * partial investigation and no investigation.
 *
 * <p>The negative questions -- the ones whose correct answer is the empty set --
 * are scored explicitly, because a system that answers everything with something
 * gets a respectable F1 while being useless for the only question in compliance
 * that matters, which is whether you may proceed.
 */
public record Score(int tp, int fp, int fn, boolean goldEmpty, boolean predictedEmpty) {

    public static Score of(Set<String> gold, Set<String> predicted) {
        Set<String> t = new TreeSet<>(predicted);
        t.retainAll(gold);
        Set<String> f = new TreeSet<>(predicted);
        f.removeAll(gold);
        Set<String> m = new TreeSet<>(gold);
        m.removeAll(predicted);
        return new Score(t.size(), f.size(), m.size(), gold.isEmpty(), predicted.isEmpty());
    }

    public double precision() {
        int d = tp + fp;
        return d == 0 ? 1.0 : tp / (double) d;
    }

    public double recall() {
        int d = tp + fn;
        return d == 0 ? 1.0 : tp / (double) d;
    }

    public double f1() {
        double p = precision();
        double r = recall();
        return p + r == 0 ? 0 : 2 * p * r / (p + r);
    }

    /** Fully correct: every gold value found, nothing invented. */
    public boolean exact() {
        return fp == 0 && fn == 0;
    }

    /**
     * A false alarm on a question whose answer is nothing. Tracked separately
     * from ordinary false positives because its operational cost is different:
     * it stops a shipment, freezes a payment, or triggers a filing.
     */
    public boolean falseAlarm() {
        return goldEmpty && !predictedEmpty;
    }

    public static String pct(double x) {
        return String.format(Locale.ROOT, "%.2f", x);
    }
}
