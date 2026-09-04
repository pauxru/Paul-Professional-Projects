package dev.hybrid;

import java.util.*;

/** Brute-force cosine retrieval over the document corpus. */
public final class Retriever {

    private final Embedding embedding = new Embedding();
    private final List<Map<Integer, Double>> vectors = new ArrayList<>();

    public Retriever() {
        List<String> texts = Corpus.documents().stream().map(Corpus.Doc::text).toList();
        embedding.fit(texts);
        for (String t : texts) {
            vectors.add(embedding.vectorise(t));
        }
    }

    public Embedding embedding() {
        return embedding;
    }

    /** Document ids in descending similarity order, ties broken by id. */
    public List<Integer> topK(String query, int k) {
        Map<Integer, Double> q = embedding.vectorise(query);
        List<int[]> ranked = new ArrayList<>();
        double[] scores = new double[vectors.size()];
        for (int i = 0; i < vectors.size(); i++) {
            scores[i] = Embedding.cosine(q, vectors.get(i));
            ranked.add(new int[]{i});
        }
        ranked.sort((a, b) -> {
            int c = Double.compare(scores[b[0]], scores[a[0]]);
            // Ties broken by id rather than left to sort stability.
            //
            // Honesty note: this branch is currently unreachable in the sense
            // that removing it changes nothing. List.sort is a stable TimSort
            // and `ranked` is built in ascending id order, so equal scores
            // already come out in id order. It survives mutation testing for
            // that reason -- an equivalent mutant, not a gap in the suite, and
            // test.ps1 says so rather than pretending otherwise.
            //
            // It is kept because both of those facts are incidental. A future
            // change to a parallel sort, or to building `ranked` from a map
            // iteration, would silently make retrieval order depend on
            // something other than the score, and every number in the report
            // would start drifting between runs for no visible reason.
            return c != 0 ? c : Integer.compare(a[0], b[0]);
        });
        List<Integer> out = new ArrayList<>();
        for (int i = 0; i < Math.min(k, ranked.size()); i++) {
            out.add(ranked.get(i)[0]);
        }
        return List.copyOf(out);
    }

    public double score(String query, int docId) {
        return Embedding.cosine(embedding.vectorise(query), vectors.get(docId));
    }
}
