package dev.hybrid;

import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import static org.junit.jupiter.api.Assertions.*;

/**
 * The report is a build artifact, not a document someone edits.
 *
 * <p>A results file that has drifted from the code that produced it is worse
 * than no results file, because it reads exactly like a current one. These
 * tests fail if the committed report is stale, if any prediction was left
 * unresolved, or if the run is not reproducible.
 */
class ReportIntegrityTest {

    private static String committed() throws IOException {
        Path p = Paths.get("docs", "results.md");
        assertTrue(Files.exists(p), "docs/results.md has not been generated");
        return Files.readString(p, StandardCharsets.UTF_8).replace("\r\n", "\n");
    }

    @Test
    @DisplayName("the committed report is byte-identical to a fresh run")
    void reportIsFresh() throws IOException {
        assertEquals(committed(), Experiments.run().replace("\r\n", "\n"),
                "docs/results.md is stale. Regenerate it; do not edit it.");
    }

    @Test
    @DisplayName("two runs in the same JVM produce identical output")
    void runIsDeterministic() {
        assertEquals(Experiments.run(), Experiments.run());
    }

    @Test
    @DisplayName("every prediction registered is also resolved")
    void everyPredictionIsSettled() {
        String md = Experiments.run();
        Matcher predicted = Pattern.compile("\\*\\*Predicted \\((P\\d+)\\)\\*\\*").matcher(md);
        java.util.Set<String> registered = new java.util.TreeSet<>();
        while (predicted.find()) {
            registered.add(predicted.group(1));
        }
        Matcher settled = Pattern.compile("\\*\\*(?:Held|Contradicted) \\((P\\d+)\\)\\*\\*")
                .matcher(md);
        java.util.Set<String> resolved = new java.util.TreeSet<>();
        while (settled.find()) {
            resolved.add(settled.group(1));
        }
        assertEquals(registered, resolved,
                "a prediction was registered and never settled, which is how an inconvenient "
                        + "measurement gets quietly dropped");
        assertFalse(registered.isEmpty());
    }

    @Test
    @DisplayName("the scoreboard counts match the body of the report")
    void scoreboardMatchesBody() {
        String md = Experiments.run();
        int held = count(md, "\\*\\*Held \\(P\\d+\\)\\*\\*");
        int contradicted = count(md, "\\*\\*Contradicted \\(P\\d+\\)\\*\\*");
        Matcher m = Pattern.compile("(\\d+) predictions registered before measurement; "
                + "(\\d+) held, (\\d+) contradicted").matcher(md);
        assertTrue(m.find(), "the report has no scoreboard summary line");
        assertEquals(held + contradicted, Integer.parseInt(m.group(1)));
        assertEquals(held, Integer.parseInt(m.group(2)));
        assertEquals(contradicted, Integer.parseInt(m.group(3)));
    }

    @Test
    @DisplayName("the report contradicts at least one of its own predictions")
    void theReportIsNotSelfCongratulatory() {
        assertTrue(count(Experiments.run(), "\\*\\*Contradicted \\(P\\d+\\)\\*\\*") > 0,
                "a report in which every prediction held is a report whose predictions were "
                        + "written after the measurements");
    }

    @Test
    @DisplayName("no placeholder text survives into the report")
    void noPlaceholders() {
        String md = Experiments.run().toLowerCase(java.util.Locale.ROOT);
        for (String bad : new String[]{"todo", "tbd", "fixme", "lorem ipsum", "xxx"}) {
            assertFalse(md.contains(bad), "the report contains '" + bad + "'");
        }
    }

    @Test
    @DisplayName("every markdown table has a consistent column count")
    void tablesAreWellFormed() {
        String[] lines = Experiments.run().split("\n");
        int expected = -1;
        for (int i = 0; i < lines.length; i++) {
            String line = lines[i].trim();
            if (!line.startsWith("|")) {
                expected = -1;
                continue;
            }
            int columns = line.split("\\|", -1).length;
            if (expected == -1) {
                expected = columns;
            } else {
                assertEquals(expected, columns,
                        "ragged table at line " + (i + 1) + ": " + line);
            }
        }
    }

    @Test
    @DisplayName("the report uses no characters the Windows console cannot render")
    void reportIsConsoleSafe() {
        for (char c : Experiments.run().toCharArray()) {
            assertTrue(c == '\n' || c == '\t' || (c >= 32 && c < 127),
                    "non-ASCII character U+" + Integer.toHexString(c) + " in the report");
        }
    }

    @Test
    @DisplayName("the report names the load-bearing conclusion")
    void reportStatesItsThesis() {
        String md = Experiments.run();
        assertTrue(md.contains("vector for identity"), "the thesis is missing");
        assertTrue(md.contains("upper bound"), "the design's generosity to the baseline is unstated");
    }

    private static int count(String text, String regex) {
        Matcher m = Pattern.compile(regex).matcher(text);
        int n = 0;
        while (m.find()) {
            n++;
        }
        return n;
    }
}
