package dev.migver;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;

/**
 * Writes docs/results.md, or prints it.
 *
 * <p>The file is written from here rather than typed, so it cannot drift from
 * the behaviour it describes. {@code OperationsTest.theCommittedReportMatchesAFreshRun}
 * byte-compares the committed copy against a fresh run and fails with the line
 * number of the first divergence.
 *
 * <p>{@code --stdout} prints instead of writing, which is what the determinism
 * stage of {@code test.ps1} hashes.
 */
public final class Main {

    public static void main(String[] args) throws IOException {
        String out = Experiments.run();
        boolean stdout = args.length > 0 && args[0].equals("--stdout");
        if (stdout) {
            System.out.print(out);
            return;
        }
        Path p = Paths.get("docs", "results.md");
        Files.createDirectories(p.getParent());
        Files.write(p, out.getBytes(StandardCharsets.UTF_8));
        System.out.println("wrote " + p.toAbsolutePath() + " (" + out.length() + " chars)");
    }
}
