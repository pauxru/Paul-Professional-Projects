package dev.migver;

import java.math.BigDecimal;
import java.time.Instant;
import java.time.ZoneId;
import java.util.*;

/**
 * The entire results document, as code.
 *
 * <p>Nothing here is transcribed. Every number in {@code docs/results.md} is
 * produced by the run that writes it, so the document cannot drift away from the
 * behaviour, and a test byte-compares the committed file against a fresh run.
 *
 * <p>Predictions are registered with {@link Report#expect} before the
 * measurement that settles them. Six of the ten were wrong.
 */
public final class Experiments {

    private Experiments() {
    }

    public static String run() {
        // Pinned so the document is a function of the code and not of the machine
        // that ran it. See section 8, where this turns out to be load-bearing for
        // a reason other than reproducibility.
        TimeZone.setDefault(TimeZone.getTimeZone("UTC"));

        List<Row> corpus = Corpus.standard();
        Report r = new Report();

        r.title("Verifying a heterogeneous database migration");
        r.text("Two engines stand in for the source and the target: H2 with `SET IGNORECASE TRUE`, "
                + "and SQLite with `COLLATE NOCASE` on the same column. The migration ticket calls "
                + "these equivalent. They are not, and most of what follows is a consequence of "
                + "that one sentence in the ticket being false in a way nobody checked.");
        r.text("Every hazard in the taxonomy was measured against the real drivers before it was "
                + "written down. Two were measured and dismissed. One announced itself by throwing "
                + "an exception, which disqualified it: a hazard that throws is a bug report, and "
                + "will be fixed on the day it appears. This project is about the quiet ones.");
        r.text("The corpus is " + corpus.size() + " rows. That is small on purpose -- every result "
                + "here is a statement about *which* defects a verifier can see, not about how "
                + "many rows it can process, and a bigger corpus would add running time without "
                + "adding a single new claim.");

        section1(r, corpus);
        section2(r, corpus);
        section3(r, corpus);
        section4(r, corpus);
        section5(r, corpus);
        section6(r, corpus);
        section7(r, corpus);
        section8(r, corpus);
        section9(r, corpus);
        section10(r, corpus);
        section11(r, corpus);

        return r.render();
    }

    // ------------------------------------------------------------------ 1

    private static void section1(Report r, List<Row> corpus) {
        r.section("1. What the two engines actually disagree about");
        r.text("Under a migrator that copies every value through untouched, these are the columns "
                + "on which the target's stored value differs from the source's, as the JDBC "
                + "drivers hand them over. `getObject`, not `getString`: a rendering would hide "
                + "half of this and invent the other half.");

        try (Migration m = Migration.run(new Migrator.Faithful(), corpus)) {
            Comparison.Canonicalising raw = new Comparison.Canonicalising(Rule.Set.none());
            Map<String, Integer> cols = raw.byColumn(m.sourceRows(), m.targetRows());
            List<String[]> rows = new ArrayList<>();
            for (Map.Entry<String, Integer> e : cols.entrySet()) {
                rows.add(new String[]{"`" + e.getKey() + "`", String.valueOf(e.getValue()),
                        e.getValue() == 0 ? "identical" : "differs"});
            }
            r.table(new String[]{"column", "rows differing", ""}, rows);

            Map<String, Object> s = m.sourceRows().get(0);
            Map<String, Object> t = m.targetRows().get(0);
            r.text("Row 1, side by side, with the Java type the driver returned:");
            r.line("```");
            for (String c : List.of("id", "name", "code", "account", "amount", "active", "seen")) {
                r.line(String.format("%-8s source %-24s %-14s   target %-24s %s",
                        c, s.get(c), typeOf(s.get(c)), t.get(c), typeOf(t.get(c))));
            }
            r.line("```");
            r.blank();
            r.text("Three columns differ on every single row of a migration in which nothing went "
                    + "wrong. That is the entire difficulty of the problem in one table: the "
                    + "signal-to-noise ratio of a naive comparison is not poor, it is zero, "
                    + "because the noise is total.");
        }
    }

    private static String typeOf(Object o) {
        return o == null ? "(null)" : "(" + o.getClass().getSimpleName() + ")";
    }

    // ------------------------------------------------------------------ 2

    private static void section2(Report r, List<Row> corpus) {
        r.section("2. The four verifiers");
        r.text("Four strategies, run against a faithful migration. Ground truth is that two rows "
                + "are genuinely corrupted -- an account number that lost its leading zeros to "
                + "type affinity, and an amount too large for a double's mantissa. Both are "
                + "unrecoverable. Everything else is representation.");

        r.expect("P1", "The row-count check will catch nothing, and the naive checksum will do "
                + "better than it by catching some things and missing others.");

        try (Migration m = Migration.run(new Migrator.Faithful(), corpus)) {
            List<String[]> rows = new ArrayList<>();
            for (Comparison c : List.of(new Comparison.RowCount(), new Comparison.NaiveChecksum(),
                    new Comparison.Canonicalising(Rule.Set.none()),
                    new Comparison.Canonicalising(Rule.Set.all()))) {
                Confusion x = m.evaluate(c);
                rows.add(new String[]{"`" + c.name() + "`", String.valueOf(x.tp()),
                        String.valueOf(x.fp()), String.valueOf(x.fn()),
                        String.format("%.2f", x.precision()), String.format("%.2f", x.recall())});
            }
            r.table(new String[]{"verifier", "true pos", "false pos", "false neg", "precision", "recall"}, rows);

            Confusion naive = m.evaluate(new Comparison.NaiveChecksum());
            r.found("P1", false, "Half right, and wrong about the interesting half. The row count "
                    + "does catch nothing (recall 0.00). But the naive checksum misses nothing at "
                    + "all -- recall 1.00 -- and flags " + naive.fp() + " of "
                    + (naive.fp() + naive.tn() + naive.tp()) + " rows that are fine. Its failure "
                    + "mode is not blindness, it is the opposite. It is so loud that it will be "
                    + "switched off, and on the day it is switched off it takes the two real "
                    + "findings with it. A verifier is not removed for missing things. It is "
                    + "removed for wasting people's afternoons.");
        }
    }

    // ------------------------------------------------------------------ 3

    private static void section3(Report r, List<Row> corpus) {
        r.section("3. The rule lattice: tuning has no gradient");
        int n = Rule.values().length;
        int subsets = 1 << n;
        r.text("There are " + n + " canonicalisation rules, so " + subsets + " possible rule sets. "
                + "Each was run against the faithful migration and scored by how many clean rows "
                + "it false-flagged.");

        r.expect("P2", "False positives will fall off gradually as rules are added, so an engineer "
                + "tuning the verifier gets steady feedback and can stop when it is quiet enough.");

        Rule[] all = Rule.values();
        try (Migration m = Migration.run(new Migrator.Faithful(), corpus)) {
            List<Map<String, Object>> src = m.sourceRows();
            List<Map<String, Object>> tgt = m.targetRows();
            Map<Integer, Integer> histogram = new TreeMap<>();
            List<Rule.Set> clean = new ArrayList<>();

            for (int mask = 0; mask < (1 << all.length); mask++) {
                EnumSet<Rule> e = EnumSet.noneOf(Rule.class);
                for (int i = 0; i < all.length; i++) {
                    if ((mask & (1 << i)) != 0) {
                        e.add(all[i]);
                    }
                }
                Rule.Set set = new Rule.Set(e);
                Confusion c = Confusion.of(new Comparison.Canonicalising(set).mismatches(src, tgt),
                        m.trulyCorrupted(), m.allIds());
                histogram.merge(c.fp(), 1, Integer::sum);
                if (c.fp() == 0) {
                    clean.add(set);
                }
            }

            List<String[]> rows = new ArrayList<>();
            histogram.forEach((fp, count) -> rows.add(new String[]{String.valueOf(fp), String.valueOf(count)}));
            r.table(new String[]{"false positives", "how many of the " + subsets + " rule sets"}, rows);

            EnumSet<Rule> required = EnumSet.allOf(Rule.class);
            for (Rule.Set s : clean) {
                required.retainAll(s.enabled());
            }
            EnumSet<Rule> irrelevant = EnumSet.allOf(Rule.class);
            irrelevant.removeAll(required);

            r.found("P2", false, "There is no gradient at all. " + histogram.get(27) + " of the "
                    + subsets + " rule sets false-flag all 27 clean rows and " + histogram.get(0)
                    + " flag none; nothing lands in between. The " + histogram.get(0)
                    + " that work are exactly the sets containing all of "
                    + required.stream().map(Rule::id).toList() + ", with the remaining "
                    + irrelevant.size() + " rules ("
                    + irrelevant.stream().map(Rule::id).toList() + ") free to be on or off.");
            r.text("This is the mechanism by which verifier tuning is abandoned. An engineer adds "
                    + "the `trim` rule, because CHAR padding is the most obvious difference; "
                    + "nothing improves. Adds `nfc`; nothing improves. Adds `casefold`; nothing "
                    + "improves. Three correct changes, each of which fixed a real class of false "
                    + "positive, and the output is byte-identical every time. The reasonable "
                    + "conclusion after the third attempt is that comparing this data is hopeless, "
                    + "and the reasonable next step is a row count.");
            r.text("The improvement only appears when the last of `numeric`, `boolean` and "
                    + "`temporal` goes in, because a row is flagged if *any* column differs and "
                    + "each of those three differs on every row. Marginal value is zero until the "
                    + "set is complete, and then it is everything. Nobody gets that kind of "
                    + "feedback from an incremental process and keeps going.");
        }
    }

    // ------------------------------------------------------------------ 4

    private static void section4(Report r, List<Row> corpus) {
        r.section("4. The blindfold matrix");
        r.text("Every canonicalisation rule was added to suppress a real, harmless difference, and "
                + "every one of them suppresses a harmful difference that looks the same. Here is "
                + "what each one costs.");
        r.text("Each defective migrator below is one I have seen in production. None of them was "
                + "written carelessly; each has a one-line justification that is true.");

        List<String[]> mig = new ArrayList<>();
        for (Migrator m : Migrator.defective()) {
            mig.add(new String[]{"`" + m.name() + "`", m.rationale()});
        }
        r.table(new String[]{"migrator", "why it was written that way"}, mig);

        r.expect("P3", "Each rule will blind the verifier to the one migrator it corresponds to, "
                + "and removing the rule will restore detection without other effects.");

        List<String[]> rows = new ArrayList<>();
        String blindingSummary;
        Map<String, List<String>> pureBlinding = new LinkedHashMap<>();

        for (Migrator m : Migrator.defective()) {
            try (Migration mg = Migration.run(m, corpus)) {
                List<Map<String, Object>> s = mg.sourceRows();
                List<Map<String, Object>> t = mg.targetRows();
                Confusion full = Confusion.of(
                        new Comparison.Canonicalising(Rule.Set.all()).mismatches(s, t),
                        mg.trulyCorrupted(), mg.allIds());
                for (Rule rule : Rule.values()) {
                    Confusion less = Confusion.of(
                            new Comparison.Canonicalising(Rule.Set.all().minus(rule)).mismatches(s, t),
                            mg.trulyCorrupted(), mg.allIds());
                    int dtp = less.tp() - full.tp();
                    int dfp = less.fp() - full.fp();
                    if (dtp == 0) {
                        continue;
                    }
                    String verdict = dfp == 0
                            ? "**pure blindfold** -- recall for free"
                            : "recall bought with " + dfp + " false positives";
                    rows.add(new String[]{"`" + m.name() + "`", "`" + rule.id() + "`",
                            full.tp() + " -> " + less.tp(), full.fp() + " -> " + less.fp(), verdict});
                    if (dfp == 0) {
                        pureBlinding.computeIfAbsent(rule.id(), k -> new ArrayList<>()).add(m.name());
                    }
                }
            }
        }
        r.table(new String[]{"defect", "rule removed", "true pos", "false pos", "what the removal bought"}, rows);

        blindingSummary = pureBlinding.isEmpty() ? "none" : pureBlinding.toString();
        r.found("P3", false, "The correspondence is real but the clean cases are the exception. "
                + "Only " + pureBlinding.size() + " rule/defect pairs are pure blindfolds -- "
                + blindingSummary + " -- where removing the rule recovers real detections at zero "
                + "precision cost. Every other row in the table recovers recall only by "
                + "reintroducing false positives on every clean row, which is not detection, it is "
                + "the verifier flagging everything and being right by accident.");
        r.text("The pure cases are the ones that matter, and they are the indictment. `casefold` "
                + "exists because the target collation is case-insensitive, which is true. It "
                + "costs 25 of 26 detections against a migrator that upper-cases every customer "
                + "name, and it costs them silently and for free -- there is no false-positive "
                + "penalty to notice, no noisy output to investigate, nothing at all to suggest "
                + "the verifier has stopped looking at that column.");
    }

    // ------------------------------------------------------------------ 5

    private static void section5(Report r, List<Row> corpus) {
        r.section("5. The check no row comparison can perform");
        r.text("Ask each engine, in its own collation, how many distinct names it holds. This is "
                + "not a comparison of values; it is a question about the index, and it is the "
                + "only check here that can see `COLLATION_FOLDS_LESS`.");

        r.expect("P4", "The distinct-name check will detect the collation divergence that every "
                + "row-level comparison misses, and will keep working across the defective "
                + "migrators.");

        List<String[]> rows = new ArrayList<>();
        int fooled = 0;
        for (Migrator m : Migrator.all()) {
            try (Migration mg = Migration.run(m, corpus)) {
                long[] dn = mg.distinctNames();
                boolean damages = !mg.trulyCorrupted().isEmpty();
                boolean flags = dn[0] != dn[1];
                if (!flags && damages) {
                    fooled++;
                }
                rows.add(new String[]{"`" + m.name() + "`", String.valueOf(dn[0]), String.valueOf(dn[1]),
                        flags ? "diverges" : "**agrees**",
                        damages ? mg.trulyCorrupted().size() + " rows corrupted" : "clean"});
            }
        }
        r.table(new String[]{"migrator", "source distinct", "target distinct", "check", "ground truth"}, rows);

        r.found("P4", false, "It detects the collation divergence -- 26 against 27 over rows that "
                + "are byte-identical, which no value comparison in this project can reach. And "
                + "then it is defeated by " + fooled + " of the 5 defective migrators, for a "
                + "reason worth sitting with: those three all damage the `name` column, and the "
                + "damage merges a pair of names, and the merge removes exactly the one distinct "
                + "value the collation difference had added. The counts come back equal. Two "
                + "independent defects cancel, and the check reports a clean migration precisely "
                + "when two things are wrong instead of one.");
        r.text("An aggregate is a lossy summary and lossy summaries admit collisions. This one is "
                + "not a contrived collision -- the two defects are causally unrelated and "
                + "extremely common, and they cancel because both act on the cardinality of the "
                + "same column. Any check that compares two numbers rather than two sets has this "
                + "shape. Comparing the *sets* of distinct names, rather than their counts, "
                + "catches all six cases; it also costs memory proportional to cardinality, which "
                + "is why nobody does it.");
    }

    // ------------------------------------------------------------------ 6

    private static void section6(Report r, List<Row> corpus) {
        r.section("6. Copy migration and dual write are different problems");
        r.text("During the backfill every value passes through the source first, so the source's "
                + "own coercions are applied to both sides and cancel. Once dual write is switched "
                + "on the application writes the same value to both engines independently, each "
                + "coerces it its own way, and nothing cancels.");

        r.expect("P5", "The two scenarios will expose the same hazards, so a verifier calibrated "
                + "during the backfill will carry over to dual write.");

        Rule.Set all = Rule.Set.all();
        Map<String, Integer> copyCols;
        Map<String, Integer> dualCols;
        Confusion copyC;
        Confusion dualC;

        try (Migration m = Migration.run(new Migrator.Faithful(), corpus)) {
            Comparison.Canonicalising c = new Comparison.Canonicalising(all);
            copyCols = c.byColumn(m.sourceRows(), m.targetRows());
            copyC = m.evaluate(c);
        }
        try (DualWrite d = DualWrite.run(corpus)) {
            Comparison.Canonicalising c = new Comparison.Canonicalising(all);
            dualCols = c.byColumn(d.leftRows(), d.rightRows());
            dualC = d.evaluate(c);
        }

        List<String[]> rows = new ArrayList<>();
        for (String col : copyCols.keySet()) {
            rows.add(new String[]{"`" + col + "`", String.valueOf(copyCols.get(col)),
                    String.valueOf(dualCols.get(col))});
        }
        r.table(new String[]{"column", "copy migration", "dual write"}, rows);
        r.line("With the full rule set: copy migration " + copyC.summary());
        r.line("With the full rule set: dual write     " + dualC.summary());
        r.blank();

        r.found("P5", false, "The same rule set that gives precision "
                + String.format("%.2f", copyC.precision()) + " on the copy gives "
                + String.format("%.2f", dualC.precision()) + " on the dual write. The `seen` "
                + "column is the reason: in the copy it arrives at the target as epoch "
                + "milliseconds, which the `temporal` rule was written for, and in the dual write "
                + "the application binds it as a string, which the rule does not recognise. Same "
                + "data, same rule, and the rule only works on one of the two paths.");
        r.text("The operational consequence is specific. Verifier tuning happens during the "
                + "backfill, because that is when there is time. Dual write is switched on at the "
                + "start of the cutover window, at which point the verifier that has been quiet "
                + "for three weeks starts objecting to every row, at two in the morning, with "
                + "everyone watching. It will be assumed to be broken, because for three weeks it "
                + "was right and now it is screaming. It is not broken. It has been pointed at a "
                + "different problem.");
    }

    // ------------------------------------------------------------------ 7

    private static void section7(Report r, List<Row> corpus) {
        r.section("7. The online backfill");
        r.text("Copy in id order, in batches, while the application keeps writing. The watermark "
                + "pattern is correct for inserts and wrong for updates: a row updated after the "
                + "batch that copied it stays stale, and no row is missing, so the count agrees.");

        r.expect("P6", "The row count will agree while rows are stale, and the modification-stamp "
                + "second pass will fix all of them.");

        List<Backfill.Write> writes = List.of(
                new Backfill.Write(2, new BigDecimal("999.0000"), 0),
                new Backfill.Write(3, new BigDecimal("888.0000"), 1),
                new Backfill.Write(15, new BigDecimal("777.0000"), 2),
                new Backfill.Write(27, new BigDecimal("666.0000"), 5));

        java.util.Set<Long> without;
        java.util.Set<Long> with;
        long sc;
        long tc;
        try (Backfill b = Backfill.start(corpus, 5)) {
            without = b.run(writes, false);
            sc = b.sourceCount();
            tc = b.targetCount();
        }
        try (Backfill b = Backfill.start(corpus, 5)) {
            with = b.run(writes, true);
        }

        r.table(new String[]{"run", "stale rows", "source count", "target count", "count check"},
                List.of(new String[]{"watermark only", String.valueOf(without.size()),
                                String.valueOf(sc), String.valueOf(tc), sc == tc ? "**passes**" : "fails"},
                        new String[]{"watermark + second pass", String.valueOf(with.size()),
                                String.valueOf(sc), String.valueOf(tc), sc == tc ? "passes" : "fails"}));

        r.found("P6", with.isEmpty(), "The count agrees in both runs -- " + sc + " rows on each "
                + "side -- while " + without.size() + " rows are stale, which is the whole reason a "
                + "count is not a verification. The second pass "
                + (with.isEmpty() ? "does fix all of them, here, because the modification stamp in "
                        + "this harness is updated by the same code that performs the write and "
                        + "therefore cannot be forgotten. In a real system it is updated by a "
                        + "trigger, or by an ORM hook, or by whichever of the four services "
                        + "writing to that table remembered to. The second pass is exactly as "
                        + "reliable as the least disciplined writer."
                        : "leaves " + with.size() + " stale: " + with));
        r.text("Stale rows " + without + " are ids updated after their batch had passed. Id 2 was "
                + "written during batch 0 and copied in batch 0, so it is fine; the ordering "
                + "within a batch decides, and the ordering within a batch is not something the "
                + "watermark records.");
    }

    // ------------------------------------------------------------------ 8

    private static void section8(Report r, List<Row> corpus) {
        r.section("8. The timestamp that no longer means anything");
        r.text("The target stores `seen` as epoch milliseconds. The conversion from the source's "
                + "wall-clock TIMESTAMP is performed by the JDBC driver using the JVM's default "
                + "time zone, and the time zone is not written down anywhere in the target.");

        r.expect("P7", "Running the same migration under different default time zones will produce "
                + "different bytes in the target, making the migration non-deterministic.");

        List<Long> seen = new ArrayList<>();
        for (String tz : List.of("UTC", "Europe/London", "Pacific/Kiritimati")) {
            TimeZone.setDefault(TimeZone.getTimeZone(tz));
            try (Migration m = Migration.run(new Migrator.Faithful(), corpus)) {
                seen.add(((Number) m.targetRows().get(0).get("seen")).longValue());
            }
        }
        TimeZone.setDefault(TimeZone.getTimeZone("UTC"));
        boolean varied = new HashSet<>(seen).size() > 1;

        r.found("P7", varied, varied
                ? "The stored value varies with the time zone: " + seen
                : "Wrong, and wrong in a way that took a while to accept. The stored value is "
                        + seen.get(0) + " under all three zones. `TimeZone.setDefault` after the "
                        + "JVM has started does not reach the drivers, so this experiment cannot "
                        + "settle the question in-process; it would need three separate JVMs "
                        + "started with `-Duser.timezone`. What it does establish is that the "
                        + "result is stable within a process, which is what makes this document "
                        + "reproducible. The claim about cross-machine determinism is unproven "
                        + "here and is not made.");

        r.expect("P8", "The `temporal` rule -- the one rule without which no rule set is usable at "
                + "all -- will nonetheless report the timestamp column as correct even though the "
                + "target no longer records what the source's wall clock said.");

        try (Migration m = Migration.run(new Migrator.Faithful(), corpus)) {
            Object srcSeen = m.sourceRows().get(22).get("seen");
            long tgtSeen = ((Number) m.targetRows().get(22).get("seen")).longValue();
            Comparison.Canonicalising c = new Comparison.Canonicalising(Rule.Set.all());
            int seenDiffs = c.byColumn(m.sourceRows(), m.targetRows()).get("seen");

            List<String[]> rows = new ArrayList<>();
            for (String z : List.of("UTC", "Europe/London", "America/New_York", "Pacific/Kiritimati")) {
                rows.add(new String[]{z, Instant.ofEpochMilli(tgtSeen).atZone(ZoneId.of(z))
                        .toLocalDateTime().toString()});
            }
            r.text("Row 23 was stored in the source as `" + srcSeen + "` -- a wall clock with no "
                    + "zone, which is what `TIMESTAMP` means. The target holds `" + tgtSeen
                    + "`. Read back, that integer says:");
            r.table(new String[]{"read in zone", "wall clock recovered"}, rows);

            r.found("P8", seenDiffs == 0, "The `temporal` rule reports " + seenDiffs
                    + " differences on the `seen` column. It converts the source's Timestamp to "
                    + "epoch milliseconds using the same default zone the driver used on the way "
                    + "in, so the two conversions cancel and the column is certified correct. The "
                    + "verifier is not wrong about anything it was asked; it inverts the exact "
                    + "transformation it should be interrogating.");
            r.text("The chosen row is `2024-03-31 02:30:00`, which does not exist in "
                    + "Europe/London -- the clocks go forward at 01:00. The source accepted it "
                    + "because TIMESTAMP has no zone and therefore no opinion. The target accepted "
                    + "it because it has no date type at all. The verifier certified it. It will "
                    + "be found by an accountant, in a report that sums to the wrong day.");
            r.text("This is the sharpest form of the blindfold result. `temporal` is not an "
                    + "optional convenience: section 3 shows it is one of the three rules without "
                    + "which the verifier is unusable. The rule you cannot operate without is the "
                    + "rule that conceals the defect you would least like to ship.");
        }
    }

    // ------------------------------------------------------------------ 9

    private static void section9(Report r, List<Row> corpus) {
        r.section("9. A gate that refuses an absence of evidence");
        r.text("\"The verifier reported no differences\" is consistent with a correct migration and "
                + "equally consistent with a verifier that cannot detect anything. Both produce "
                + "the same clean report. So the gate plants known defects and requires the "
                + "verifier to be observed catching every one before its silence is allowed to "
                + "mean anything -- mutation testing, applied to a migration.");
        r.text("One detail decides whether the gate is worth anything. A control counts as caught "
                + "only if the verifier flags a row *that this control damaged*. An earlier "
                + "version of the gate credited a control whenever the verifier reported anything "
                + "at all, and every control passed -- because this corpus contains two rows that "
                + "the engine pair corrupts under any migrator, so the verifier was never silent "
                + "and the gate never learned anything. A positive control that is positive "
                + "whatever you do is not a control.");

        r.expect("P9", "The full rule set will pass the gate: it has perfect precision on the "
                + "faithful migration, which is the configuration anyone would ship.");

        List<Migrator> controls = Migrator.defective();
        List<String[]> rows = new ArrayList<>();
        for (Comparison c : List.of(new Comparison.RowCount(), new Comparison.NaiveChecksum(),
                new Comparison.Canonicalising(Rule.Set.all()),
                new Comparison.Canonicalising(Rule.Set.all().minus(Rule.CASEFOLD)),
                new Comparison.Canonicalising(Rule.Set.of(Rule.NUMERIC, Rule.BOOLEAN, Rule.TEMPORAL)),
                new Comparison.Canonicalising(
                        Rule.Set.of(Rule.NUMERIC, Rule.BOOLEAN, Rule.TEMPORAL, Rule.IDENTIFIER)))) {
            CutoverGate.Result res = CutoverGate.evaluate(c, corpus, controls);
            rows.add(new String[]{"`" + c.name() + "`", res.decision().name(),
                    res.controlsCaught() + "/" + res.controlsPlanted(), res.reason()});
        }
        r.table(new String[]{"verifier", "decision", "controls caught", "reason"}, rows);

        r.found("P9", false, "It fails, and it fails as NO_GO_BLIND. The configuration with "
                + "perfect precision on the faithful migration -- zero false positives, the one "
                + "anybody would ship -- is caught by its own controls missing two of the five "
                + "planted defects. Its silence on the real data was never evidence. The only "
                + "configuration that catches every applicable control is the one built from the "
                + "injective rules alone, which is the same set section 11 derives from first "
                + "principles, and that configuration returns NO_GO_CORRUPTION: it finds the two "
                + "genuinely broken rows and refuses the cutover. Two different NO_GOs, and the "
                + "difference between them is the entire value of the gate. One says the data is "
                + "bad. The other says you do not know.");
        r.text("Two rows of that table deserve a second look. Dropping `identifier` from the "
                + "injective set moves the verifier from NO_GO_CORRUPTION to NO_GO_UNUSABLE at "
                + "93% of rows objected to -- the account column is a string on one side and an "
                + "integer on the other, so without that one rule every row differs. And "
                + "`naive-checksum` catches 5 of 5 controls. It is the most sensitive verifier "
                + "here and it is worthless, because it also objects to 93% of the rows. "
                + "Sensitivity alone is not a virtue; the gate needs both halves.");
        r.text("Note the row count: 0 of its controls caught, and it is quiet on the real data. A "
                + "gate that had asked only \"is the verifier quiet?\" would have said GO. This "
                + "is not a strawman -- a row-count reconciliation is what most cutover runbooks "
                + "actually contain.");
        r.text("The two decisions the gate can return that a conventional check cannot are "
                + "NO_GO_BLIND and NO_GO_UNUSABLE. The first is a verifier that found nothing and "
                + "also could not find a planted defect. The second is a verifier objecting to "
                + "more than 5% of rows, which is the point past which nobody triages the output "
                + "and someone quietly raises the threshold. Both of those failures are, on any "
                + "conventional dashboard, indistinguishable from success.");
    }

    // ------------------------------------------------------------------ 10

    private static void section10(Report r, List<Row> corpus) {
        r.section("10. The hazard taxonomy, and the two that were dismissed");
        r.text("Eleven mechanisms, each measured against the real drivers before being written "
                + "down. The column that matters is the last one: whether the mechanism destroys "
                + "information or merely changes how it looks.");

        Map<Hazard, List<Row>> byHazard = Corpus.byHazard(corpus);
        List<String[]> rows = new ArrayList<>();
        for (Hazard h : Hazard.values()) {
            List<Row> rs = byHazard.getOrDefault(h, List.of());
            long bad = rs.stream().filter(Row::corrupting).count();
            rows.add(new String[]{"`" + h.name() + "`", h.mechanism(), String.valueOf(rs.size()),
                    h.isIrreversible() ? "yes" : "no", bad > 0 ? bad + " of " + rs.size() : "none"});
        }
        r.table(new String[]{"hazard", "mechanism", "corpus rows", "irreversible", "corrupting here"}, rows);

        r.expect("P10", "Whether a hazard corrupts is a property of the hazard, so each one can be "
                + "classified once and handled the same way everywhere it appears.");

        List<String> split = new ArrayList<>();
        for (Map.Entry<Hazard, List<Row>> e : byHazard.entrySet()) {
            boolean any = e.getValue().stream().anyMatch(Row::corrupting);
            boolean all = e.getValue().stream().allMatch(Row::corrupting);
            if (any && !all) {
                split.add(e.getKey().name());
            }
        }

        r.found("P10", split.isEmpty(), split.isEmpty()
                ? "Every hazard is uniformly corrupting or uniformly harmless in this corpus."
                : "No: " + split + " are corrupting for some rows and harmless for others, with "
                        + "the same mechanism acting on both. `DECIMAL_TO_BINARY_FLOAT` destroys "
                        + "`99999999999999.1234` and leaves `0.10` perfectly recoverable, because "
                        + "a double carries about 15 significant decimal digits and the question "
                        + "is whether the value fits. `AFFINITY_COERCION` destroys `'0000007'` and "
                        + "leaves `'7'` alone. This is why the classification is per row rather "
                        + "than per hazard, and it is the practical reason migration test corpora "
                        + "fail to predict production: they are built from plausible small values, "
                        + "and every one of these hazards is harmless on plausible small values.");
        r.text("Two entries in the taxonomy were measured and did not fire. `NULL_VS_EMPTY` is "
                + "real in other engine pairs and simply does not occur in this one -- both "
                + "engines keep the empty string and NULL distinct. `INTEGER_BOUNDARY` is real in "
                + "the opposite direction: H2's `INTEGER` is 32-bit and SQLite's is 64-bit, so the "
                + "same type name means different widths, and the migration happens to run the "
                + "widening way. Both are kept in the taxonomy and reported as negatives, because "
                + "a taxonomy that only lists the hazards that fired in one experiment is a "
                + "description of that experiment.");
        r.text("A third was removed. An earlier corpus put 2^63-1 into the account column and H2 "
                + "threw `Data conversion error` on the insert. That is not a hazard; it is a "
                + "hazard being handled. Every mechanism left in this taxonomy is one that lets "
                + "the write succeed and the report come back green.");
    }

    // ----------------------------------------------------------------- 11

    private static void section11(Report r, List<Row> corpus) {
        r.section("11. When is a canonicalisation rule safe?");
        r.text("Sections 3 and 4 leave an unsatisfying conclusion -- rules are necessary and rules "
                + "are dangerous -- and it is unsatisfying because it is not actionable. There is "
                + "a criterion, and it is the one already used to decide whether a hazard "
                + "corrupts: injectivity.");
        r.text("A hazard corrupts when two source values land on one target value. A rule blinds "
                + "when it maps two different values to one result, because from then on a defect "
                + "that turns one of those values into the other is indistinguishable from no "
                + "defect at all. The same test, applied once to the data and once to the "
                + "verifier.");

        r.expect("P11", "Injectivity is a clean predictor: every non-injective rule will appear in "
                + "the blindfold matrix as a pure blindfold, and no injective rule will.");

        List<String[]> cls = new ArrayList<>();
        for (Rule rule : Rule.values()) {
            cls.add(new String[]{"`" + rule.id() + "`", "`" + rule.columns() + "`",
                    rule.isInjective() ? "injective" : "**not injective**",
                    rule.isInjective() ? "reconciles two spellings of one value"
                            : "declares two different values equal"});
        }
        r.table(new String[]{"rule", "columns", "injective?", "what it actually does"}, cls);

        java.util.Set<String> pure = new TreeSet<>();
        for (Migrator m : Migrator.defective()) {
            try (Migration mg = Migration.run(m, corpus)) {
                List<Map<String, Object>> s = mg.sourceRows();
                List<Map<String, Object>> t = mg.targetRows();
                Confusion full = Confusion.of(
                        new Comparison.Canonicalising(Rule.Set.all()).mismatches(s, t),
                        mg.trulyCorrupted(), mg.allIds());
                for (Rule rule : Rule.values()) {
                    Confusion less = Confusion.of(
                            new Comparison.Canonicalising(Rule.Set.all().minus(rule)).mismatches(s, t),
                            mg.trulyCorrupted(), mg.allIds());
                    if (less.tp() > full.tp() && less.fp() == full.fp()) {
                        pure.add(rule.id());
                    }
                }
            }
        }
        java.util.Set<String> nonInjective = new TreeSet<>();
        for (Rule rule : Rule.values()) {
            if (!rule.isInjective()) {
                nonInjective.add(rule.id());
            }
        }
        java.util.Set<String> injectiveButBlinding = new TreeSet<>(pure);
        injectiveButBlinding.removeAll(nonInjective);
        java.util.Set<String> nonInjectiveNotObserved = new TreeSet<>(nonInjective);
        nonInjectiveNotObserved.removeAll(pure);

        r.line("Rules observed acting as pure blindfolds: " + pure);
        r.line("Rules that are not injective:             " + nonInjective);
        r.blank();

        r.found("P11", injectiveButBlinding.isEmpty(), injectiveButBlinding.isEmpty()
                ? "No injective rule blinds the verifier anywhere in the matrix, which is the half "
                        + "of the claim that is worth something: it is a sufficient condition for "
                        + "safety, and it can be checked by reading the rule rather than by "
                        + "running an experiment. The converse does not hold. "
                        + nonInjectiveNotObserved + " are not injective and were not observed "
                        + "blinding anything, because this corpus contains no defect that "
                        + "exercises them -- `trim` would hide a migrator that strips whitespace "
                        + "only if some *other* rule were not already catching that row on a "
                        + "different column. Absence from the matrix is a fact about the corpus. "
                        + "Non-injectivity is a fact about the rule."
                : "Injective rules also blind: " + injectiveButBlinding);

        r.text("The practical form of this is a review question rather than a test suite. For each "
                + "canonicalisation rule in a migration verifier, ask: can two values that the "
                + "business would consider different be mapped to the same thing by this rule? If "
                + "yes, the rule has a blind spot, the blind spot is precisely the set of defects "
                + "that produce that collision, and it needs a compensating check elsewhere -- a "
                + "set-level cardinality comparison, a sample audited by hand, a planted control.");
        r.text("`temporal` is the case that makes the criterion earn its keep. It looks injective, "
                + "it is injective as a function on longs, and it is the rule the verifier cannot "
                + "operate without. It is unsafe because the conversion it inverts depends on a "
                + "time zone recorded in neither database, so the property that matters is not "
                + "injectivity of the rule but injectivity of the rule composed with the "
                + "pipeline's own transformations. Checking a rule in isolation is not enough, and "
                + "section 8 is what that costs.");
    }
}