package dev.hybrid;

import java.util.*;

/**
 * A results document that has to be written before the results are known.
 *
 * <p>Every claim must be registered with {@link #expect} before the measurement
 * that settles it is recorded with {@link #found}, and {@link #render} refuses to
 * produce a document with a prediction still open. The mechanism is small and
 * the point of it is not: a report assembled after the fact is a description of
 * whatever happened, and reads as though everything went as planned. Writing the
 * prediction down first is the only way to find out how often it did not.
 *
 * <p>It did not, here, more often than not.
 */
public final class Report {

    /** One registered claim and its fate. */
    public static final class Prediction {
        final String id;
        final String claim;
        String outcome;
        boolean held;

        Prediction(String id, String claim) {
            this.id = id;
            this.claim = claim;
        }
    }

    private final StringBuilder body = new StringBuilder();
    private final Map<String, Prediction> predictions = new LinkedHashMap<>();
    private final List<Prediction> order = new ArrayList<>();

    public Report title(String t) {
        body.append("# ").append(t).append("\n\n");
        return this;
    }

    public Report section(String s) {
        body.append("\n## ").append(s).append("\n\n");
        return this;
    }

    public Report sub(String s) {
        body.append("\n### ").append(s).append("\n\n");
        return this;
    }

    public Report text(String s) {
        body.append(s).append("\n\n");
        return this;
    }

    public Report line(String s) {
        body.append(s).append("\n");
        return this;
    }

    public Report blank() {
        body.append("\n");
        return this;
    }

    /** Register a claim. Must happen before the corresponding {@link #found}. */
    public Report expect(String id, String claim) {
        if (predictions.containsKey(id)) {
            throw new IllegalStateException("prediction already registered: " + id);
        }
        Prediction p = new Prediction(id, claim);
        predictions.put(id, p);
        order.add(p);
        body.append("> **Predicted (").append(id).append(")** -- ").append(claim).append("\n\n");
        return this;
    }

    /** Settle a registered claim against what was measured. */
    public Report found(String id, boolean held, String outcome) {
        Prediction p = predictions.get(id);
        if (p == null) {
            throw new IllegalStateException("no such prediction: " + id);
        }
        if (p.outcome != null) {
            throw new IllegalStateException("prediction already settled: " + id);
        }
        p.held = held;
        p.outcome = outcome;
        body.append("> **").append(held ? "Held" : "Contradicted").append(" (").append(id)
                .append(")** -- ").append(outcome).append("\n\n");
        return this;
    }

    public Report table(String[] headers, List<String[]> rows) {
        body.append("| ").append(String.join(" | ", headers)).append(" |\n");
        body.append("|").append("---|".repeat(headers.length)).append("\n");
        for (String[] r : rows) {
            body.append("| ").append(String.join(" | ", r)).append(" |\n");
        }
        body.append("\n");
        return this;
    }

    public int contradicted() {
        return (int) order.stream().filter(p -> p.outcome != null && !p.held).count();
    }

    public String render() {
        List<String> open = order.stream().filter(p -> p.outcome == null).map(p -> p.id).toList();
        if (!open.isEmpty()) {
            throw new IllegalStateException("predictions never settled: " + open);
        }
        StringBuilder out = new StringBuilder(body);
        out.append("\n## Scoreboard\n\n");
        out.append("| id | prediction | outcome |\n|---|---|---|\n");
        for (Prediction p : order) {
            out.append("| ").append(p.id).append(" | ").append(p.claim).append(" | ")
                    .append(p.held ? "held" : "**contradicted**").append(" |\n");
        }
        long held = order.stream().filter(p -> p.held).count();
        out.append("\n").append(order.size()).append(" predictions registered before measurement; ")
                .append(held).append(" held, ").append(order.size() - held).append(" contradicted.\n");
        return out.toString();
    }
}
