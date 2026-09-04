package dev.hybrid;

import java.util.*;
import java.util.function.UnaryOperator;

/**
 * Builds the systems under comparison and the plans they execute.
 *
 * <p>Three systems, differing only in which graph the shared {@link Executor}
 * is handed:
 *
 * <ul>
 *   <li><b>vector</b> -- the subgraph induced by the top-k retrieved documents</li>
 *   <li><b>graph</b> -- the full extracted graph</li>
 *   <li><b>hybrid</b> -- the full graph, but with node identity decided by the
 *       embedding rather than by exact string match, which is what a real
 *       pipeline over messy filings must do</li>
 * </ul>
 */
public final class Systems {

    private Systems() {}

    /** The graph as extracted with perfect entity resolution -- the ceiling. */
    public static Graph goldGraph() {
        Graph g = new Graph();
        for (Corpus.Doc d : Corpus.factual()) {
            g.add(new Graph.Edge(d.subject(), d.relation(), d.object(), d.id()));
        }
        return g;
    }

    /** The subgraph induced by a set of document ids. */
    public static Graph subgraph(Collection<Integer> docIds) {
        Graph g = new Graph();
        Set<Integer> keep = new HashSet<>(docIds);
        for (Corpus.Doc d : Corpus.factual()) {
            if (keep.contains(d.id())) {
                g.add(new Graph.Edge(d.subject(), d.relation(), d.object(), d.id()));
            }
        }
        return g;
    }

    /**
     * The graph as it would actually be built: entity names taken from the text
     * as written, then clustered by the resolver.
     *
     * <p>Every node is a resolver output, so a merge error fabricates edges
     * between things that were never connected and a split error disconnects
     * things that were. Section 5 measures both.
     */
    public static Graph resolvedGraph(Resolver resolver) {
        Graph g = new Graph();
        for (Corpus.Doc d : Corpus.factual()) {
            g.add(new Graph.Edge(resolver.canonical(surfaceOf(d.subject(), d.text())),
                    d.relation(),
                    resolver.canonical(surfaceOf(d.object(), d.text())),
                    d.id()));
        }
        return g;
    }

    /**
     * The surface form of an entity as it appears in a document.
     *
     * <p>Real extraction reads the string out of the sentence. Here the corpus
     * declares the canonical entity for each triple, so the surface form is
     * recovered by finding which of that entity's declared variants the sentence
     * actually contains. This keeps the extraction step honest -- the graph is
     * built from what the text says, not from the answer key -- without adding a
     * named-entity recogniser whose errors would confound the measurement.
     */
    public static String surfaceOf(String canonical, String text) {
        String best = canonical;
        int bestLength = -1;
        for (Corpus.Ent e : Corpus.entities()) {
            if (!e.canonical().equals(canonical)) {
                continue;
            }
            for (String v : e.variants()) {
                if (text.toLowerCase(Locale.ROOT).contains(v.toLowerCase(Locale.ROOT))
                        && v.length() > bestLength) {
                    best = v;
                    bestLength = v.length();
                }
            }
        }
        return best;
    }

    /** Every surface form that appears anywhere in the factual documents. */
    public static List<String> surfaceForms() {
        Set<String> out = new TreeSet<>();
        for (Corpus.Doc d : Corpus.factual()) {
            out.add(surfaceOf(d.subject(), d.text()));
            out.add(surfaceOf(d.object(), d.text()));
        }
        return List.copyOf(out);
    }

    // ------------------------------------------------------------------ plans

    private static final Set<String> CONTROL = Set.of("OWNED_BY", "SUBSIDIARY_OF");

    public static Map<String, Plan> plans() {
        Map<String, Plan> p = new LinkedHashMap<>();
        p.put("Q1", Plan.of("Meridian Shipping Ltd", Plan.back("DIRECTOR_OF")));
        p.put("Q2", Plan.of("Baltic Freight AG", Plan.fwd("REGISTERED_IN")));
        p.put("Q3", Plan.of("Silverline Holdings", Plan.fwd("OWNED_BY")));
        p.put("Q4", Plan.of("Pemberton Metals", Plan.back("SUPPLIES")));
        p.put("Q5", Plan.of("Viktor Anisimov", Plan.back("OWNED_BY"))
                .withGuard(new Plan.Guard(Set.of(), 0, "REGISTERED_IN", "Panama")));
        p.put("Q6", Plan.of("Orion Chartering", Plan.back("DIRECTOR_OF")));
        p.put("Q7", Plan.of("Meridian Shipping Ltd", Plan.fwd("SUBSIDIARY_OF"),
                Plan.fwd("SUBSIDIARY_OF")));
        p.put("Q8", Plan.of("Pemberton Metals", Plan.back("SUPPLIES"), Plan.fwd("SUBSIDIARY_OF")));
        p.put("Q9", Plan.of("Northwind Logistics", Plan.fwd("SUBSIDIARY_OF"),
                Plan.fwd("REGISTERED_IN")));
        p.put("Q10", Plan.of("Meridian Shipping Ltd", Plan.fwd("SUBSIDIARY_OF"),
                Plan.fwd("SUBSIDIARY_OF"), Plan.fwd("OWNED_BY")));
        p.put("Q11", Plan.of("Pemberton Metals", Plan.back("SUPPLIES"), Plan.fwd("SUBSIDIARY_OF"),
                Plan.fwd("OWNED_BY")));
        p.put("Q12", Plan.of("Ashford Components", Plan.back("SUPPLIES"))
                .withGuard(new Plan.Guard(CONTROL, 3, "LISTED_ON", "OFAC SDN List")));
        p.put("Q13", Plan.of("Panama", Plan.back("REGISTERED_IN")).counting());
        p.put("Q14", Plan.of("Elena Marchetti", new Plan.Hop("OWNED_BY", Graph.Direction.BACKWARD)));
        p.put("Q15", Plan.of("Ravenna Textiles", Plan.back("SUPPLIES"))
                .withGuard(new Plan.Guard(CONTROL, 3, "LISTED_ON", "OFAC SDN List")));
        p.put("Q16", Plan.of("Meridian Freight Services", Plan.fwd("OWNED_BY"))
                .withGuard(new Plan.Guard(Set.of(), 0, "OWNED_BY", "Silverline Holdings")));
        return Map.copyOf(p);
    }

    /**
     * Q14 asks for companies Marchetti owns <em>or</em> directs, which is a
     * union of two plans. Rather than adding disjunction to the plan language
     * for one question -- a feature the rest of the report would not exercise --
     * the union is taken here and named for what it is.
     */
    public static Executor.Result q14(Graph g, UnaryOperator<String> entityMap) {
        Executor.Result owns = Executor.run(
                Plan.of("Elena Marchetti", Plan.back("OWNED_BY")).remapEntities(entityMap), g);
        Executor.Result directs = Executor.run(
                Plan.of("Elena Marchetti", Plan.fwd("DIRECTOR_OF")).remapEntities(entityMap), g);
        Set<String> values = new TreeSet<>(owns.values());
        values.addAll(directs.values());
        List<Executor.Answer> answers = new ArrayList<>(owns.answers());
        answers.addAll(directs.answers());
        return new Executor.Result(Set.copyOf(values), List.copyOf(answers));
    }

    public static Executor.Result q14(Graph g) {
        return q14(g, UnaryOperator.identity());
    }

    public static Executor.Result answer(String questionId, Graph g) {
        return answer(questionId, g, UnaryOperator.identity());
    }

    /**
     * Runs a question against a graph whose nodes may be named by the resolver
     * rather than canonically, mapping the plan's own entity names the same way
     * so that a naming convention is never mistaken for a resolution error.
     */
    public static Executor.Result answer(String questionId, Graph g,
                                         UnaryOperator<String> entityMap) {
        if (questionId.equals("Q14")) {
            return q14(g, entityMap);
        }
        return Executor.run(plans().get(questionId).remapEntities(entityMap), g);
    }
}
