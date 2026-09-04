package dev.hybrid;

import java.util.*;

/**
 * A property graph with provenance on every edge.
 *
 * <p>The provenance is not a nicety. A traversal result is only usable if the
 * chain that produced it can be shown to a human who will act on it, and each
 * link in that chain has to point back at the document that asserts it.
 * Section 6 measures what that buys: the graph's answers are checkable and the
 * retriever's are not, independent of whether either is correct.
 *
 * <p>Nodes are canonical entity names as produced by {@link Resolver}. That is
 * the load-bearing coupling in this project: <em>the graph is only as good as
 * the resolution</em>, and section 5 measures exactly how a small resolution
 * error becomes a large traversal error.
 */
public final class Graph {

    /** A directed, labelled edge with the id of the document that asserts it. */
    public record Edge(String from, String relation, String to, int docId) {}

    /** One step of an answer path: the edge taken, in the direction taken. */
    public record Step(Edge edge, boolean forward) {
        public String from() {
            return forward ? edge.from() : edge.to();
        }

        public String to() {
            return forward ? edge.to() : edge.from();
        }

        @Override
        public String toString() {
            return from() + " --" + edge.relation() + (forward ? "-> " : " (inv)-> ") + to();
        }
    }

    /** A traversal result: the entity reached, and how. */
    public record Path(String target, List<Step> steps) {
        public int hops() {
            return steps.size();
        }

        public List<Integer> provenance() {
            return steps.stream().map(s -> s.edge().docId()).toList();
        }

        public String render() {
            if (steps.isEmpty()) {
                return target;
            }
            StringBuilder sb = new StringBuilder(steps.get(0).from());
            for (Step s : steps) {
                sb.append(" --").append(s.edge().relation()).append(s.forward() ? "--> " : "--(inv)--> ")
                        .append(s.to());
            }
            return sb.toString();
        }
    }

    private final Map<String, List<Edge>> outgoing = new LinkedHashMap<>();
    private final Map<String, List<Edge>> incoming = new LinkedHashMap<>();
    private final List<Edge> edges = new ArrayList<>();

    public void add(Edge e) {
        edges.add(e);
        outgoing.computeIfAbsent(e.from(), k -> new ArrayList<>()).add(e);
        incoming.computeIfAbsent(e.to(), k -> new ArrayList<>()).add(e);
    }

    public List<Edge> edges() {
        return List.copyOf(edges);
    }

    public Set<String> nodes() {
        Set<String> n = new TreeSet<>(outgoing.keySet());
        n.addAll(incoming.keySet());
        return n;
    }

    public boolean hasEdge(String from, String relation, String to) {
        return outgoing.getOrDefault(from, List.of()).stream()
                .anyMatch(e -> e.relation().equals(relation) && e.to().equals(to));
    }

    /**
     * Every entity reachable from {@code start} in at most {@code maxHops},
     * following only the given relations, with the shortest path to each.
     *
     * @param direction FORWARD follows edges as asserted, BACKWARD follows them
     *                  inverted, EITHER does both. The distinction matters:
     *                  "who owns X" and "what does X own" are the same edges
     *                  read in opposite directions, and a traversal that ignores
     *                  direction answers neither question correctly.
     */
    public List<Path> reach(String start, Set<String> relations, int maxHops, Direction direction) {
        Map<String, Path> best = new LinkedHashMap<>();
        Deque<Path> queue = new ArrayDeque<>();
        queue.add(new Path(start, List.of()));
        best.put(start, new Path(start, List.of()));

        while (!queue.isEmpty()) {
            Path p = queue.poll();
            if (p.hops() >= maxHops) {
                continue;
            }
            for (Step s : stepsFrom(p.target(), relations, direction)) {
                String next = s.to();
                // Cycles are normal in ownership graphs -- cross-holdings are a
                // structuring technique, not a data error. Keeping the first
                // (shortest) path to each node terminates and also gives the
                // most defensible provenance chain.
                if (best.containsKey(next)) {
                    continue;
                }
                List<Step> extended = new ArrayList<>(p.steps());
                extended.add(s);
                Path np = new Path(next, List.copyOf(extended));
                best.put(next, np);
                queue.add(np);
            }
        }
        best.remove(start);
        return best.values().stream().sorted(Comparator.comparingInt(Path::hops)
                .thenComparing(Path::target)).toList();
    }

    private List<Step> stepsFrom(String node, Set<String> relations, Direction direction) {
        List<Step> out = new ArrayList<>();
        if (direction != Direction.BACKWARD) {
            for (Edge e : outgoing.getOrDefault(node, List.of())) {
                if (relations.isEmpty() || relations.contains(e.relation())) {
                    out.add(new Step(e, true));
                }
            }
        }
        if (direction != Direction.FORWARD) {
            for (Edge e : incoming.getOrDefault(node, List.of())) {
                if (relations.isEmpty() || relations.contains(e.relation())) {
                    out.add(new Step(e, false));
                }
            }
        }
        return out;
    }

    public enum Direction { FORWARD, BACKWARD, EITHER }
}
