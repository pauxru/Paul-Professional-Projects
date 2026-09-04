package dev.hybrid;

import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

import java.util.*;

import static org.junit.jupiter.api.Assertions.*;

class ExecutorTest {

    private static Graph ownership() {
        Graph g = new Graph();
        g.add(new Graph.Edge("Supplier", "SUPPLIES", "Buyer", 1));
        g.add(new Graph.Edge("Supplier", "OWNED_BY", "Parent", 2));
        g.add(new Graph.Edge("Parent", "OWNED_BY", "Person", 3));
        g.add(new Graph.Edge("Person", "LISTED_ON", "OFAC SDN List", 4));
        g.add(new Graph.Edge("Clean", "SUPPLIES", "Buyer", 5));
        g.add(new Graph.Edge("Clean", "OWNED_BY", "Nobody", 6));
        return g;
    }

    @Test
    @DisplayName("a one-hop plan returns the neighbour")
    void oneHop() {
        Executor.Result r = Executor.run(Plan.of("Buyer", Plan.back("SUPPLIES")), ownership());
        assertEquals(Set.of("Supplier", "Clean"), r.values());
    }

    @Test
    @DisplayName("a two-hop plan composes")
    void twoHops() {
        Executor.Result r = Executor.run(
                Plan.of("Supplier", Plan.fwd("OWNED_BY"), Plan.fwd("OWNED_BY")), ownership());
        assertEquals(Set.of("Person"), r.values());
    }

    @Test
    @DisplayName("a guard filters candidates by reachability, which is what no retriever can do")
    void guardFilters() {
        Plan p = Plan.of("Buyer", Plan.back("SUPPLIES"))
                .withGuard(new Plan.Guard(Set.of("OWNED_BY"), 3, "LISTED_ON", "OFAC SDN List"));
        assertEquals(Set.of("Supplier"), Executor.run(p, ownership()).values());
    }

    @Test
    @DisplayName("a guard that nothing satisfies yields nothing rather than everything")
    void unsatisfiableGuardYieldsNothing() {
        Plan p = Plan.of("Buyer", Plan.back("SUPPLIES"))
                .withGuard(new Plan.Guard(Set.of("OWNED_BY"), 3, "LISTED_ON", "EU Consolidated"));
        assertTrue(Executor.run(p, ownership()).empty());
    }

    @Test
    @DisplayName("a guard's hop budget is enforced")
    void guardHopBudgetIsEnforced() {
        Plan tooShort = Plan.of("Buyer", Plan.back("SUPPLIES"))
                .withGuard(new Plan.Guard(Set.of("OWNED_BY"), 1, "LISTED_ON", "OFAC SDN List"));
        assertTrue(Executor.run(tooShort, ownership()).empty());
        Plan longEnough = Plan.of("Buyer", Plan.back("SUPPLIES"))
                .withGuard(new Plan.Guard(Set.of("OWNED_BY"), 2, "LISTED_ON", "OFAC SDN List"));
        assertEquals(Set.of("Supplier"), Executor.run(longEnough, ownership()).values());
    }

    @Test
    @DisplayName("the guard's own witness path is spliced into the provenance")
    void guardContributesProvenance() {
        Plan p = Plan.of("Buyer", Plan.back("SUPPLIES"))
                .withGuard(new Plan.Guard(Set.of("OWNED_BY"), 3, "LISTED_ON", "OFAC SDN List"));
        Executor.Result r = Executor.run(p, ownership());
        assertTrue(r.provenance().containsAll(Set.of(1, 2, 3, 4)),
                "an answer that cannot show why the entity is implicated is not actionable: "
                        + r.provenance());
    }

    @Test
    @DisplayName("a counting plan returns the size, not the members")
    void countingReturnsSize() {
        Executor.Result r = Executor.run(
                Plan.of("Buyer", Plan.back("SUPPLIES")).counting(), ownership());
        assertEquals(Set.of("2"), r.values());
    }

    @Test
    @DisplayName("counting after a guard counts only what survived it")
    void countingRespectsTheGuard() {
        Plan p = Plan.of("Buyer", Plan.back("SUPPLIES"))
                .withGuard(new Plan.Guard(Set.of("OWNED_BY"), 3, "LISTED_ON", "OFAC SDN List"))
                .counting();
        assertEquals(Set.of("1"), Executor.run(p, ownership()).values());
    }

    @Test
    @DisplayName("a plan over an empty graph returns nothing and does not throw")
    void emptyGraphIsSafe() {
        assertTrue(Executor.run(Plan.of("Buyer", Plan.back("SUPPLIES")), new Graph()).empty());
    }

    @Test
    @DisplayName("every answer carries a path whose last node is that answer")
    void answersCarryTheirOwnPath() {
        Executor.Result r = Executor.run(Plan.of("Buyer", Plan.back("SUPPLIES")), ownership());
        for (Executor.Answer a : r.answers()) {
            assertFalse(a.path().steps().isEmpty(), a.value() + " has no path");
            assertTrue(a.path().render().contains(a.value()));
        }
    }

    @Test
    @DisplayName("values and answers agree, so provenance is never reported for a dropped answer")
    void valuesAndAnswersAgree() {
        for (Corpus.Question q : Corpus.questions()) {
            if (q.kind() == Corpus.Kind.OPEN || q.id().equals("Q13")) {
                continue;
            }
            Executor.Result r = Systems.answer(q.id(), Systems.goldGraph());
            Set<String> fromAnswers = new TreeSet<>();
            r.answers().forEach(a -> fromAnswers.add(a.value()));
            assertEquals(r.values(), fromAnswers, q.id() + " reports values its answers do not");
        }
    }

    @Test
    @DisplayName("remapEntities rewrites the start node")
    void remapRewritesStart() {
        Plan p = Plan.of("Buyer", Plan.back("SUPPLIES"));
        assertEquals("BUYER", p.remapEntities(String::toUpperCase).start());
    }

    @Test
    @DisplayName("remapEntities rewrites the guard target too, or the guard silently never fires")
    void remapRewritesGuardTarget() {
        Plan p = Plan.of("Buyer", Plan.back("SUPPLIES"))
                .withGuard(new Plan.Guard(Set.of("OWNED_BY"), 3, "LISTED_ON", "OFAC SDN List"));
        assertEquals("OFAC SDN LIST", p.remapEntities(String::toUpperCase).guard().target());
    }

    @Test
    @DisplayName("remapEntities leaves relations and hop budgets alone")
    void remapDoesNotTouchRelations() {
        Plan p = Plan.of("Buyer", Plan.back("SUPPLIES"))
                .withGuard(new Plan.Guard(Set.of("OWNED_BY"), 3, "LISTED_ON", "OFAC SDN List"))
                .counting();
        Plan m = p.remapEntities(String::toUpperCase);
        assertEquals(p.steps(), m.steps());
        assertEquals(p.guard().via(), m.guard().via());
        assertEquals(p.guard().marker(), m.guard().marker());
        assertEquals(p.guard().maxHops(), m.guard().maxHops());
        assertTrue(m.count());
    }

    @Test
    @DisplayName("the identity remap changes no answer anywhere")
    void identityRemapIsANoOp() {
        Graph gold = Systems.goldGraph();
        for (Corpus.Question q : Corpus.questions()) {
            if (q.kind() == Corpus.Kind.OPEN) {
                continue;
            }
            assertEquals(Systems.answer(q.id(), gold).values(),
                    Systems.answer(q.id(), gold, java.util.function.UnaryOperator.identity())
                            .values(), q.id());
        }
    }

    @Test
    @DisplayName("the same plan on the same graph gives the same answer every time")
    void executionIsDeterministic() {
        Graph gold = Systems.goldGraph();
        for (int i = 0; i < 5; i++) {
            assertEquals(Systems.answer("Q12", gold).values(),
                    Systems.answer("Q12", gold).values());
        }
    }

    @Test
    @DisplayName("a subgraph missing a middle link breaks the chain, which is section 3's mechanism")
    void aMissingMiddleLinkBreaksTheChain() {
        List<Integer> withoutMiddle = new ArrayList<>(List.of(0, 1, 3, 4));
        assertNotEquals(Set.of("Meridian Shipping Ltd"),
                Systems.answer("Q12", Systems.subgraph(withoutMiddle)).values());
    }
}
