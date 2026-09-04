package dev.migver;

import java.util.*;

/**
 * Runs one migration end to end and holds both sides for inspection.
 *
 * <p>The pipeline is deliberately the real one: corpus values are written into
 * the source first and read back from it, so that anything the <em>source</em>
 * does to a value on the way in -- scale enforcement, collation, type checking --
 * has already happened before the migrator sees it. Verifiers that compare the
 * target against the original input file rather than against the source database
 * are measuring the wrong thing, and will attribute the source's own coercions
 * to the migration.
 */
public final class Migration implements AutoCloseable {

    private static int counter = 0;

    private final Engine source;
    private final Engine target;
    private final Migrator migrator;
    private final List<Row> corpus;

    private Migration(Engine source, Engine target, Migrator migrator, List<Row> corpus) {
        this.source = source;
        this.target = target;
        this.migrator = migrator;
        this.corpus = corpus;
    }

    public static Migration run(Migrator migrator, List<Row> corpus) {
        String tag = "m" + (++counter);
        Engine src = Engine.open(Engine.Kind.H2, tag);
        Engine dst = Engine.open(Engine.Kind.SQLITE, tag);
        src.createTable(Corpus.TABLE);
        dst.createTable(Corpus.TABLE);
        for (Row r : corpus) {
            src.insert(Corpus.TABLE, r);
        }
        for (Map<String, Object> row : src.read(Corpus.TABLE)) {
            dst.insertMap(Corpus.TABLE, migrator.apply(row));
        }
        return new Migration(src, dst, migrator, corpus);
    }

    public List<Map<String, Object>> sourceRows() {
        return source.read(Corpus.TABLE);
    }

    public List<Map<String, Object>> targetRows() {
        return target.read(Corpus.TABLE);
    }

    public Engine source() {
        return source;
    }

    public Engine target() {
        return target;
    }

    public Migrator migrator() {
        return migrator;
    }

    public List<Row> corpus() {
        return corpus;
    }

    /**
     * Ids whose information was genuinely destroyed, from the union of what the
     * engine pair does and what this migrator does.
     *
     * <p>This is ground truth, computed from the definitions of the hazards and
     * of the migrator. No verifier's output contributes to it, which is what
     * makes the confusion matrices meaningful rather than self-congratulatory.
     */
    public java.util.Set<Long> trulyCorrupted() {
        java.util.Set<Long> out = new TreeSet<>();
        for (Row r : corpus) {
            if (r.corrupting() || migrator.damages(r)) {
                out.add(r.id());
            }
        }
        return out;
    }

    public Confusion evaluate(Comparison c) {
        return Confusion.of(c.mismatches(sourceRows(), targetRows()), trulyCorrupted(), allIds());
    }

    public java.util.Set<Long> allIds() {
        java.util.Set<Long> out = new TreeSet<>();
        for (Row r : corpus) {
            out.add(r.id());
        }
        return out;
    }

    /**
     * The check no row comparison can perform: ask each engine, in its own
     * collation, how many distinct names it is holding.
     *
     * <p>Every row can be byte-identical on both sides and this can still differ,
     * because it is a property of the index and not of the data. It is the only
     * check in the project that catches {@link Hazard#COLLATION_FOLDS_LESS}.
     */
    public long[] distinctNames() {
        return new long[]{source.distinctNames(Corpus.TABLE), target.distinctNames(Corpus.TABLE)};
    }

    @Override
    public void close() {
        source.close();
        target.close();
    }
}
