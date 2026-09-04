package dev.hybrid;

import java.util.*;

/**
 * Executes a {@link Plan} against a {@link Graph}, returning answers with the
 * provenance chain that produced each one.
 *
 * <p>There is exactly one executor, used by every system in the comparison. The
 * vector system and the graph system differ in <em>which graph they are given</em>
 * and in nothing else. The retriever's graph is the subgraph induced by its
 * top-k documents; the graph system's is the whole thing.
 *
 * <p>That equivalence is the experimental design. It makes the retriever's
 * reader perfect -- it never hallucinates, never misreads, and composes facts
 * flawlessly across every passage it was handed. Real readers do none of those
 * things. So every number reported for the vector system is an <b>upper bound
 * on what any RAG pipeline over this corpus could achieve</b>, and the failures
 * measured are floors, not artefacts of a weak baseline.
 */
public final class Executor {

    public record Answer(String value, Graph.Path path) {}

    public record Result(Set<String> values, List<Answer> answers) {

        public boolean empty() {
            return values.isEmpty();
        }

        /** Every document id that any answer's provenance depends on. */
        public Set<Integer> provenance() {
            Set<Integer> out = new TreeSet<>();
            for (Answer a : answers) {
                out.addAll(a.path().provenance());
            }
            return out;
        }
    }

    private Executor() {}

    public static Result run(Plan plan, Graph graph) {
        List<Graph.Path> frontier = new ArrayList<>();
        frontier.add(new Graph.Path(plan.start(), List.of()));

        for (Plan.Hop hop : plan.steps()) {
            List<Graph.Path> next = new ArrayList<>();
            for (Graph.Path p : frontier) {
                for (Graph.Path reached : graph.reach(p.target(), Set.of(hop.relation()), 1,
                        hop.direction())) {
                    List<Graph.Step> steps = new ArrayList<>(p.steps());
                    steps.addAll(reached.steps());
                    next.add(new Graph.Path(reached.target(), List.copyOf(steps)));
                }
            }
            frontier = next;
        }

        List<Answer> answers = new ArrayList<>();
        Set<String> values = new TreeSet<>();
        for (Graph.Path p : frontier) {
            if (plan.guard() != null && !satisfies(p.target(), plan.guard(), graph)) {
                continue;
            }
            Graph.Path full = p;
            if (plan.guard() != null) {
                // Splice the guard's own path onto the answer, because a
                // compliance answer that cannot show why the entity is
                // implicated is not an answer anyone can act on.
                Graph.Path witness = witness(p.target(), plan.guard(), graph);
                if (witness != null) {
                    List<Graph.Step> steps = new ArrayList<>(p.steps());
                    steps.addAll(witness.steps());
                    full = new Graph.Path(p.target(), List.copyOf(steps));
                }
            }
            if (values.add(p.target())) {
                answers.add(new Answer(p.target(), full));
            }
        }

        if (plan.count()) {
            return new Result(Set.of(String.valueOf(values.size())), List.copyOf(answers));
        }
        return new Result(Set.copyOf(values), List.copyOf(answers));
    }

    private static boolean satisfies(String node, Plan.Guard g, Graph graph) {
        return witness(node, g, graph) != null;
    }

    private static Graph.Path witness(String node, Plan.Guard g, Graph graph) {
        List<Graph.Path> reachable = new ArrayList<>();
        reachable.add(new Graph.Path(node, List.of()));
        reachable.addAll(graph.reach(node, g.via(), g.maxHops(), Graph.Direction.EITHER));
        for (Graph.Path p : reachable) {
            for (Graph.Edge e : graph.edges()) {
                if (e.relation().equals(g.marker()) && e.from().equals(p.target())
                        && e.to().equals(g.target())) {
                    List<Graph.Step> steps = new ArrayList<>(p.steps());
                    steps.add(new Graph.Step(e, true));
                    return new Graph.Path(g.target(), List.copyOf(steps));
                }
            }
        }
        return null;
    }
}
