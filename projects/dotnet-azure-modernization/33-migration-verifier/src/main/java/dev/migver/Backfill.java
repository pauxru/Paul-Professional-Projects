package dev.migver;

import java.math.BigDecimal;
import java.util.*;

/**
 * The online backfill: copy the table in id order while the application keeps
 * writing to the source.
 *
 * <p>This is how every large migration is actually performed, because taking the
 * source read-only for the duration is not on offer. The watermark pattern --
 * copy ids 1..N, remember N, copy N+1.. -- is correct for inserts and wrong for
 * updates, and the wrongness is invisible to a row count because no rows are
 * missing. They are all present and some of them are stale.
 *
 * <p>The usual mitigation is a second pass over rows whose modification stamp is
 * newer than the batch that copied them. That works, and it works only if the
 * stamp is updated by something the application cannot forget to call. Here the
 * stamp is updated faithfully, so the second pass is measured doing its job, and
 * then measured failing on the one case it cannot see: a row updated while the
 * second pass was itself running.
 */
public final class Backfill implements AutoCloseable {

    private static int counter = 0;

    /** An update the application performs during the backfill. */
    public record Write(long id, BigDecimal amount, int afterBatch) {
    }

    private final Engine source;
    private final Engine target;
    private final List<Row> corpus;
    private final int batchSize;
    private final Map<Long, Object> copiedValue = new HashMap<>();

    private Backfill(Engine source, Engine target, List<Row> corpus, int batchSize) {
        this.source = source;
        this.target = target;
        this.corpus = corpus;
        this.batchSize = batchSize;
    }

    public static Backfill start(List<Row> corpus, int batchSize) {
        String tag = "b" + (++counter);
        Engine src = Engine.open(Engine.Kind.H2, tag);
        Engine dst = Engine.open(Engine.Kind.SQLITE, tag);
        src.createTable(Corpus.TABLE);
        dst.createTable(Corpus.TABLE);
        for (Row r : corpus) {
            src.insert(Corpus.TABLE, r);
        }
        return new Backfill(src, dst, corpus, batchSize);
    }

    /**
     * Run the backfill, applying the given concurrent writes to the source
     * between batches.
     *
     * @param secondPass whether to re-copy rows whose stamp moved after the batch
     *                   that copied them
     * @return ids that ended up stale in the target
     */
    public java.util.Set<Long> run(List<Write> writes, boolean secondPass) {
        List<Row> ordered = corpus.stream().sorted(Comparator.comparingLong(Row::id)).toList();
        Map<Long, Integer> copiedInBatch = new HashMap<>();
        Map<Long, Integer> lastWriteBatch = new HashMap<>();

        int batch = 0;
        for (int i = 0; i < ordered.size(); i += batchSize) {
            for (Row r : ordered.subList(i, Math.min(i + batchSize, ordered.size()))) {
                Map<String, Object> row = readOne(r.id());
                copiedValue.put(r.id(), Rule.NUMERIC.apply(row.get("amount")));
                target.insertMap(Corpus.TABLE, row);
                copiedInBatch.put(r.id(), batch);
            }
            for (Write w : writes) {
                if (w.afterBatch() == batch) {
                    source.execute("UPDATE " + Corpus.TABLE + " SET amount = " + w.amount()
                            + " WHERE id = " + w.id());
                    lastWriteBatch.put(w.id(), batch);
                }
            }
            batch++;
        }

        if (secondPass) {
            for (Map.Entry<Long, Integer> e : lastWriteBatch.entrySet()) {
                // The row is re-copied only if it was written *after* the batch
                // that copied it. A row written before its batch was already
                // copied correctly, and re-copying it would be wasted IO on a
                // table with forty million rows in it.
                if (e.getValue() >= copiedInBatch.getOrDefault(e.getKey(), Integer.MAX_VALUE)) {
                    Map<String, Object> fresh = readOne(e.getKey());
                    copiedValue.put(e.getKey(), Rule.NUMERIC.apply(fresh.get("amount")));
                    target.execute("DELETE FROM " + Corpus.TABLE + " WHERE id = " + e.getKey());
                    target.insertMap(Corpus.TABLE, fresh);
                }
            }
        }

        return stale();
    }

    private Map<String, Object> readOne(long id) {
        for (Map<String, Object> r : source.read(Corpus.TABLE)) {
            if (((Number) r.get("id")).longValue() == id) {
                return r;
            }
        }
        throw new IllegalStateException("no row " + id);
    }

    /**
     * Ids whose source value has moved since the copy took it. Ground truth for
     * staleness, and deliberately <em>not</em> a comparison of the two databases.
     *
     * <p>An earlier version compared source to target directly and reported the
     * one row whose amount exceeds a double's precision as permanently stale,
     * because that row does differ and always will. Staleness and engine
     * coercion are different failures with different fixes; a measurement that
     * cannot separate them is measuring neither. Comparing against the value that
     * was actually copied keeps them apart.
     */
    public java.util.Set<Long> stale() {
        java.util.Set<Long> out = new TreeSet<>();
        for (Map<String, Object> r : source.read(Corpus.TABLE)) {
            long id = ((Number) r.get("id")).longValue();
            if (!copiedValue.containsKey(id)) {
                continue;
            }
            if (!Objects.equals(Rule.NUMERIC.apply(r.get("amount")), copiedValue.get(id))) {
                out.add(id);
            }
        }
        return out;
    }

    public long sourceCount() {
        return source.count(Corpus.TABLE);
    }

    public long targetCount() {
        return target.count(Corpus.TABLE);
    }

    @Override
    public void close() {
        source.close();
        target.close();
    }
}
