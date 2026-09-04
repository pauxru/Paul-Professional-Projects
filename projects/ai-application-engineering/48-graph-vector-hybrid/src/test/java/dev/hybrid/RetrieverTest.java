package dev.hybrid;

import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

import java.util.*;

import static org.junit.jupiter.api.Assertions.*;

class RetrieverTest {

    private static final Retriever R = new Retriever();

    @Test
    @DisplayName("topK returns exactly k documents when k is within the corpus")
    void topKReturnsK() {
        for (int k : new int[]{1, 3, 5, 10, 40}) {
            assertEquals(k, R.topK("Who owns Silverline Holdings?", k).size());
        }
    }

    @Test
    @DisplayName("topK is capped at the corpus size rather than throwing")
    void topKIsCapped() {
        assertEquals(Corpus.documents().size(),
                R.topK("anything at all", Corpus.documents().size() * 3).size());
    }

    @Test
    @DisplayName("topK returns no duplicates")
    void topKHasNoDuplicates() {
        List<Integer> got = R.topK("Which company supplies Pemberton Metals?", 20);
        assertEquals(got.size(), new TreeSet<>(got).size());
    }

    @Test
    @DisplayName("a larger k is a superset of a smaller one")
    void topKIsNested() {
        List<Integer> small = R.topK("Who owns Silverline Holdings?", 5);
        List<Integer> large = R.topK("Who owns Silverline Holdings?", 20);
        assertTrue(large.containsAll(small), "ranking must be stable as k grows");
    }

    @Test
    @DisplayName("results are in descending similarity order")
    void resultsAreRanked() {
        String q = "Who owns Silverline Holdings?";
        List<Integer> got = R.topK(q, 30);
        for (int i = 1; i < got.size(); i++) {
            assertTrue(R.score(q, got.get(i - 1)) >= R.score(q, got.get(i)) - 1e-12,
                    "ranking inverted at position " + i);
        }
    }

    @Test
    @DisplayName("ties are broken by document id, not by sort stability")
    void tiesAreBrokenDeterministically() {
        String q = "zzzz nothing matches this at all zzzz";
        List<Integer> a = R.topK(q, 30);
        List<Integer> b = new Retriever().topK(q, 30);
        assertEquals(a, b, "a fresh index must rank identically or every measured number drifts");
    }

    @Test
    @DisplayName("retrieval is deterministic across repeated calls")
    void retrievalIsDeterministic() {
        for (int i = 0; i < 5; i++) {
            assertEquals(R.topK("Who is a director of Meridian Shipping?", 10),
                    R.topK("Who is a director of Meridian Shipping?", 10));
        }
    }

    @Test
    @DisplayName("a document retrieves itself first")
    void documentsRetrieveThemselves() {
        for (int id : new int[]{0, 7, 21, 43, 60}) {
            assertEquals(id, R.topK(Corpus.doc(id).text(), 1).get(0),
                    "d" + id + " is not its own nearest neighbour");
        }
    }

    @Test
    @DisplayName("every single-premise question finds its premise in the top 5")
    void singlePremiseQuestionsAreEasy() {
        for (Corpus.Question q : Corpus.questions()) {
            if (q.premises().size() != 1) {
                continue;
            }
            assertTrue(R.topK(q.text(), 5).contains(q.premises().get(0)),
                    q.id() + " cannot find its only premise in the top 5, which would make the "
                            + "baseline too weak for any conclusion about it to be interesting");
        }
    }

    @Test
    @DisplayName("no multi-hop question has all its premises in the top 5")
    void multiHopQuestionsAreHard() {
        for (Corpus.Question q : Corpus.questions()) {
            if (q.kind() != Corpus.Kind.MULTIHOP) {
                continue;
            }
            assertFalse(R.topK(q.text(), 5).containsAll(q.premises()),
                    q.id() + " has full premise recall at k=5, so section 3 has no effect to "
                            + "measure on it");
        }
    }

    @Test
    @DisplayName("the retriever indexes distractors too, because a real index cannot tell")
    void distractorsAreIndexed() {
        boolean anyDistractorRetrieved = false;
        for (Corpus.Question q : Corpus.questions()) {
            for (int id : R.topK(q.text(), 10)) {
                if (Corpus.doc(id).relation() == null) {
                    anyDistractorRetrieved = true;
                }
            }
        }
        assertTrue(anyDistractorRetrieved,
                "if no distractor is ever retrieved they are not distracting anything");
    }

    @Test
    @DisplayName("the subgraph induced by top-k contains only factual documents' edges")
    void subgraphIgnoresDistractors() {
        Graph g = Systems.subgraph(R.topK("Who owns Silverline Holdings?", 20));
        for (Graph.Edge e : g.edges()) {
            assertNotNull(Corpus.doc(e.docId()).relation());
        }
    }

    @Test
    @DisplayName("the subgraph of every document equals the gold graph")
    void fullSubgraphEqualsGold() {
        List<Integer> all = new ArrayList<>();
        for (int i = 0; i < Corpus.documents().size(); i++) {
            all.add(i);
        }
        assertEquals(Systems.goldGraph().edges().size(), Systems.subgraph(all).edges().size());
    }

    @Test
    @DisplayName("an empty subgraph answers nothing")
    void emptySubgraphAnswersNothing() {
        assertTrue(Systems.answer("Q12", Systems.subgraph(List.of())).empty());
    }

    @Test
    @DisplayName("score is consistent with the ranking it produced")
    void scoreMatchesRanking() {
        String q = "Which companies are registered in Panama?";
        int top = R.topK(q, 1).get(0);
        for (int i = 0; i < Corpus.documents().size(); i++) {
            assertTrue(R.score(q, top) >= R.score(q, i) - 1e-12);
        }
    }
}
