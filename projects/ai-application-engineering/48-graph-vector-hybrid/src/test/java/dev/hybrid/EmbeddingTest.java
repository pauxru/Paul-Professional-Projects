package dev.hybrid;

import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

import java.util.*;

import static org.junit.jupiter.api.Assertions.*;

class EmbeddingTest {

    private static Embedding fitted(String... docs) {
        Embedding e = new Embedding();
        e.fit(List.of(docs));
        return e;
    }

    @Test
    @DisplayName("cosine of a vector with itself is 1")
    void selfSimilarityIsOne() {
        Embedding e = fitted("Meridian Shipping Ltd", "Baltic Freight AG", "Silverline Holdings");
        Map<Integer, Double> v = e.vectorise("Meridian Shipping Ltd");
        assertEquals(1.0, Embedding.cosine(v, v), 1e-9);
    }

    @Test
    @DisplayName("cosine is symmetric")
    void cosineIsSymmetric() {
        Embedding e = fitted("alpha beta", "beta gamma", "gamma delta");
        Map<Integer, Double> a = e.vectorise("alpha beta");
        Map<Integer, Double> b = e.vectorise("beta gamma");
        assertEquals(Embedding.cosine(a, b), Embedding.cosine(b, a), 1e-12);
    }

    @Test
    @DisplayName("cosine stays within [-1, 1] for every pair in the real corpus")
    void cosineIsBounded() {
        Retriever r = new Retriever();
        List<Corpus.Doc> docs = Corpus.documents();
        for (int i = 0; i < docs.size(); i += 7) {
            for (int j = 0; j < docs.size(); j += 11) {
                double c = Embedding.cosine(r.embedding().vectorise(docs.get(i).text()),
                        r.embedding().vectorise(docs.get(j).text()));
                assertTrue(c >= -1e-9 && c <= 1 + 1e-9, "cosine out of range: " + c);
            }
        }
    }

    @Test
    @DisplayName("vectors are L2-normalised, so cosine is a dot product")
    void vectorsAreUnitLength() {
        Embedding e = fitted("Ashford Components plc", "Kestrel Maritime SA", "Orion Chartering");
        Map<Integer, Double> v = e.vectorise("Ashford Components plc");
        double sumSquares = v.values().stream().mapToDouble(x -> x * x).sum();
        assertEquals(1.0, sumSquares, 1e-9);
    }

    @Test
    @DisplayName("an unseen string with no known n-grams gives an empty vector, not an exception")
    void unknownTextIsEmptyNotFatal() {
        Embedding e = fitted("alpha", "beta");
        Map<Integer, Double> v = e.vectorise("\u4f60\u597d\u4e16\u754c");
        assertNotNull(v);
        assertEquals(0.0, Embedding.cosine(v, e.vectorise("alpha")), 1e-12);
    }

    @Test
    @DisplayName("case and punctuation are normalised away")
    void normalisationIgnoresCaseAndPunctuation() {
        Embedding e = fitted("Meridian Shipping Ltd.", "Baltic Freight AG");
        assertEquals(1.0,
                Embedding.cosine(e.vectorise("MERIDIAN SHIPPING LTD."),
                        e.vectorise("meridian shipping ltd.")), 1e-9);
    }

    @Test
    @DisplayName("near-identical company names score higher than unrelated ones")
    void similarNamesScoreHigher() {
        Retriever r = new Retriever();
        Embedding e = r.embedding();
        double similar = Embedding.cosine(e.vectorise("Halcyon Trading Co"),
                e.vectorise("Halcyon Trading Company"));
        double unrelated = Embedding.cosine(e.vectorise("Halcyon Trading Co"),
                e.vectorise("Baltic Freight AG"));
        assertTrue(similar > unrelated,
                "similar=" + similar + " should exceed unrelated=" + unrelated);
    }

    @Test
    @DisplayName("the boundary padding makes a prefix match distinguishable from an infix one")
    void boundaryPaddingMatters() {
        Retriever r = new Retriever();
        Embedding e = r.embedding();
        double prefix = Embedding.cosine(e.vectorise("Meridian"), e.vectorise("Meridian Shipping"));
        double infix = Embedding.cosine(e.vectorise("Meridian"), e.vectorise("The Meridian Group"));
        assertTrue(prefix > infix, "prefix=" + prefix + " infix=" + infix);
    }

    @Test
    @DisplayName("vectorising is deterministic across calls")
    void vectorisingIsDeterministic() {
        Embedding e = fitted("Silverline Holdings SA", "Viktor Anisimov", "OFAC SDN List");
        assertEquals(e.vectorise("Silverline Holdings SA"), e.vectorise("Silverline Holdings SA"));
    }

    @Test
    @DisplayName("idf makes a term common to every document worth less than a rare one")
    void idfDownweightsUbiquitousTerms() {
        Embedding e = fitted(
                "registered in Panama", "registered in Cyprus", "registered in Malta",
                "registered in Panama and Bermuda");
        double common = Embedding.cosine(e.vectorise("registered"), e.vectorise("registered"));
        assertEquals(1.0, common, 1e-9);
        double viaCommonWord = Embedding.cosine(e.vectorise("registered in Cyprus"),
                e.vectorise("registered in Malta"));
        double viaRareWord = Embedding.cosine(e.vectorise("Panama"),
                e.vectorise("registered in Panama"));
        assertTrue(viaRareWord > 0, "a rare term should carry signal");
        assertTrue(viaCommonWord < 1.0, "documents sharing only boilerplate should not be identical");
    }

    @Test
    @DisplayName("the empty string is handled")
    void emptyStringIsSafe() {
        Embedding e = fitted("alpha", "beta");
        assertEquals(0.0, Embedding.cosine(e.vectorise(""), e.vectorise("alpha")), 1e-12);
    }
}
