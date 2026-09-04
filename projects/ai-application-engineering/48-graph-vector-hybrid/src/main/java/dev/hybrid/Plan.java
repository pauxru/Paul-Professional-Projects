package dev.hybrid;

import java.util.*;

/**
 * A query plan: the structural half of a question, separated from the language.
 *
 * <p><b>Why plans are given rather than parsed.</b> Turning "which suppliers of
 * Ashford Components are ultimately controlled by a sanctioned person" into a
 * traversal is a language problem, and in a real system it is what the model
 * does. Solving it here would add a component whose errors are indistinguishable
 * from the effect being measured -- a wrong answer could mean the retrieval
 * substrate could not support the question, or that the parser mangled it, and
 * the report could not tell you which.
 *
 * <p>So the plan is handed to both systems, identically and correctly. The
 * measurement is then sharply about one thing: <em>given a correct plan, can
 * this substrate execute it?</em> That framing is deliberately generous to the
 * retriever, which is the point -- a result that survives being generous to the
 * thing you are criticising is worth more than one that does not.
 *
 * @param start   canonical entity the traversal begins at
 * @param steps   the deterministic path
 * @param guard   optional reachability condition every candidate must satisfy
 * @param count   if true the answer is the size of the result set, not the set
 */
public record Plan(String start, List<Hop> steps, Guard guard, boolean count) {

    public record Hop(String relation, Graph.Direction direction) {}

    /**
     * A condition of the form "from here, some path of at most {@code maxHops}
     * edges drawn from {@code via} reaches an entity that has a {@code marker}
     * edge to {@code target}".
     *
     * <p>This is the shape of every interesting compliance question and it is
     * the shape no retriever can express, because it quantifies over paths that
     * no document describes.
     */
    public record Guard(Set<String> via, int maxHops, String marker, String target) {}

    public static Plan of(String start, Hop... steps) {
        return new Plan(start, List.of(steps), null, false);
    }

    public Plan withGuard(Guard g) {
        return new Plan(start, steps, g, count);
    }

    public Plan counting() {
        return new Plan(start, steps, guard, true);
    }

    public static Hop fwd(String relation) {
        return new Hop(relation, Graph.Direction.FORWARD);
    }

    public static Hop back(String relation) {
        return new Hop(relation, Graph.Direction.BACKWARD);
    }

    /** Edges the plan could traverse in the best case; used for reporting only. */
    public int structuralHops() {
        return steps.size() + (guard == null ? 0 : guard.maxHops());
    }

    /**
     * Rewrites every entity name the plan mentions through {@code f}.
     *
     * <p>Needed because a plan names entities canonically but a resolved graph
     * names them by whichever surface form claimed the cluster. Skipping this
     * would make the plan's start node absent from the graph and every answer
     * empty -- a failure that looks exactly like a resolution error while being
     * a harness bug, which is precisely why it is worth naming: <b>the query
     * entity passes through the same resolver as the corpus</b>, and a threshold
     * that is wrong for the corpus is wrong for the query in the same way.
     */
    public Plan remapEntities(java.util.function.UnaryOperator<String> f) {
        Guard g = guard == null ? null
                : new Guard(guard.via(), guard.maxHops(), guard.marker(), f.apply(guard.target()));
        return new Plan(f.apply(start), steps, g, count);
    }
}
