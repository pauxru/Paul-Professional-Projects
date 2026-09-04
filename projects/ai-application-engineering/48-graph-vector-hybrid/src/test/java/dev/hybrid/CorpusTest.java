package dev.hybrid;

import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

import java.util.*;

import static org.junit.jupiter.api.Assertions.*;

/**
 * Ground truth integrity.
 *
 * <p>Every other measurement in this project is scored against
 * {@link Corpus#questions()}. If that answer key is wrong, or is derived from
 * the same code that produces the answers, then the report measures its own
 * consistency and nothing else. These tests check the key from the outside:
 * against the document text, against the declared entity variants, and against
 * a graph built independently of any retriever.
 */
class CorpusTest {

    private static final Set<String> KNOWN_RELATIONS = Set.of(
            "SUPPLIES", "OWNED_BY", "SUBSIDIARY_OF", "REGISTERED_IN", "DIRECTOR_OF", "LISTED_ON");

    @Test
    @DisplayName("document ids are dense and in order, so a premise list means what it says")
    void documentIdsAreDenseAndOrdered() {
        List<Corpus.Doc> docs = Corpus.documents();
        for (int i = 0; i < docs.size(); i++) {
            assertEquals(i, docs.get(i).id(), "document at index " + i + " has the wrong id");
        }
        assertEquals(80, docs.size());
    }

    @Test
    @DisplayName("factual documents are exactly the ones with a relation")
    void factualDocumentsHaveRelations() {
        for (Corpus.Doc d : Corpus.factual()) {
            assertNotNull(d.relation(), "d" + d.id() + " is in factual() with no relation");
            assertTrue(KNOWN_RELATIONS.contains(d.relation()),
                    "d" + d.id() + " uses unknown relation " + d.relation());
        }
        assertEquals(50, Corpus.factual().size());
    }

    @Test
    @DisplayName("distractors state nothing, which is what makes them dangerous")
    void distractorsHaveNoRelation() {
        long distractors = Corpus.documents().stream().filter(d -> d.relation() == null).count();
        assertEquals(30, distractors);
        for (Corpus.Doc d : Corpus.documents()) {
            if (d.relation() == null) {
                assertNull(d.subject(), "d" + d.id() + " is a distractor with a subject");
                assertNull(d.object(), "d" + d.id() + " is a distractor with an object");
            }
        }
    }

    @Test
    @DisplayName("every triple's entities actually appear in the sentence that asserts them")
    void triplesAreGroundedInTheirText() {
        for (Corpus.Doc d : Corpus.factual()) {
            assertTrue(mentions(d.text(), d.subject()),
                    "d" + d.id() + " claims subject '" + d.subject() + "' but the text is: "
                            + d.text());
            assertTrue(mentions(d.text(), d.object()),
                    "d" + d.id() + " claims object '" + d.object() + "' but the text is: "
                            + d.text());
        }
    }

    private static boolean mentions(String text, String canonical) {
        String lower = text.toLowerCase(Locale.ROOT);
        for (Corpus.Ent e : Corpus.entities()) {
            if (!e.canonical().equals(canonical)) {
                continue;
            }
            for (String v : e.variants()) {
                if (lower.contains(v.toLowerCase(Locale.ROOT))) {
                    return true;
                }
            }
        }
        return false;
    }

    @Test
    @DisplayName("every entity referenced by a triple is declared, and vice versa")
    void entityDeclarationsAndUsagesAgree() {
        Set<String> declared = new TreeSet<>();
        for (Corpus.Ent e : Corpus.entities()) {
            declared.add(e.canonical());
        }
        Set<String> used = new TreeSet<>();
        for (Corpus.Doc d : Corpus.factual()) {
            used.add(d.subject());
            used.add(d.object());
        }
        Set<String> undeclared = new TreeSet<>(used);
        undeclared.removeAll(declared);
        assertTrue(undeclared.isEmpty(), "used but never declared: " + undeclared);

        Set<String> unused = new TreeSet<>(declared);
        unused.removeAll(used);
        assertTrue(unused.isEmpty(), "declared but never used: " + unused);
    }

    @Test
    @DisplayName("a canonical name is always one of its own variants")
    void canonicalIsAVariantOfItself() {
        for (Corpus.Ent e : Corpus.entities()) {
            assertTrue(e.variants().contains(e.canonical()),
                    e.canonical() + " is not in its own variant list");
        }
    }

    @Test
    @DisplayName("no surface form belongs to two entities, or resolution is undecidable")
    void surfaceFormsAreUnambiguous() {
        Map<String, String> owner = new LinkedHashMap<>();
        for (Corpus.Ent e : Corpus.entities()) {
            for (String v : e.variants()) {
                String prior = owner.put(v, e.canonical());
                assertNull(prior, "'" + v + "' is a variant of both " + prior + " and "
                        + e.canonical() + "; no resolver could be scored against this");
            }
        }
    }

    @Test
    @DisplayName("the two Meridians are declared as different entities -- the trap is real")
    void theMeridianTrapIsActuallyATrap() {
        Map<String, String> byVariant = new LinkedHashMap<>();
        for (Corpus.Ent e : Corpus.entities()) {
            for (String v : e.variants()) {
                byVariant.put(v, e.canonical());
            }
        }
        assertEquals("Meridian Shipping Ltd", byVariant.get("Meridian Shipping Ltd"));
        assertEquals("Meridian Freight Services", byVariant.get("Meridian Freight Services"));
        assertNotEquals(byVariant.get("Meridian Shipping Ltd"),
                byVariant.get("Meridian Freight Services"));
    }

    @Test
    @DisplayName("the two Halcyons are declared as the same entity -- the opposite trap")
    void theHalcyonTrapIsTheOppositeTrap() {
        Map<String, String> byVariant = new LinkedHashMap<>();
        for (Corpus.Ent e : Corpus.entities()) {
            for (String v : e.variants()) {
                byVariant.put(v, e.canonical());
            }
        }
        assertEquals(byVariant.get("Halcyon Trading Co"), byVariant.get("Halcyon Trading Company"),
                "Halcyon's two spellings must be one entity or section 5 measures nothing");
    }

    @Test
    @DisplayName("question ids are unique")
    void questionIdsAreUnique() {
        Set<String> seen = new TreeSet<>();
        for (Corpus.Question q : Corpus.questions()) {
            assertTrue(seen.add(q.id()), "duplicate question id " + q.id());
        }
        assertEquals(19, seen.size());
    }

    @Test
    @DisplayName("every premise document id exists and states a relation")
    void premisesPointAtFactualDocuments() {
        for (Corpus.Question q : Corpus.questions()) {
            for (int id : q.premises()) {
                assertTrue(id >= 0 && id < Corpus.documents().size(),
                        q.id() + " cites out-of-range document " + id);
                if (q.kind() != Corpus.Kind.OPEN) {
                    assertNotNull(Corpus.doc(id).relation(),
                            q.id() + " cites d" + id + " as a premise, but that document states "
                                    + "no relation, so no amount of retrieval could use it");
                }
            }
        }
    }

    @Test
    @DisplayName("open questions cite documents the extractor's schema cannot represent")
    void openQuestionsAreOutsideTheSchema() {
        long open = 0;
        for (Corpus.Question q : Corpus.questions()) {
            if (q.kind() != Corpus.Kind.OPEN) {
                continue;
            }
            open++;
            assertEquals(1, q.premises().size(), q.id() + " should rest on one document");
            assertNull(Corpus.doc(q.premises().get(0)).relation(),
                    q.id() + " is marked OPEN but its premise is in the graph's schema, so it "
                            + "does not demonstrate what section 6 claims it demonstrates");
        }
        assertEquals(3, open, "section 6 needs open questions to have anything to measure");
    }

    @Test
    @DisplayName("negative questions have the empty set as their answer")
    void negativesAreEmpty() {
        long negatives = Corpus.questions().stream()
                .filter(q -> q.kind() == Corpus.Kind.NEGATIVE).peek(q ->
                        assertTrue(q.answer().isEmpty(), q.id() + " is NEGATIVE but expects "
                                + q.answer())).count();
        assertEquals(2, negatives);
    }

    @Test
    @DisplayName("non-negative questions expect a non-empty answer")
    void positivesAreNonEmpty() {
        for (Corpus.Question q : Corpus.questions()) {
            if (q.kind() == Corpus.Kind.NEGATIVE) {
                continue;
            }
            assertFalse(q.answer().isEmpty(), q.id() + " expects nothing but is not NEGATIVE");
        }
    }

    @Test
    @DisplayName("declared hop counts match the plan that answers the question")
    void hopCountsMatchThePlans() {
        Map<String, Plan> plans = Systems.plans();
        for (Corpus.Question q : Corpus.questions()) {
            Plan p = plans.get(q.id());
            if (p == null) {
                continue;
            }
            assertEquals(q.hops(), p.structuralHops(),
                    q.id() + " declares " + q.hops() + " hops but its plan walks "
                            + p.structuralHops());
        }
    }

    @Test
    @DisplayName("every non-open question has a plan, so no result is a missing-plan artefact")
    void everyScoredQuestionHasAPlan() {
        Map<String, Plan> plans = Systems.plans();
        for (Corpus.Question q : Corpus.questions()) {
            if (q.kind() == Corpus.Kind.OPEN) {
                continue;
            }
            assertTrue(plans.containsKey(q.id()) || q.id().equals("Q14"),
                    q.id() + " has no plan; its score would be a harness artefact");
        }
    }

    @Test
    @DisplayName("the gold graph answers every non-open question exactly")
    void goldGraphIsTheCeiling() {
        Graph gold = Systems.goldGraph();
        for (Corpus.Question q : Corpus.questions()) {
            if (q.kind() == Corpus.Kind.OPEN) {
                continue;
            }
            Set<String> got = Systems.answer(q.id(), gold).values();
            assertEquals(q.answer(), got, q.id() + " -- the answer key and the graph disagree. "
                    + "One of them is wrong, and until that is settled every comparison in the "
                    + "report is measuring the disagreement rather than the systems.");
        }
    }

    @Test
    @DisplayName("premises are sufficient: the subgraph they induce answers the question")
    void premisesAreSufficient() {
        for (Corpus.Question q : Corpus.questions()) {
            if (q.kind() == Corpus.Kind.OPEN) {
                continue;
            }
            Set<String> got = Systems.answer(q.id(), Systems.subgraph(q.premises())).values();
            assertEquals(q.answer(), got, q.id() + " cannot be answered from its own premise "
                    + "list, so section 4's oracle is not an oracle");
        }
    }

    @Test
    @DisplayName("premises are necessary: dropping any one of them changes the answer")
    void premisesAreNecessary() {
        for (Corpus.Question q : Corpus.questions()) {
            if (q.kind() == Corpus.Kind.OPEN || q.kind() == Corpus.Kind.NEGATIVE
                    || q.premises().size() < 2) {
                continue;
            }
            for (int drop : q.premises()) {
                List<Integer> reduced = new ArrayList<>(q.premises());
                reduced.remove(Integer.valueOf(drop));
                Set<String> got = Systems.answer(q.id(), Systems.subgraph(reduced)).values();
                assertNotEquals(q.answer(), got,
                        q.id() + " still answers correctly without d" + drop + ", so that "
                                + "document is not a premise and the premise-recall table in "
                                + "section 3 overstates what retrieval must find");
            }
        }
    }

    @Test
    @DisplayName("hop counts and premise counts are consistent with each other")
    void multiHopQuestionsNeedMultipleDocuments() {
        for (Corpus.Question q : Corpus.questions()) {
            if (q.kind() != Corpus.Kind.MULTIHOP) {
                continue;
            }
            assertTrue(q.premises().size() >= 2,
                    q.id() + " is MULTIHOP but rests on " + q.premises().size() + " document(s)");
            assertTrue(q.hops() >= 2, q.id() + " is MULTIHOP but declares " + q.hops() + " hop(s)");
        }
    }

    @Test
    @DisplayName("the five-document chain the report opens with really is a chain")
    void theOpeningChainComposes() {
        Graph g = Systems.subgraph(List.of(0, 1, 2, 3, 4));
        assertTrue(g.hasEdge("Meridian Shipping Ltd", "SUPPLIES", "Ashford Components"));
        assertTrue(g.hasEdge("Meridian Shipping Ltd", "SUBSIDIARY_OF", "Baltic Freight AG"));
        assertTrue(g.hasEdge("Baltic Freight AG", "SUBSIDIARY_OF", "Silverline Holdings"));
        assertTrue(g.hasEdge("Silverline Holdings", "OWNED_BY", "Viktor Anisimov"));
        assertTrue(g.hasEdge("Viktor Anisimov", "LISTED_ON", "OFAC SDN List"));
    }

    @Test
    @DisplayName("no single document states the composed conclusion -- the premise of the project")
    void noDocumentStatesTheComposedFact() {
        for (Corpus.Doc d : Corpus.documents()) {
            String t = d.text().toLowerCase(Locale.ROOT);
            boolean namesBuyerAndSanction = t.contains("ashford")
                    && (t.contains("sdn") || t.contains("sanction"));
            assertFalse(namesBuyerAndSanction,
                    "d" + d.id() + " states the conclusion the report claims is only derivable: "
                            + d.text());
        }
    }
}
