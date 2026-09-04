package dev.hybrid;

import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

import java.util.*;

import static org.junit.jupiter.api.Assertions.*;

class ResolverTest {

    private static Resolver at(double threshold) {
        return new Resolver(new Retriever().embedding(), threshold);
    }

    private static Map<String, String> resolveCorpus(double threshold) {
        return at(threshold).resolve(Systems.surfaceForms()).surfaceToCanonical();
    }

    @Test
    @DisplayName("every surface form is assigned to some cluster")
    void everySurfaceFormIsAssigned() {
        Map<String, String> a = resolveCorpus(0.55);
        for (String s : Systems.surfaceForms()) {
            assertNotNull(a.get(s), s + " was not assigned");
        }
    }

    @Test
    @DisplayName("a cluster representative is itself a surface form")
    void representativesAreRealSurfaceForms() {
        Map<String, String> a = resolveCorpus(0.55);
        Set<String> forms = new TreeSet<>(Systems.surfaceForms());
        for (String rep : a.values()) {
            assertTrue(forms.contains(rep), rep + " is not a surface form");
        }
    }

    @Test
    @DisplayName("resolution is idempotent: a representative resolves to itself")
    void resolutionIsIdempotent() {
        Map<String, String> a = resolveCorpus(0.55);
        for (String rep : new TreeSet<>(a.values())) {
            assertEquals(rep, a.get(rep), rep + " does not resolve to itself");
        }
    }

    @Test
    @DisplayName("a threshold above 1 puts every surface form in its own cluster")
    void impossibleThresholdSplitsEverything() {
        Map<String, String> a = resolveCorpus(1.01);
        assertEquals(Systems.surfaceForms().size(), new TreeSet<>(a.values()).size());
    }

    @Test
    @DisplayName("a threshold of zero collapses everything into one cluster")
    void zeroThresholdMergesEverything() {
        Map<String, String> a = resolveCorpus(0.0);
        assertEquals(1, new TreeSet<>(a.values()).size());
    }

    @Test
    @DisplayName("resolution is deterministic across runs")
    void resolutionIsDeterministic() {
        assertEquals(resolveCorpus(0.55), resolveCorpus(0.55));
    }

    @Test
    @DisplayName("resolution does not depend on the order the surface forms arrive in")
    void resolutionIsOrderIndependent() {
        List<String> forms = new ArrayList<>(Systems.surfaceForms());
        Collections.reverse(forms);
        Embedding e = new Retriever().embedding();
        assertEquals(new Resolver(e, 0.55).resolve(Systems.surfaceForms()).surfaceToCanonical(),
                new Resolver(e, 0.55).resolve(forms).surfaceToCanonical(),
                "input order must not decide identity, or resolution is a coin flip");
    }

    @Test
    @DisplayName("there is a threshold at which resolution is exactly right")
    void aPerfectThresholdExists() {
        Resolver.Quality q = Resolver.score(resolveCorpus(0.55), Corpus.entities());
        assertTrue(q.perfect(), "expected perfect resolution at 0.55, got " + q);
    }

    @Test
    @DisplayName("Halcyon's two spellings are merged at the working threshold")
    void halcyonMerges() {
        Map<String, String> a = resolveCorpus(0.55);
        assertEquals(a.get("Halcyon Trading Co"), a.get("Halcyon Trading Company"));
    }

    @Test
    @DisplayName("the two Meridians stay apart at the working threshold")
    void meridiansStayApart() {
        Map<String, String> a = resolveCorpus(0.55);
        assertNotEquals(a.get("Meridian Shipping Ltd"), a.get("Meridian Freight Services"));
    }

    @Test
    @DisplayName("a low threshold merges something that fabricates a sanctions path")
    void theTrapCanBeSprung() {
        Map<String, String> a = resolveCorpus(0.20);
        assertTrue(Resolver.score(a, Corpus.entities()).mergeErrors() > 0,
                "if no threshold merges anything, section 5 has nothing to measure");
        assertEquals(a.get("Baltic Freight"), a.get("Meridian Freight Services"),
                "the measured merge is not the one the corpus was designed to bait: two firms "
                        + "sharing only the word Freight are fused, and Baltic Freight sits "
                        + "inside the sanctioned chain");
    }

    @Test
    @DisplayName("the two Meridians are never fused at any threshold, despite the bait")
    void theBaitedTrapDoesNotSpring() {
        for (double t : new double[]{0.10, 0.20, 0.25, 0.30, 0.40, 0.55}) {
            Map<String, String> a = resolveCorpus(t);
            assertNotEquals(a.get("Meridian Shipping Ltd"), a.get("Meridian Freight Services"),
                    "at t=" + t + " -- the report claims this pair survives every threshold "
                            + "and the damaging merge is a different one entirely");
        }
    }

    @Test
    @DisplayName("merging the Meridians fabricates a sanctions path to Ravenna Textiles")
    void aMergeFabricatesAWellCitedFalsehood() {
        Resolver low = at(0.20);
        low.resolve(Systems.surfaceForms());
        Executor.Result r = Systems.answer("Q15", Systems.resolvedGraph(low), low::resolveQuery);
        assertFalse(r.empty(),
                "Q15's correct answer is nothing; a merge should make the graph assert otherwise");
        for (Executor.Answer a : r.answers()) {
            for (int docId : a.path().provenance()) {
                assertNotNull(Corpus.doc(docId).relation(),
                        "the fabricated path should cite only real documents -- that is the point");
            }
        }
    }

    @Test
    @DisplayName("split errors rise monotonically with the threshold")
    void splitsRiseWithThreshold() {
        int prior = -1;
        for (double t : new double[]{0.55, 0.65, 0.75, 0.85, 0.95}) {
            int splits = Resolver.score(resolveCorpus(t), Corpus.entities()).splitErrors();
            assertTrue(splits >= prior, "splits fell from " + prior + " to " + splits + " at " + t);
            prior = splits;
        }
    }

    @Test
    @DisplayName("merge errors fall as the threshold rises")
    void mergesFallWithThreshold() {
        int prior = Integer.MAX_VALUE;
        for (double t : new double[]{0.20, 0.30, 0.40, 0.55, 0.75}) {
            int merges = Resolver.score(resolveCorpus(t), Corpus.entities()).mergeErrors();
            assertTrue(merges <= prior, "merges rose from " + prior + " to " + merges + " at " + t);
            prior = merges;
        }
    }

    @Test
    @DisplayName("a perfect resolution scores 1.0 on both pair metrics")
    void perfectScoresOne() {
        Resolver.Quality q = Resolver.score(resolveCorpus(0.55), Corpus.entities());
        assertEquals(1.0, q.pairRecall(), 1e-12);
        assertEquals(1.0, q.pairSpecificity(), 1e-12);
    }

    @Test
    @DisplayName("scoring counts a split, not a merge, when one entity is torn apart")
    void scoringDistinguishesTheTwoErrors() {
        List<Corpus.Ent> truth = List.of(new Corpus.Ent("X", List.of("X", "X Ltd"), "company"));
        Resolver.Quality split = Resolver.score(Map.of("X", "X", "X Ltd", "X Ltd"), truth);
        assertEquals(1, split.splitErrors());
        assertEquals(0, split.mergeErrors());
    }

    @Test
    @DisplayName("scoring counts a merge, not a split, when two entities are fused")
    void scoringCountsMerges() {
        List<Corpus.Ent> truth = List.of(
                new Corpus.Ent("X", List.of("X"), "company"),
                new Corpus.Ent("Y", List.of("Y"), "company"));
        Resolver.Quality merged = Resolver.score(Map.of("X", "X", "Y", "X"), truth);
        assertEquals(1, merged.mergeErrors());
        assertEquals(0, merged.splitErrors());
    }

    @Test
    @DisplayName("a resolution error only shows in the answers when it touches a queried path")
    void invisibleErrorsAreStillErrors() {
        Resolver r = at(0.40);
        Map<String, String> a = r.resolve(Systems.surfaceForms()).surfaceToCanonical();
        Resolver.Quality q = Resolver.score(a, Corpus.entities());
        assertFalse(q.perfect(), "0.40 is expected to be imperfect");
        Graph g = Systems.resolvedGraph(r);
        assertFalse(Systems.answer("Q1", g, r::resolveQuery).empty(),
                "an error elsewhere in the graph should not break an unrelated question");
    }

    @Test
    @DisplayName("resolve is a pure function of its input, not of how often it has been called")
    void resolveIsIdempotent() {
        Resolver r = at(0.55);
        Map<String, String> first = r.resolve(Systems.surfaceForms()).surfaceToCanonical();
        Map<String, String> second = r.resolve(Systems.surfaceForms()).surfaceToCanonical();
        assertEquals(first, second,
                "resolving twice on one instance must not cluster the second input against the "
                        + "first input's clusters");
    }

    @Test
    @DisplayName("resolving a different input does not leak the previous input's clusters")
    void resolveDoesNotLeakBetweenInputs() {
        Resolver r = at(0.55);
        r.resolve(List.of("Zzyzx Holdings BV", "Zzyzx Holdings"));
        Map<String, String> after = r.resolve(List.of("Baltic Freight AG")).surfaceToCanonical();
        assertEquals(Set.of("Baltic Freight AG"), after.keySet());
    }

    @Test
    @DisplayName("a query name never seen in the corpus resolves to the nearest cluster")
    void queryResolutionHandlesUnseenNames() {
        Resolver r = at(0.45);
        r.resolve(Systems.surfaceForms());
        String resolved = r.resolveQuery("Meridian Shipping Ltd.");
        assertEquals(r.canonical("Meridian Shipping Ltd"), resolved,
                "the query entity passes through the same resolver as the corpus");
    }

    @Test
    @DisplayName("an unresolvable query name is returned unchanged rather than guessed at")
    void unresolvableQueryNamesPassThrough() {
        Resolver r = at(0.99);
        r.resolve(Systems.surfaceForms());
        assertEquals("Zzyzx Holdings BV", r.resolveQuery("Zzyzx Holdings BV"));
    }

    @Test
    @DisplayName("with perfect resolution the built graph answers exactly what the gold graph does")
    void perfectResolutionReproducesGold() {
        Resolver r = at(0.55);
        r.resolve(Systems.surfaceForms());
        Graph built = Systems.resolvedGraph(r);
        Graph gold = Systems.goldGraph();
        assertEquals(gold.edges().size(), built.edges().size());
        assertEquals(gold.nodes().size(), built.nodes().size(),
                "perfect resolution must not change the node count");
    }

    @Test
    @DisplayName("surfaceOf picks the longest variant actually present in the sentence")
    void surfaceOfPrefersTheLongestMatch() {
        assertEquals("Meridian Shipping Limited",
                Systems.surfaceOf("Meridian Shipping Ltd",
                        "Meridian Shipping Limited is a registered supplier to Ashford "
                                + "Components plc."));
    }

    @Test
    @DisplayName("surfaceOf falls back to the canonical name when no variant appears")
    void surfaceOfFallsBack() {
        assertEquals("Baltic Freight AG",
                Systems.surfaceOf("Baltic Freight AG", "a sentence mentioning nothing relevant"));
    }
}
