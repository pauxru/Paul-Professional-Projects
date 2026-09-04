package dev.hybrid;

import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

import java.util.*;

import static org.junit.jupiter.api.Assertions.*;

class GraphTest {

    private static Graph chain() {
        Graph g = new Graph();
        g.add(new Graph.Edge("A", "OWNED_BY", "B", 1));
        g.add(new Graph.Edge("B", "OWNED_BY", "C", 2));
        g.add(new Graph.Edge("C", "OWNED_BY", "D", 3));
        return g;
    }

    @Test
    @DisplayName("nodes are the union of every edge's endpoints")
    void nodesAreEndpoints() {
        assertEquals(Set.of("A", "B", "C", "D"), chain().nodes());
    }

    @Test
    @DisplayName("a forward traversal follows edges as asserted")
    void forwardFollowsAssertion() {
        List<Graph.Path> paths = chain().reach("A", Set.of("OWNED_BY"), 3,
                Graph.Direction.FORWARD);
        assertEquals(List.of("B", "C", "D"), paths.stream().map(Graph.Path::target).toList());
    }

    @Test
    @DisplayName("a backward traversal answers the opposite question from the same edges")
    void backwardInvertsTheQuestion() {
        List<Graph.Path> paths = chain().reach("D", Set.of("OWNED_BY"), 3,
                Graph.Direction.BACKWARD);
        assertEquals(List.of("C", "B", "A"), paths.stream().map(Graph.Path::target).toList());
    }

    @Test
    @DisplayName("direction is not decorative: forward from D reaches nothing")
    void directionIsLoadBearing() {
        assertTrue(chain().reach("D", Set.of("OWNED_BY"), 3, Graph.Direction.FORWARD).isEmpty());
    }

    @Test
    @DisplayName("maxHops truncates the traversal")
    void maxHopsBounds() {
        assertEquals(1, chain().reach("A", Set.of("OWNED_BY"), 1, Graph.Direction.FORWARD).size());
        assertEquals(2, chain().reach("A", Set.of("OWNED_BY"), 2, Graph.Direction.FORWARD).size());
    }

    @Test
    @DisplayName("maxHops of zero reaches nothing")
    void zeroHopsReachesNothing() {
        assertTrue(chain().reach("A", Set.of("OWNED_BY"), 0, Graph.Direction.FORWARD).isEmpty());
    }

    @Test
    @DisplayName("the relation filter excludes edges of other types")
    void relationFilterExcludes() {
        Graph g = chain();
        g.add(new Graph.Edge("A", "SUPPLIES", "Z", 4));
        List<String> owned = g.reach("A", Set.of("OWNED_BY"), 3, Graph.Direction.FORWARD)
                .stream().map(Graph.Path::target).toList();
        assertFalse(owned.contains("Z"));
    }

    @Test
    @DisplayName("an empty relation set means any relation")
    void emptyRelationSetMeansAny() {
        Graph g = chain();
        g.add(new Graph.Edge("A", "SUPPLIES", "Z", 4));
        List<String> any = g.reach("A", Set.of(), 1, Graph.Direction.FORWARD)
                .stream().map(Graph.Path::target).toList();
        assertTrue(any.contains("Z"));
        assertTrue(any.contains("B"));
    }

    @Test
    @DisplayName("the start node is never returned as its own answer")
    void startIsExcluded() {
        assertFalse(chain().reach("A", Set.of("OWNED_BY"), 3, Graph.Direction.FORWARD)
                .stream().anyMatch(p -> p.target().equals("A")));
    }

    @Test
    @DisplayName("a cycle terminates -- cross-holdings are structuring, not a data error")
    void cyclesTerminate() {
        Graph g = new Graph();
        g.add(new Graph.Edge("A", "OWNED_BY", "B", 1));
        g.add(new Graph.Edge("B", "OWNED_BY", "C", 2));
        g.add(new Graph.Edge("C", "OWNED_BY", "A", 3));
        List<Graph.Path> paths = g.reach("A", Set.of("OWNED_BY"), 10, Graph.Direction.FORWARD);
        assertEquals(Set.of("B", "C"), paths.stream().map(Graph.Path::target)
                .collect(java.util.stream.Collectors.toCollection(TreeSet::new)));
    }

    @Test
    @DisplayName("a self-loop terminates")
    void selfLoopTerminates() {
        Graph g = new Graph();
        g.add(new Graph.Edge("A", "OWNED_BY", "A", 1));
        assertTrue(g.reach("A", Set.of("OWNED_BY"), 5, Graph.Direction.FORWARD).isEmpty());
    }

    @Test
    @DisplayName("the shortest path to each node is the one kept")
    void shortestPathWins() {
        Graph g = chain();
        g.add(new Graph.Edge("A", "OWNED_BY", "D", 9));
        Graph.Path toD = g.reach("A", Set.of("OWNED_BY"), 5, Graph.Direction.FORWARD).stream()
                .filter(p -> p.target().equals("D")).findFirst().orElseThrow();
        assertEquals(1, toD.hops());
        assertEquals(List.of(9), toD.provenance());
    }

    @Test
    @DisplayName("results are ordered by hop count then name, so the report is stable")
    void resultsAreOrdered() {
        Graph g = chain();
        g.add(new Graph.Edge("A", "OWNED_BY", "Z", 9));
        List<Graph.Path> paths = g.reach("A", Set.of("OWNED_BY"), 5, Graph.Direction.FORWARD);
        for (int i = 1; i < paths.size(); i++) {
            Graph.Path prev = paths.get(i - 1);
            Graph.Path cur = paths.get(i);
            assertTrue(prev.hops() < cur.hops()
                            || (prev.hops() == cur.hops()
                            && prev.target().compareTo(cur.target()) <= 0),
                    "unordered at " + i);
        }
    }

    @Test
    @DisplayName("provenance lists one document per step, in path order")
    void provenanceFollowsThePath() {
        Graph.Path toD = chain().reach("A", Set.of("OWNED_BY"), 3, Graph.Direction.FORWARD)
                .stream().filter(p -> p.target().equals("D")).findFirst().orElseThrow();
        assertEquals(List.of(1, 2, 3), toD.provenance());
        assertEquals(toD.hops(), toD.provenance().size());
    }

    @Test
    @DisplayName("a rendered path names every intermediate entity")
    void renderNamesTheChain() {
        Graph.Path toD = chain().reach("A", Set.of("OWNED_BY"), 3, Graph.Direction.FORWARD)
                .stream().filter(p -> p.target().equals("D")).findFirst().orElseThrow();
        String rendered = toD.render();
        for (String node : List.of("A", "B", "C", "D")) {
            assertTrue(rendered.contains(node), "render omits " + node + ": " + rendered);
        }
    }

    @Test
    @DisplayName("an inverted step renders as inverted, so a reader is not misled about direction")
    void renderMarksInversion() {
        Graph.Path toA = chain().reach("D", Set.of("OWNED_BY"), 3, Graph.Direction.BACKWARD)
                .stream().filter(p -> p.target().equals("A")).findFirst().orElseThrow();
        assertTrue(toA.render().contains("(inv)"), toA.render());
    }

    @Test
    @DisplayName("hasEdge is direction-sensitive")
    void hasEdgeIsDirected() {
        Graph g = chain();
        assertTrue(g.hasEdge("A", "OWNED_BY", "B"));
        assertFalse(g.hasEdge("B", "OWNED_BY", "A"));
    }

    @Test
    @DisplayName("a traversal from an unknown node returns nothing rather than failing")
    void unknownStartIsEmpty() {
        assertTrue(chain().reach("nobody", Set.of("OWNED_BY"), 3, Graph.Direction.FORWARD)
                .isEmpty());
    }

    @Test
    @DisplayName("EITHER reaches nodes in both directions")
    void eitherIsTheUnion() {
        Set<String> either = new TreeSet<>();
        chain().reach("B", Set.of("OWNED_BY"), 2, Graph.Direction.EITHER)
                .forEach(p -> either.add(p.target()));
        assertEquals(Set.of("A", "C", "D"), either);
    }

    @Test
    @DisplayName("every edge in the real graph cites a document that asserts that exact relation")
    void everyEdgeIsGroundedInItsDocument() {
        for (Graph.Edge e : Systems.goldGraph().edges()) {
            Corpus.Doc d = Corpus.doc(e.docId());
            assertEquals(d.relation(), e.relation(), "edge from d" + e.docId());
            assertEquals(d.subject(), e.from(), "edge from d" + e.docId());
            assertEquals(d.object(), e.to(), "edge from d" + e.docId());
        }
    }
}
