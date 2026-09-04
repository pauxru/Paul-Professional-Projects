package dev.hybrid;

import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

import java.util.*;

import static org.junit.jupiter.api.Assertions.*;

class ScoreTest {

    @Test
    @DisplayName("a perfect answer is exact with precision and recall of 1")
    void perfectAnswer() {
        Score s = Score.of(Set.of("A", "B"), Set.of("A", "B"));
        assertTrue(s.exact());
        assertEquals(1.0, s.precision());
        assertEquals(1.0, s.recall());
        assertEquals(1.0, s.f1());
    }

    @Test
    @DisplayName("a partial answer is not exact, and recall says how partial")
    void partialAnswer() {
        Score s = Score.of(Set.of("A", "B", "C"), Set.of("A", "B"));
        assertFalse(s.exact());
        assertEquals(1.0, s.precision());
        assertEquals(2.0 / 3, s.recall(), 1e-12);
    }

    @Test
    @DisplayName("two of three sanctioned suppliers scores above none of them")
    void partialBeatsNothing() {
        Score partial = Score.of(Set.of("A", "B", "C"), Set.of("A", "B"));
        Score nothing = Score.of(Set.of("A", "B", "C"), Set.of());
        assertTrue(partial.f1() > nothing.f1(),
                "exact-match accuracy would score these identically, which is the reason this "
                        + "class exists");
    }

    @Test
    @DisplayName("an invented answer costs precision")
    void inventionCostsPrecision() {
        Score s = Score.of(Set.of("A"), Set.of("A", "B"));
        assertEquals(0.5, s.precision());
        assertEquals(1.0, s.recall());
        assertFalse(s.exact());
    }

    @Test
    @DisplayName("silence on a question whose answer is nothing is not a false alarm")
    void correctSilenceIsNotAnAlarm() {
        Score s = Score.of(Set.of(), Set.of());
        assertFalse(s.falseAlarm());
        assertTrue(s.exact());
    }

    @Test
    @DisplayName("answering a negative question is a false alarm")
    void speakingOnANegativeIsAnAlarm() {
        Score s = Score.of(Set.of(), Set.of("A"));
        assertTrue(s.falseAlarm());
        assertFalse(s.exact());
    }

    @Test
    @DisplayName("a false alarm is distinguished from an ordinary false positive")
    void alarmsAreNotJustFalsePositives() {
        Score ordinary = Score.of(Set.of("A"), Set.of("A", "B"));
        assertFalse(ordinary.falseAlarm(),
                "a wrong extra answer on a real question is not the same operational event as "
                        + "stopping a shipment that should have proceeded");
    }

    @Test
    @DisplayName("silence on a question that had an answer is not an alarm but is not exact")
    void silenceOnAPositiveIsAMiss() {
        Score s = Score.of(Set.of("A"), Set.of());
        assertFalse(s.falseAlarm());
        assertFalse(s.exact());
        assertEquals(0.0, s.recall());
    }

    @Test
    @DisplayName("precision and recall of two empty sets are defined as 1, not NaN")
    void emptyOverEmptyIsDefined() {
        Score s = Score.of(Set.of(), Set.of());
        assertEquals(1.0, s.precision());
        assertEquals(1.0, s.recall());
    }

    @Test
    @DisplayName("f1 is zero when nothing overlaps")
    void disjointScoresZero() {
        assertEquals(0.0, Score.of(Set.of("A"), Set.of("B")).f1());
    }

    @Test
    @DisplayName("tp, fp and fn account for every element on both sides")
    void countsAreComplete() {
        Set<String> gold = Set.of("A", "B", "C");
        Set<String> got = Set.of("B", "C", "D");
        Score s = Score.of(gold, got);
        assertEquals(2, s.tp());
        assertEquals(1, s.fp());
        assertEquals(1, s.fn());
        assertEquals(gold.size(), s.tp() + s.fn());
        assertEquals(got.size(), s.tp() + s.fp());
    }

    @Test
    @DisplayName("scoring does not mutate the sets it is given")
    void scoringIsNonDestructive() {
        Set<String> gold = new TreeSet<>(Set.of("A", "B"));
        Set<String> got = new TreeSet<>(Set.of("B", "C"));
        Score.of(gold, got);
        assertEquals(Set.of("A", "B"), gold);
        assertEquals(Set.of("B", "C"), got);
    }

    @Test
    @DisplayName("pct renders two decimals in a locale-independent way")
    void pctIsStable() {
        assertEquals("0.50", Score.pct(0.5));
        assertEquals("1.00", Score.pct(1.0));
        assertEquals("0.00", Score.pct(0.0));
    }
}
