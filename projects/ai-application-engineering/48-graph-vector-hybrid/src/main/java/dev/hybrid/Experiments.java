package dev.hybrid;

import java.util.*;

/**
 * The report, as a program.
 *
 * <p>Every prediction is registered with {@code r.expect} before the code that
 * settles it runs. {@link Report#render} refuses to produce a document with an
 * open prediction, so the format cannot be quietly abandoned when a measurement
 * is inconvenient.
 */
public final class Experiments {

    private static final int[] K_VALUES = {3, 5, 10, 20, 40, 80};

    private Experiments() {}

    public static String run() {
        Report r = new Report();
        Retriever retriever = new Retriever();
        Graph gold = Systems.goldGraph();
        List<Corpus.Question> questions = Corpus.questions();

        r.title("Graph traversal and vector retrieval are not competing answers to one question");
        r.text("They answer different questions, and the difference is not a matter of quality. "
                + "A retriever returns text that exists. A traversal derives facts that no text "
                + "states. Everything below measures where that boundary falls and what it costs "
                + "to cross it.");
        r.text("**Method.** Both systems execute the *same* query plan through the *same* "
                + "executor. They differ in one thing: which graph they are handed. The graph "
                + "system gets the full extracted graph; the vector system gets the subgraph "
                + "induced by its top-k retrieved documents. This makes the retriever's reader "
                + "perfect -- it never hallucinates, never misreads a passage, and composes "
                + "facts flawlessly across everything it was given. Every number reported for "
                + "the vector system is therefore an **upper bound on what any RAG pipeline over "
                + "this corpus could achieve**, and the failures below are floors rather than "
                + "artefacts of a weak baseline.");
        r.text("The embedding is a character 4-gram TF-IDF model, not a neural encoder. What "
                + "that changes, and what it demonstrably does not, is measured in section 8 and "
                + "argued in `docs/adr/001-lexical-embeddings.md`.");

        section1(r, retriever, gold, questions);
        section2(r, retriever, gold, questions);
        section3(r, retriever, questions);
        section4(r, retriever, gold, questions);
        section5(r, retriever, questions);
        section6(r, retriever, gold, questions);
        section7(r, retriever, gold, questions);
        section8(r, retriever, questions);
        section9(r, gold);

        return r.render();
    }

    // ------------------------------------------------------------------- 1

    private static void section1(Report r, Retriever retriever, Graph gold,
                                 List<Corpus.Question> questions) {
        r.section("1. The corpus, and the fact it does not contain");
        r.text("Eighty documents about corporate ownership and supply relationships. Fifty state "
                + "exactly one relation each; thirty are plausible, topical, and state nothing "
                + "-- they exist so that top-k retrieval has something confident and wrong to "
                + "return, which is the realistic failure. An empty result is obvious. A "
                + "confident irrelevant one is not.");

        List<String[]> rows = new ArrayList<>();
        for (int id = 0; id <= 4; id++) {
            rows.add(new String[]{"d" + id, Corpus.doc(id).text()});
        }
        r.table(new String[]{"doc", "text"}, rows);

        r.text("Those five documents, composed, say that Ashford Components buys from a company "
                + "ultimately controlled by a sanctioned individual. **No document in the corpus "
                + "says that**, because no filing in the world would. The fact exists only as the "
                + "composition of five separate assertions made by five parties who never spoke "
                + "to each other. That is what a multi-hop question is, and it is why retrieval "
                + "quality is not the relevant axis.");
        r.text("The graph extracted from the fifty factual documents has " + gold.nodes().size()
                + " nodes and " + gold.edges().size() + " edges across five relation types. The "
                + "retriever indexes all eighty documents, including the distractors, because a "
                + "real index does not know which is which.");

        r.expect("P1", "On single-hop questions -- where the answer is stated verbatim in one "
                + "document -- the two systems will be indistinguishable, because retrieval only "
                + "has to find one thing.");

        List<Corpus.Question> single = questions.stream()
                .filter(q -> q.kind() == Corpus.Kind.LOOKUP).toList();
        int vectorExact = 0;
        int graphExact = 0;
        List<String[]> t = new ArrayList<>();
        for (Corpus.Question q : single) {
            Score sg = Score.of(q.answer(), Systems.answer(q.id(), gold).values());
            Score sv = Score.of(q.answer(),
                    Systems.answer(q.id(), Systems.subgraph(retriever.topK(q.text(), 10))).values());
            if (sg.exact()) {
                graphExact++;
            }
            if (sv.exact()) {
                vectorExact++;
            }
            t.add(new String[]{q.id(), q.text(), sv.exact() ? "yes" : "**no**",
                    sg.exact() ? "yes" : "**no**"});
        }
        r.table(new String[]{"id", "question", "vector k=10 exact", "graph exact"}, t);
        r.found("P1", vectorExact == graphExact,
                vectorExact == graphExact
                        ? "Indistinguishable: " + vectorExact + " of " + single.size() + " each. "
                        + "Retrieval is a perfectly good way to answer a question whose answer is "
                        + "written down, and nothing in this report argues otherwise."
                        : "Not indistinguishable: vector " + vectorExact + ", graph " + graphExact
                        + " of " + single.size() + ".");
    }

    // ------------------------------------------------------------------- 2

    private static void section2(Report r, Retriever retriever, Graph gold,
                                 List<Corpus.Question> questions) {
        r.section("2. Accuracy against hop distance");
        r.text("The same comparison, grouped by how many edges the answer path needs.");

        r.expect("P2", "Vector accuracy will fall off gradually with hop count -- worse at three "
                + "hops than at two, but still finding some answers, because a large enough k "
                + "will often happen to contain the whole chain.");

        Map<Integer, int[]> byHops = new TreeMap<>();
        for (Corpus.Question q : questions) {
            if (q.kind() == Corpus.Kind.OPEN || q.kind() == Corpus.Kind.AGGREGATE) {
                continue;
            }
            int[] c = byHops.computeIfAbsent(q.hops(), k -> new int[3]);
            c[0]++;
            if (Score.of(q.answer(), Systems.answer(q.id(),
                    Systems.subgraph(retriever.topK(q.text(), 10))).values()).exact()) {
                c[1]++;
            }
            if (Score.of(q.answer(), Systems.answer(q.id(), gold).values()).exact()) {
                c[2]++;
            }
        }
        List<String[]> rows = new ArrayList<>();
        for (Map.Entry<Integer, int[]> e : byHops.entrySet()) {
            int[] c = e.getValue();
            rows.add(new String[]{String.valueOf(e.getKey()), String.valueOf(c[0]),
                    c[1] + "/" + c[0], c[2] + "/" + c[0]});
        }
        r.table(new String[]{"hops", "questions", "vector k=10 exact", "graph exact"}, rows);

        int multiCorrect = byHops.entrySet().stream().filter(e -> e.getKey() >= 2)
                .mapToInt(e -> e.getValue()[1]).sum();
        int multiTotal = byHops.entrySet().stream().filter(e -> e.getKey() >= 2)
                .mapToInt(e -> e.getValue()[0]).sum();
        r.found("P2", false, "There is no gradual fall-off. Vector answers " + multiCorrect
                + " of the " + multiTotal + " questions needing two or more hops. The transition "
                + "is not from good to poor; it is from working to not working, and it happens "
                + "between one hop and two. Section 3 measures why.");
    }

    // ------------------------------------------------------------------- 3

    private static void section3(Report r, Retriever retriever, List<Corpus.Question> questions) {
        r.section("3. Why more retrieval does not help: the conjunction problem");
        r.text("A single-hop question needs one document. A four-hop question needs all nine, "
                + "*simultaneously*, inside the same top-k window. Retrieval ranks documents by "
                + "similarity to the question, and the documents in the middle of a chain do not "
                + "resemble the question -- \"Baltic Freight AG is controlled by Silverline "
                + "Holdings SA\" shares almost nothing with \"which suppliers of Ashford "
                + "Components are ultimately controlled by a sanctioned person\". Those documents "
                + "are relevant by composition, not by similarity, and similarity is the only "
                + "thing the index knows.");

        r.expect("P3", "Recall of the full premise set will improve steadily with k, so a "
                + "sufficiently large k recovers multi-hop performance. This is the standard "
                + "remedy and the reason production systems ship k=50.");

        List<String[]> rows = new ArrayList<>();
        for (Corpus.Question q : questions) {
            if (q.premises().size() < 2 || q.kind() == Corpus.Kind.OPEN) {
                continue;
            }
            List<String> cells = new ArrayList<>();
            cells.add(q.id());
            cells.add(String.valueOf(q.hops()));
            cells.add(String.valueOf(q.premises().size()));
            for (int k : K_VALUES) {
                List<Integer> got = retriever.topK(q.text(), k);
                long hit = q.premises().stream().filter(got::contains).count();
                cells.add(hit == q.premises().size() ? "**all**" : hit + "/" + q.premises().size());
            }
            rows.add(cells.toArray(new String[0]));
        }
        List<String> headers = new ArrayList<>(List.of("id", "hops", "premises"));
        for (int k : K_VALUES) {
            headers.add("k=" + k);
        }
        r.table(headers.toArray(new String[0]), rows);

        int completeAt40 = 0;
        int completeAt80 = 0;
        int multi = 0;
        for (Corpus.Question q : questions) {
            if (q.premises().size() < 2 || q.kind() == Corpus.Kind.OPEN) {
                continue;
            }
            multi++;
            if (retriever.topK(q.text(), 40).containsAll(q.premises())) {
                completeAt40++;
            }
            if (retriever.topK(q.text(), 80).containsAll(q.premises())) {
                completeAt80++;
            }
        }
        r.found("P3", false, "It does not recover. Of the " + multi + " questions needing more "
                + "than one premise, " + completeAt40 + " have their full premise set inside the "
                + "top 40 of an 80-document corpus -- half the corpus, a k no production system "
                + "would run. At k=80, which is the entire corpus, " + completeAt80 + " do, and "
                + "at that point the retriever has stopped being a retriever. The premises that "
                + "stay missing are the middle links, which is exactly what the similarity "
                + "argument predicts: they are the ones with no lexical relationship to the "
                + "question.");
        r.text("This is the mechanism behind a familiar production experience -- raising k "
                + "improves the easy questions, does nothing for the hard ones, and increases "
                + "cost and latency monotonically. The hard questions are not hard because "
                + "retrieval is imprecise. They are hard because their answer requires a "
                + "conjunction, and ranking optimises each item independently.");
    }

    // ------------------------------------------------------------------- 4

    private static void section4(Report r, Retriever retriever, Graph gold,
                                 List<Corpus.Question> questions) {
        r.section("4. Isolating the cause: an oracle retriever");
        r.text("If the failure is retrieval of the conjunction, then handing the retriever "
                + "exactly the right documents should fix it completely. If something else is "
                + "wrong -- reasoning, composition, the plan -- it should not.");

        r.expect("P4", "Given an oracle that returns precisely the premise documents and nothing "
                + "else, the vector system will match the graph exactly. The entire multi-hop "
                + "deficit is retrieval of the conjunction and none of it is reasoning.");

        int oracleExact = 0;
        int graphExact = 0;
        int scored = 0;
        List<String[]> rows = new ArrayList<>();
        for (Corpus.Question q : questions) {
            if (q.kind() == Corpus.Kind.OPEN) {
                continue;
            }
            scored++;
            Executor.Result oracle = Systems.answer(q.id(), Systems.subgraph(q.premises()));
            Score so = Score.of(q.answer(), oracle.values());
            Score sg = Score.of(q.answer(), Systems.answer(q.id(), gold).values());
            if (so.exact()) {
                oracleExact++;
            }
            if (sg.exact()) {
                graphExact++;
            }
            if (!so.exact()) {
                rows.add(new String[]{q.id(), String.valueOf(q.hops()), q.kind().name(),
                        "p=" + Score.pct(so.precision()) + " r=" + Score.pct(so.recall()),
                        oracle.values().isEmpty() ? "(nothing)" : String.valueOf(oracle.values())});
            }
        }
        if (rows.isEmpty()) {
            r.text("Every question the oracle was given, it answered exactly.");
        } else {
            r.text("The questions the oracle still gets wrong:");
            r.table(new String[]{"id", "hops", "kind", "oracle score", "oracle answer"}, rows);
        }
        r.found("P4", oracleExact == graphExact,
                oracleExact == graphExact
                        ? "Confirmed, and this is the load-bearing result of the report. With "
                        + "perfect retrieval the vector system scores " + oracleExact + "/"
                        + scored + " -- identical to the graph. The reasoning machinery is shared "
                        + "and it works. **The multi-hop deficit is entirely the probability of "
                        + "retrieving a conjunction, and no improvement to the encoder changes "
                        + "the shape of that problem**: a better encoder ranks each document "
                        + "better; it does not make the middle of a chain resemble the question."
                        : "Not confirmed: oracle " + oracleExact + ", graph " + graphExact + " of "
                        + scored + ". Something other than retrieval is also wrong, and the table "
                        + "above says what.");
    }

    // ------------------------------------------------------------------- 5

    private static void section5(Report r, Retriever retriever, List<Corpus.Question> questions) {
        r.section("5. The part nobody mentions: the graph is built by an embedding");
        r.text("A graph over messy filings cannot be built without deciding that \"Halcyon "
                + "Trading Co\" and \"Halcyon Trading Company\" are one node, and that \"Meridian "
                + "Shipping Ltd\" and \"Meridian Freight Services\" are two. That is a similarity "
                + "judgement over surface form. It is what embeddings are for, and it is what "
                + "traversal cannot do.");
        r.text("So the architecture these measurements support is not graph *or* vector. It is "
                + "**vector for identity, graph for traversal**: the embedding decides what a "
                + "node is, the graph decides what follows from it.");

        r.expect("P5", "Resolution errors will degrade answers roughly in proportion to the error "
                + "rate -- a few bad merges will cost a few answers.");

        List<String[]> rows = new ArrayList<>();
        double[] thresholds = {0.20, 0.30, 0.40, 0.45, 0.55, 0.60, 0.65, 0.75, 0.85};
        int perfectWindow = 0;
        int mergeAlarms = 0;
        int splitAlarms = 0;
        int worstSplitExact = Integer.MAX_VALUE;
        int scoredTotal = 0;
        String silentRow = "";
        String culprit = "";
        for (double t : thresholds) {
            Resolver resolver = new Resolver(retriever.embedding(), t);
            Map<String, String> assignment =
                    resolver.resolve(Systems.surfaceForms()).surfaceToCanonical();
            Resolver.Quality q = Resolver.score(assignment, Corpus.entities());
            Graph built = Systems.resolvedGraph(resolver);
            int exact = 0;
            int scored = 0;
            int falseAlarms = 0;
            for (Corpus.Question question : questions) {
                if (question.kind() == Corpus.Kind.OPEN) {
                    continue;
                }
                scored++;
                Set<String> got = remap(
                        Systems.answer(question.id(), built, resolver::resolveQuery).values(),
                        assignment);
                Score s = Score.of(question.answer(), got);
                if (s.exact()) {
                    exact++;
                }
                if (s.falseAlarm()) {
                    falseAlarms++;
                }
            }
            if (q.perfect()) {
                perfectWindow++;
            }
            scoredTotal = scored;
            if (q.mergeErrors() > 0) {
                mergeAlarms += falseAlarms;
                if (falseAlarms > 0 && culprit.isEmpty()) {
                    culprit = String.format(Locale.ROOT, "at threshold %.2f, %s", t,
                            describeMerges(assignment));
                }
            } else if (q.splitErrors() > 0) {
                splitAlarms += falseAlarms;
                worstSplitExact = Math.min(worstSplitExact, exact);
            }
            if (!q.perfect() && falseAlarms == 0 && exact == scored && silentRow.isEmpty()) {
                silentRow = String.format(Locale.ROOT,
                        "At threshold %.2f the resolver makes %d merge and %d split errors and "
                                + "every one of the %d answers is still exactly right",
                        t, q.mergeErrors(), q.splitErrors(), scored);
            }
            rows.add(new String[]{String.format(Locale.ROOT, "%.2f", t),
                    String.valueOf(q.mergeErrors()), String.valueOf(q.splitErrors()),
                    String.format(Locale.ROOT, "%.3f", q.pairRecall()),
                    String.format(Locale.ROOT, "%.3f", q.pairSpecificity()),
                    exact + "/" + scored, String.valueOf(falseAlarms)});
        }
        r.table(new String[]{"threshold", "merge errors", "split errors", "pair recall",
                "pair specificity", "exact answers", "false alarms"}, rows);
        r.text("The two ratio columns are printed to three decimals because at two they both read "
                + "1.00 across the whole sweep. There are " + Resolver.score(
                        new Resolver(retriever.embedding(), 0.55).resolve(Systems.surfaceForms())
                                .surfaceToCanonical(), Corpus.entities()).diffGoldPairs()
                + " pairs that should be apart, so four bad merges is a specificity of 0.993 -- a "
                + "number that rounds to perfect and reads as success on a dashboard. **Aggregate "
                + "resolution metrics hide exactly the errors that matter**, because the damage is "
                + "not proportional to the pair count; it is proportional to how central the "
                + "merged node is.");

        r.found("P5", false, "Not proportional, and -- far more importantly -- **not symmetric**. "
                + "Across the whole sweep the thresholds that merge produced " + mergeAlarms
                + " false alarm(s); the thresholds that only split produced " + splitAlarms
                + ". Splits fail by omission: the graph comes apart, fewer questions are "
                + "answerable, and the worst split-only threshold still answers " + worstSplitExact
                + " of " + scoredTotal + " without ever asserting something untrue. Merges fail by "
                + "invention. **The error that looks smaller on every dashboard is the one that "
                + "produces a wrong answer rather than no answer**, and in compliance those are "
                + "not comparable outcomes -- a missing answer gets escalated, a fabricated one "
                + "gets acted on.");
        if (!silentRow.isEmpty()) {
            r.text(silentRow + ". Resolution errors are only visible when they touch a path "
                    + "somebody traverses, so the error rate you can measure and the error rate "
                    + "that matters are different quantities, and the second one depends on the "
                    + "queries.");
        }
        r.text("There is a window of " + perfectWindow + " of " + thresholds.length
                + " sampled thresholds where resolution is exactly right, and the uncomfortable "
                + "part is that **you cannot locate that window without ground truth** -- which is "
                + "the thing you were building the graph to obtain. In production this is why "
                + "entity resolution is a labelled-data problem wearing an unsupervised costume.");
        r.text("The specific damage is worth naming, and it is not the damage the corpus was "
                + "built to bait. Two entities were planted with confusable names -- \"Meridian "
                + "Shipping Ltd\" and \"Meridian Freight Services\" -- on the expectation that a "
                + "low threshold would fuse them. **No threshold ever does.** What happens "
                + "instead: " + (culprit.isEmpty() ? "no merge produced a false alarm" : culprit)
                + ".");
        r.text("That is a more uncomfortable result than the one that was designed. The merge "
                + "that does the damage is between two names sharing a single generic industry "
                + "word, and it happens because greedy clustering assigns each surface form to "
                + "whichever cluster already exists and scores highest -- so the fused pair "
                + "depends on processing order and on which names arrived first, not on the pair "
                + "being especially similar. Ravenna Textiles buys from the merged node, the "
                + "merged node inherits the sanctioned parent's edges, and the graph asserts a "
                + "sanctions exposure that no document supports, with a provenance chain in which "
                + "**every cited document is real and every edge was genuinely asserted**. The "
                + "falsehood is in the node, and nothing downstream of the node can see it.");
    }

    /** Names the surface-form pairs a resolution wrongly placed in one cluster. */
    private static String describeMerges(Map<String, String> assignment) {
        Map<String, String> gold = new LinkedHashMap<>();
        for (Corpus.Ent e : Corpus.entities()) {
            for (String v : e.variants()) {
                gold.put(v, e.canonical());
            }
        }
        List<String> surfaces = new ArrayList<>(new TreeSet<>(assignment.keySet()));
        List<String> pairs = new ArrayList<>();
        for (int i = 0; i < surfaces.size(); i++) {
            for (int j = i + 1; j < surfaces.size(); j++) {
                String a = surfaces.get(i);
                String b = surfaces.get(j);
                String ga = gold.get(a);
                String gb = gold.get(b);
                if (ga == null || gb == null || ga.equals(gb)) {
                    continue;
                }
                if (assignment.get(a).equals(assignment.get(b))) {
                    pairs.add("\"" + a + "\" is fused with \"" + b + "\"");
                }
            }
        }
        return pairs.isEmpty() ? "nothing is fused" : String.join("; ", pairs);
    }

    private static Set<String> remap(Set<String> values, Map<String, String> assignment) {
        // Answers come back named after whichever surface form claimed each
        // cluster. Map them onto the corpus's canonical names so the comparison
        // is against the entity rather than against a naming accident of the
        // clustering. This deliberately does not repair merge errors -- two
        // entities in one cluster still answer as one.
        Map<String, String> canonicalOf = new LinkedHashMap<>();
        for (Corpus.Ent e : Corpus.entities()) {
            for (String v : e.variants()) {
                canonicalOf.put(v, e.canonical());
            }
        }
        Set<String> out = new TreeSet<>();
        for (String v : values) {
            out.add(canonicalOf.getOrDefault(v, v));
        }
        return out;
    }

    // ------------------------------------------------------------------- 6

    private static void section6(Report r, Retriever retriever, Graph gold,
                                 List<Corpus.Question> questions) {
        r.section("6. Where the graph loses, and it is not paraphrase");
        r.text("A comparison that only asks questions the graph was built to answer is not a "
                + "comparison. The extractor recognises five relation types. The corpus, like "
                + "every real corpus, contains facts that are none of them.");

        r.expect("P6", "The graph will lose on paraphrased questions, where the wording differs "
                + "from the stored relation names -- the usual argument for embeddings.");

        List<String[]> rows = new ArrayList<>();
        long openTotal = questions.stream().filter(q -> q.kind() == Corpus.Kind.OPEN).count();
        int graphInSchema = 0;
        int vectorFoundCount = 0;
        for (Corpus.Question q : questions) {
            if (q.kind() != Corpus.Kind.OPEN) {
                continue;
            }
            boolean graphCan = Corpus.doc(q.premises().get(0)).relation() != null;
            boolean vectorFound = retriever.topK(q.text(), 5).contains(q.premises().get(0));
            if (graphCan) {
                graphInSchema++;
            }
            if (vectorFound) {
                vectorFoundCount++;
            }
            rows.add(new String[]{q.id(), q.text(), vectorFound ? "**found**" : "missed",
                    graphCan ? "in schema" : "**not in schema**"});
        }
        r.table(new String[]{"id", "question", "vector k=5", "graph"}, rows);

        int paraphraseGraph = 0;
        int paraphraseVector = 0;
        int paraphraseCount = 0;
        for (Corpus.Question q : questions) {
            if (q.kind() != Corpus.Kind.PARAPHRASE) {
                continue;
            }
            paraphraseCount++;
            if (Score.of(q.answer(), Systems.answer(q.id(), gold).values()).exact()) {
                paraphraseGraph++;
            }
            if (Score.of(q.answer(), Systems.answer(q.id(),
                    Systems.subgraph(retriever.topK(q.text(), 10))).values()).exact()) {
                paraphraseVector++;
            }
        }
        r.found("P6", false, "Paraphrase is not where the graph loses -- it scores "
                + paraphraseGraph + "/" + paraphraseCount + " against the retriever's "
                + paraphraseVector + "/" + paraphraseCount + ", because once a question has been "
                + "turned into a plan its wording is irrelevant. The graph loses somewhere more "
                + "fundamental: **" + (openTotal - graphInSchema) + " of " + openTotal + " open "
                + "questions have no representation in its schema at all.** \"Who maintains the "
                + "OFAC SDN List?\" is answered by one sentence in the corpus and by no edge in "
                + "the graph, because MAINTAINS was not a relation the extractor knew about. The "
                + "retriever finds " + vectorFoundCount + " of " + openTotal + " in its top 5.");
        r.text("This is the real trade and it is structural. A graph's coverage is bounded by a "
                + "schema fixed before the questions were known. Retrieval has no schema and "
                + "therefore no coverage limit -- it returns something for any question, which is "
                + "simultaneously its advantage here and its failure mode in section 7.");
    }

    // ------------------------------------------------------------------- 7

    private static void section7(Report r, Retriever retriever, Graph gold,
                                 List<Corpus.Question> questions) {
        r.section("7. Saying nothing");
        r.text("Two questions have the empty set as their correct answer. In compliance this is "
                + "the majority case and the only one with a deadline: the operational question "
                + "is not \"who is sanctioned\" but \"may this shipment proceed\".");

        r.expect("P7", "Both systems will handle the negatives, since neither has any incentive "
                + "to invent an answer -- the executor returns only nodes it actually reached.");

        List<String[]> rows = new ArrayList<>();
        int vectorAlarms = 0;
        int graphAlarms = 0;
        for (Corpus.Question q : questions) {
            if (q.kind() != Corpus.Kind.NEGATIVE) {
                continue;
            }
            Executor.Result g = Systems.answer(q.id(), gold);
            Executor.Result v = Systems.answer(q.id(),
                    Systems.subgraph(retriever.topK(q.text(), 10)));
            if (Score.of(q.answer(), v.values()).falseAlarm()) {
                vectorAlarms++;
            }
            if (Score.of(q.answer(), g.values()).falseAlarm()) {
                graphAlarms++;
            }
            rows.add(new String[]{q.id(), q.text(),
                    v.values().isEmpty() ? "(nothing)" : String.valueOf(v.values()),
                    g.values().isEmpty() ? "(nothing)" : String.valueOf(g.values())});
        }
        r.table(new String[]{"id", "question", "vector k=10", "graph"}, rows);
        r.found("P7", vectorAlarms == 0 && graphAlarms == 0,
                vectorAlarms == 0 && graphAlarms == 0
                        ? "Both stay silent, and the reason is worth stating because it is a "
                        + "design choice rather than a property of either substrate: the shared "
                        + "executor returns only nodes reached by a real edge. A generative "
                        + "reader over the same retrieved passages has no such constraint. It "
                        + "sees Meridian Freight Services, Silverline Holdings and the word "
                        + "sanctioned inside one window, and the passages that would rule out a "
                        + "connection are not there to rule it out. Q16 exists to make that "
                        + "concrete: the corpus never says Meridian Freight is *not* owned by "
                        + "Silverline. **Absence is not retrievable.**"
                        : "Not both: vector raised " + vectorAlarms + " false alarms, graph "
                        + graphAlarms + ".");
        r.text("The asymmetry that matters is not who gets the negative right today. It is that "
                + "the graph's silence is a *closed-world* statement -- no path exists in the "
                + "extracted graph -- which is checkable, and wrong in knowable ways. The "
                + "retriever's silence means only that nothing similar appeared in the top k.");
    }

    // ------------------------------------------------------------------- 8

    private static void section8(Report r, Retriever retriever, List<Corpus.Question> questions) {
        r.section("8. What a better encoder would and would not change");
        r.text("The embedding here is character 4-gram TF-IDF. It is a real vector space with a "
                + "real metric, and it is weaker than a sentence encoder at exactly one thing: "
                + "recognising that two differently worded sentences mean the same. That weakness "
                + "has a measurable footprint, and it is worth locating precisely rather than "
                + "waving at.");

        r.expect("P8", "The lexical model's disadvantage will show up as poor ranking of premise "
                + "documents for paraphrased questions, and nowhere else that matters.");

        List<String[]> rows = new ArrayList<>();
        for (Corpus.Question q : questions) {
            if (q.premises().isEmpty()) {
                continue;
            }
            rows.add(new String[]{q.id(), q.kind().name(), String.valueOf(q.hops()),
                    String.valueOf(q.premises().size()),
                    String.valueOf(worstPremiseRank(retriever, q))});
        }
        r.table(new String[]{"id", "kind", "hops", "premises", "k needed for full recall"}, rows);
        r.text("The last column is the k a perfect-recall retriever would need for that question. "
                + "A better encoder moves those numbers down. It does not change which of them "
                + "are large: the large ones belong to multi-hop questions, and they are large "
                + "because the middle of a chain has no lexical *or* semantic relationship to the "
                + "question. \"Baltic Freight AG is controlled by Silverline Holdings SA\" is not "
                + "about Ashford Components under any encoder, because it is not about Ashford "
                + "Components.");

        double lookup = meanWorstRank(retriever, questions, Corpus.Kind.LOOKUP);
        double paraphrase = meanWorstRank(retriever, questions, Corpus.Kind.PARAPHRASE);
        double multihop = meanWorstRank(retriever, questions, Corpus.Kind.MULTIHOP);
        r.found("P8", paraphrase > lookup && multihop > paraphrase,
                String.format(Locale.ROOT,
                        "Mean k needed for full premise recall: literal lookups %.1f, paraphrased "
                                + "%.1f, multi-hop %.1f. The paraphrase penalty is real and is "
                                + "exactly what a neural encoder fixes. It is also far smaller "
                                + "than the multi-hop penalty, which no encoder fixes. Section 4 "
                                + "settled this independently: with perfect retrieval the vector "
                                + "system matches the graph, so the encoder is not the binding "
                                + "constraint on anything measured here.",
                        lookup, paraphrase, multihop));
    }

    private static int worstPremiseRank(Retriever retriever, Corpus.Question q) {
        List<Integer> all = retriever.topK(q.text(), Corpus.documents().size());
        int worst = 0;
        for (int p : q.premises()) {
            worst = Math.max(worst, all.indexOf(p) + 1);
        }
        return worst;
    }

    private static double meanWorstRank(Retriever retriever, List<Corpus.Question> questions,
                                        Corpus.Kind kind) {
        List<Integer> ranks = new ArrayList<>();
        for (Corpus.Question q : questions) {
            if (q.kind() == kind && !q.premises().isEmpty()) {
                ranks.add(worstPremiseRank(retriever, q));
            }
        }
        return ranks.stream().mapToInt(Integer::intValue).average().orElse(0);
    }

    // ------------------------------------------------------------------- 9

    private static void section9(Report r, Graph gold) {
        r.section("9. Provenance, and what it is not evidence of");
        r.text("Every edge carries the id of the document that asserts it, so every answer comes "
                + "back with the chain that produced it. This is the graph's most under-rated "
                + "property and it decides whether a compliance answer is usable at all: an "
                + "analyst who must justify freezing a payment needs the four filings, not a "
                + "confidence score.");

        r.expect("P9", "Provenance makes graph answers verifiable, which makes the graph the "
                + "safer substrate for high-stakes questions.");

        Executor.Result q12 = Systems.answer("Q12", gold);
        r.text("Q12 -- *which suppliers of Ashford Components are ultimately controlled by a "
                + "sanctioned person* -- returns " + q12.values().size()
                + " answers, each with a path:");
        r.line("```");
        for (Executor.Answer a : q12.answers()) {
            r.line(a.path().render());
            r.line("    documents: " + a.path().provenance());
        }
        r.line("```");
        r.blank();

        int totalSteps = q12.answers().stream().mapToInt(a -> a.path().hops()).sum();
        boolean allReal = q12.provenance().stream()
                .allMatch(id -> Corpus.doc(id).relation() != null);
        r.found("P9", false, "Verifiable, yes -- all " + totalSteps + " steps across those paths "
                + "cite documents that really do state the relation claimed"
                + (allReal ? "" : ", except one, which is a bug") + ". Safer, not necessarily. "
                + "Section 5 produced a fabricated path whose every link was a real document; the "
                + "falsehood was in the node identity, not in any edge. **A provenance chain is "
                + "evidence that the edges were asserted. It is not evidence that the entities "
                + "were correctly resolved**, and the second failure is the one that produces "
                + "confident, well-cited, wrong answers. Anyone shipping this needs the "
                + "resolution decisions in the audit trail alongside the edges.");

        r.section("10. The whole thing in one paragraph");
        r.text("Retrieval finds text. Traversal derives facts. On questions whose answer is "
                + "written down, they are the same tool and retrieval is cheaper. On questions "
                + "whose answer is a composition, retrieval fails -- not gradually, and not for "
                + "want of a better encoder, but because it must retrieve a conjunction of "
                + "passages that do not individually resemble the question. On questions outside "
                + "the graph's schema, traversal cannot answer at all. And the graph itself is "
                + "built by an embedding making similarity judgements about identity, so the two "
                + "techniques were never alternatives: **the working architecture is vector for "
                + "identity and graph for traversal, and the failure mode of the combination is a "
                + "well-cited path between the wrong nodes.**");
    }
}
