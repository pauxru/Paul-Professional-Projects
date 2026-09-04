package dev.hybrid;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;

/** Writes docs/results.md, or prints it with {@code --stdout}. */
public final class Main {

    public static void main(String[] args) throws IOException {
        String out = Experiments.run();
        if (args.length > 0 && args[0].equals("--stdout")) {
            System.out.print(out);
            return;
        }
        Path p = Paths.get("docs", "results.md");
        Files.createDirectories(p.getParent());
        Files.write(p, out.getBytes(StandardCharsets.UTF_8));
        System.out.println("wrote " + p.toAbsolutePath() + " (" + out.length() + " chars)");
    }
}
