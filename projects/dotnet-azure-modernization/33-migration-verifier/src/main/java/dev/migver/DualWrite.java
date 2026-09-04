package dev.migver;

import java.util.*;

/**
 * The second hazard surface.
 *
 * <p>A copy migration and a dual write are not the same operation, and the
 * distinction is usually lost because both are described as "getting the data
 * into the new system". In a copy migration every value passes through the
 * source first, so the source's own coercions are applied to both sides and
 * cancel out. In a dual write the application writes the same value to both
 * engines independently, and each engine coerces it in its own way, with nothing
 * to cancel.
 *
 * <p>The practical consequence is that a verifier calibrated during the backfill
 * -- when everything is a copy -- is calibrated for the wrong hazard set on the
 * day dual write is switched on. {@link Experiments} measures how different the
 * two sets are.
 */
public final class DualWrite implements AutoCloseable {

    private static int counter = 0;

    private final Engine left;
    private final Engine right;
    private final List<Row> corpus;

    private DualWrite(Engine left, Engine right, List<Row> corpus) {
        this.left = left;
        this.right = right;
        this.corpus = corpus;
    }

    public static DualWrite run(List<Row> corpus) {
        String tag = "d" + (++counter);
        Engine l = Engine.open(Engine.Kind.H2, tag);
        Engine r = Engine.open(Engine.Kind.SQLITE, tag);
        l.createTable(Corpus.TABLE);
        r.createTable(Corpus.TABLE);
        for (Row row : corpus) {
            l.insert(Corpus.TABLE, row);
            r.insert(Corpus.TABLE, row);
        }
        return new DualWrite(l, r, corpus);
    }

    public List<Map<String, Object>> leftRows() {
        return left.read(Corpus.TABLE);
    }

    public List<Map<String, Object>> rightRows() {
        return right.read(Corpus.TABLE);
    }

    public java.util.Set<Long> trulyCorrupted() {
        java.util.Set<Long> out = new TreeSet<>();
        for (Row r : corpus) {
            if (r.corrupting()) {
                out.add(r.id());
            }
        }
        return out;
    }

    public java.util.Set<Long> allIds() {
        java.util.Set<Long> out = new TreeSet<>();
        for (Row r : corpus) {
            out.add(r.id());
        }
        return out;
    }

    public Confusion evaluate(Comparison c) {
        return Confusion.of(c.mismatches(leftRows(), rightRows()), trulyCorrupted(), allIds());
    }

    @Override
    public void close() {
        left.close();
        right.close();
    }
}
