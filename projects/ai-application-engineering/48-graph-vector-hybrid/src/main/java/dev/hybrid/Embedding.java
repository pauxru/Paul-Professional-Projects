package dev.hybrid;

import java.util.*;

/**
 * A deterministic lexical embedding: character 4-gram TF-IDF, L2-normalised.
 *
 * <p>This is not a neural embedding and the project never pretends otherwise.
 * It is a genuine vector space with a genuine cosine metric, built from a real
 * distributional signal, and it has the property that matters for every
 * measurement here: <em>it is a similarity function over surface form, and
 * similarity is not entailment.</em>
 *
 * <p>Using a real sentence encoder would change the numbers in section 2 -- it
 * would score paraphrases better, so single-hop retrieval would improve -- and
 * would change nothing at all about sections 3 through 6, because the multi-hop
 * failure this project measures is not a failure of embedding quality. A
 * retriever returns passages that exist. If no passage states the answer, no
 * amount of encoder quality creates one. That claim is argued in
 * docs/adr/001-lexical-embeddings.md and tested directly in section 4, where a
 * <em>perfect</em> retriever -- one handed the correct premises by an oracle --
 * still cannot answer the question.
 *
 * <p>Character n-grams rather than words, because entity resolution is half of
 * this project and "Acme Corp." vs "ACME Corporation" has no word overlap
 * beyond a token a word model would treat as identical or absent, whereas the
 * n-gram model sees a graded signal.
 */
public final class Embedding {

    /** Character n-gram width. 4 is the smallest width that keeps "Ltd" and "Plc" apart. */
    public static final int N = 4;

    private final Map<String, Integer> vocabulary = new LinkedHashMap<>();
    private final Map<String, Double> idf = new LinkedHashMap<>();
    private boolean fitted;

    /** Builds the vocabulary and IDF table. Must be called before {@link #vectorise}. */
    public void fit(List<String> documents) {
        Map<String, Integer> documentFrequency = new LinkedHashMap<>();
        for (String d : documents) {
            for (String g : new TreeSet<>(grams(d))) {
                documentFrequency.merge(g, 1, Integer::sum);
                vocabulary.putIfAbsent(g, vocabulary.size());
            }
        }
        int n = documents.size();
        for (Map.Entry<String, Integer> e : documentFrequency.entrySet()) {
            // Smoothed IDF. Without the +1 on the numerator a gram present in
            // every document gets weight 0 and drops out of the space entirely,
            // which quietly removes the most common entity type suffixes.
            idf.put(e.getKey(), Math.log((n + 1.0) / (e.getValue() + 1.0)) + 1.0);
        }
        fitted = true;
    }

    public boolean fitted() {
        return fitted;
    }

    public int vocabularySize() {
        return vocabulary.size();
    }

    /**
     * A sparse L2-normalised TF-IDF vector.
     *
     * <p>Sparse rather than dense because the vocabulary runs to tens of
     * thousands of grams and every document touches a few hundred of them. A
     * dense array would make the corpus a matrix of mostly zeros and the cosine
     * a scan over them.
     */
    public Map<Integer, Double> vectorise(String text) {
        if (!fitted) {
            throw new IllegalStateException("fit() first; an unfitted embedding silently returns "
                    + "the zero vector, which has cosine 0 with everything and looks like a "
                    + "retrieval failure rather than a bug");
        }
        Map<Integer, Double> v = new LinkedHashMap<>();
        Map<String, Integer> counts = new LinkedHashMap<>();
        for (String g : grams(text)) {
            counts.merge(g, 1, Integer::sum);
        }
        for (Map.Entry<String, Integer> e : counts.entrySet()) {
            Integer index = vocabulary.get(e.getKey());
            if (index == null) {
                // An out-of-vocabulary gram. Dropped, deliberately: assigning it
                // a weight would mean the vector depends on which documents were
                // in the fit, in a way that is not the IDF's dependence.
                continue;
            }
            double w = idf.getOrDefault(e.getKey(), 1.0);
            v.put(index, (1.0 + Math.log(e.getValue())) * w);
        }
        double norm = 0;
        for (double x : v.values()) {
            norm += x * x;
        }
        final double length = Math.sqrt(norm);
        if (length == 0) {
            return v;
        }
        v.replaceAll((k, x) -> x / length);
        return v;
    }

    /** Cosine of two L2-normalised sparse vectors, which is their dot product. */
    public static double cosine(Map<Integer, Double> a, Map<Integer, Double> b) {
        Map<Integer, Double> small = a.size() <= b.size() ? a : b;
        Map<Integer, Double> large = small == a ? b : a;
        double dot = 0;
        for (Map.Entry<Integer, Double> e : small.entrySet()) {
            Double other = large.get(e.getKey());
            if (other != null) {
                dot += e.getValue() * other;
            }
        }
        return dot;
    }

    static List<String> grams(String text) {
        String t = normalise(text);
        List<String> out = new ArrayList<>();
        if (t.length() < N) {
            out.add(t);
            return out;
        }
        for (int i = 0; i + N <= t.length(); i++) {
            out.add(t.substring(i, i + N));
        }
        return out;
    }

    /**
     * Lower-cases, collapses whitespace, and pads with a boundary marker.
     *
     * <p>The padding is not decoration. Without it "Acme" and "Macme" share
     * every 4-gram of the shorter string and score far too close; the boundary
     * marker makes a prefix match distinguishable from an infix one, which is
     * exactly the distinction entity resolution needs.
     */
    static String normalise(String text) {
        String t = text.toLowerCase(Locale.ROOT).replaceAll("[^a-z0-9]+", " ").trim();
        return "^" + t.replaceAll("\\s+", " ") + "$";
    }
}
