package dev.hybrid;

import java.util.*;

/**
 * Entity resolution: mapping surface forms onto canonical entities using the
 * same embedding the retriever uses.
 *
 * <p>This is the part of a "graph vs vector" comparison that usually goes
 * unmentioned, and it is the reason the honest answer is neither. The graph
 * cannot be built without deciding that "Halcyon Trading Co" and "Halcyon
 * Trading Company" are one node and that "Meridian Shipping Ltd" and "Meridian
 * Freight Services" are two. That decision is a similarity judgement over
 * surface form -- which is exactly what an embedding is for and exactly what
 * graph traversal cannot do.
 *
 * <p>So the architecture the measurements support is not graph <em>or</em>
 * vector. It is <b>vector for identity, graph for traversal</b>: embeddings
 * decide what a node is, the graph decides what follows from it.
 *
 * <p>The threshold is the whole game. Too low and distinct entities merge,
 * fabricating edges that create paths to conclusions nobody asserted. Too high
 * and one entity splits, silently disconnecting the graph. Section 5 sweeps it
 * and measures how each error propagates with hop distance.
 */
public final class Resolver {

    public record Result(Map<String, String> surfaceToCanonical, int merges, int splits) {}

    private final Embedding embedding;
    private final double threshold;
    private final List<String> canonicalOrder = new ArrayList<>();
    private final Map<String, Map<Integer, Double>> canonicalVectors = new LinkedHashMap<>();
    private final Map<String, String> assigned = new LinkedHashMap<>();

    public Resolver(Embedding embedding, double threshold) {
        this.embedding = embedding;
        this.threshold = threshold;
    }

    public double threshold() {
        return threshold;
    }

    /**
     * Greedy single-pass clustering of surface forms.
     *
     * <p>Deliberately the naive algorithm rather than a good one. A better
     * clusterer would move the threshold at which each error appears; it would
     * not remove the trade-off, because the trade-off is a property of a
     * similarity function being asked to make a discrete decision. Using the
     * simple version keeps the measured effect attributable to the threshold
     * rather than to clustering heuristics.
     *
     * <p>Order-dependence is real and is why the input is sorted: the first
     * surface form to claim a cluster names it. Without the sort the resolution
     * would depend on document order, which is a coin flip dressed up as a
     * decision.
     */
    public Result resolve(Collection<String> surfaceForms) {
        // Reset first. Without this, resolving twice on one instance clusters
        // the second input against the first input's clusters and returns a
        // different answer for the same argument -- a stateful method that
        // looks pure. It cost a failing test to notice, which is the cheapest
        // place to notice it.
        canonicalOrder.clear();
        canonicalVectors.clear();
        assigned.clear();
        List<String> sorted = new ArrayList<>(new TreeSet<>(surfaceForms));
        for (String s : sorted) {
            Map<Integer, Double> v = embedding.vectorise(s);
            String bestCanonical = null;
            double bestScore = -1;
            for (String c : canonicalOrder) {
                double score = Embedding.cosine(v, canonicalVectors.get(c));
                if (score > bestScore) {
                    bestScore = score;
                    bestCanonical = c;
                }
            }
            if (bestCanonical != null && bestScore >= threshold) {
                assigned.put(s, bestCanonical);
            } else {
                canonicalOrder.add(s);
                canonicalVectors.put(s, v);
                assigned.put(s, s);
            }
        }
        return new Result(Map.copyOf(assigned), 0, 0);
    }

    public String canonical(String surface) {
        return assigned.getOrDefault(surface, surface);
    }

    /**
     * Resolves a name that was never in the corpus -- a name a user typed.
     *
     * <p>A production system does exactly this and it is usually invisible in
     * architecture diagrams: before any traversal can start, the query's entity
     * must be matched to a node, and that match is another similarity judgement
     * made by the same embedding at the same threshold. It follows that a
     * threshold badly chosen for the corpus is badly chosen for the query too,
     * and the two errors compound rather than cancel -- a query that resolves to
     * a merged node inherits that node's fabricated edges.
     */
    public String resolveQuery(String name) {
        String exact = assigned.get(name);
        if (exact != null) {
            return exact;
        }
        Map<Integer, Double> v = embedding.vectorise(name);
        String best = null;
        double bestScore = -1;
        for (String c : canonicalOrder) {
            double score = Embedding.cosine(v, canonicalVectors.get(c));
            if (score > bestScore) {
                bestScore = score;
                best = c;
            }
        }
        return best != null && bestScore >= threshold ? best : name;
    }

    /**
     * Scores a resolution against the corpus's declared entity identities.
     *
     * <p>A <b>merge error</b> is two surface forms of different entities placed
     * in one cluster. A <b>split error</b> is two surface forms of one entity
     * placed in different clusters. They are counted over pairs, not over
     * clusters, because pair counts compose: a cluster error affects every pair
     * it touches, and it is the pairs that become graph edges.
     */
    public static Quality score(Map<String, String> assignment, List<Corpus.Ent> truth) {
        Map<String, String> gold = new LinkedHashMap<>();
        for (Corpus.Ent e : truth) {
            for (String v : e.variants()) {
                gold.put(v, e.canonical());
            }
        }
        List<String> surfaces = new ArrayList<>(new TreeSet<>(assignment.keySet()));
        int mergeErrors = 0;
        int splitErrors = 0;
        int sameGoldPairs = 0;
        int diffGoldPairs = 0;
        for (int i = 0; i < surfaces.size(); i++) {
            for (int j = i + 1; j < surfaces.size(); j++) {
                String a = surfaces.get(i);
                String b = surfaces.get(j);
                String ga = gold.get(a);
                String gb = gold.get(b);
                if (ga == null || gb == null) {
                    continue;
                }
                boolean sameGold = ga.equals(gb);
                boolean samePredicted = assignment.get(a).equals(assignment.get(b));
                if (sameGold) {
                    sameGoldPairs++;
                    if (!samePredicted) {
                        splitErrors++;
                    }
                } else {
                    diffGoldPairs++;
                    if (samePredicted) {
                        mergeErrors++;
                    }
                }
            }
        }
        return new Quality(mergeErrors, splitErrors, sameGoldPairs, diffGoldPairs);
    }

    public record Quality(int mergeErrors, int splitErrors, int sameGoldPairs, int diffGoldPairs) {

        /** Of the pairs that should be together, how many are. */
        public double pairRecall() {
            return sameGoldPairs == 0 ? 1.0 : (sameGoldPairs - splitErrors) / (double) sameGoldPairs;
        }

        /** Of the pairs that should be apart, how many are. */
        public double pairSpecificity() {
            return diffGoldPairs == 0 ? 1.0 : (diffGoldPairs - mergeErrors) / (double) diffGoldPairs;
        }

        public boolean perfect() {
            return mergeErrors == 0 && splitErrors == 0;
        }
    }
}
