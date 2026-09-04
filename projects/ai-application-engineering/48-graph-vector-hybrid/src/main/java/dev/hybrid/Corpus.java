package dev.hybrid;

import java.util.*;

/**
 * The corpus: 78 single-fact documents about corporate ownership and supply
 * relationships, plus the entity name variants that make resolution necessary.
 *
 * <p>Three properties are deliberate and every measurement depends on them.
 *
 * <p><b>One relation per document.</b> Each document states exactly one triple.
 * This makes "the premises required to answer question Q" a finite, checkable
 * set of document ids rather than a judgement call, so retrieval recall can be
 * measured against ground truth instead of against a rubric. A corpus of long
 * passages would make the multi-hop measurement unfalsifiable, which is
 * convenient and is why most retrieval benchmarks are.
 *
 * <p><b>No document states a derived fact.</b> Nothing in the corpus says that
 * Meridian Shipping is ultimately controlled by a sanctioned person, because
 * nothing in the real world says so either -- that fact exists only as the
 * composition of four separate filings. This is the entire point. A retriever
 * can only return text that exists.
 *
 * <p><b>Names are inconsistent, and two of the inconsistencies are traps.</b>
 * "Halcyon Trading Co" and "Halcyon Trading Company" are one entity; a
 * resolver that splits them breaks a true path. "Meridian Shipping Ltd" and
 * "Meridian Freight Services" are two entities; a resolver that merges them
 * invents a path to a sanctioned owner that does not exist. Both errors are
 * silent and both compound with hop distance.
 */
public final class Corpus {

    public record Doc(int id, String text, String subject, String relation, String object) {}

    public record Ent(String canonical, List<String> variants, String type) {}

    public enum Kind { LOOKUP, PARAPHRASE, MULTIHOP, AGGREGATE, NEGATIVE, OPEN }

    /**
     * A question with checkable ground truth.
     *
     * @param premises the document ids that must all be retrieved for a
     *                 text-only system to have any chance; the point of the
     *                 project is that this is necessary and not sufficient
     * @param hops     edges in the answer path; 1 means the answer is stated
     */
    public record Question(String id, String text, Kind kind, int hops,
                           List<Integer> premises, Set<String> answer) {}

    private Corpus() {}

    // ---------------------------------------------------------------- entities

    public static List<Ent> entities() {
        List<Ent> e = new ArrayList<>();
        e.add(new Ent("Meridian Shipping Ltd",
                List.of("Meridian Shipping Ltd", "Meridian Shipping Limited", "Meridian Shipping",
                        "MERIDIAN SHIPPING LTD"), "company"));
        // The trap. Similar name, unrelated entity, no ownership link to
        // Silverline. Merging it with Meridian Shipping fabricates a path from
        // Ravenna Textiles to a sanctioned individual.
        e.add(new Ent("Meridian Freight Services",
                List.of("Meridian Freight Services", "Meridian Freight"), "company"));
        e.add(new Ent("Halcyon Trading Co",
                List.of("Halcyon Trading Co", "Halcyon Trading Company", "Halcyon Trading"),
                "company"));
        e.add(new Ent("Baltic Freight AG", List.of("Baltic Freight AG", "Baltic Freight"), "company"));
        e.add(new Ent("Silverline Holdings",
                List.of("Silverline Holdings", "Silverline Holdings SA", "Silverline"), "company"));
        e.add(new Ent("Northwind Logistics",
                List.of("Northwind Logistics", "Northwind Logistics BV", "Northwind"), "company"));
        e.add(new Ent("Ashford Components",
                List.of("Ashford Components", "Ashford Components plc"), "company"));
        e.add(new Ent("Pemberton Metals", List.of("Pemberton Metals", "Pemberton Metals Ltd"), "company"));
        e.add(new Ent("Vector Marine Services", List.of("Vector Marine Services", "Vector Marine"), "company"));
        e.add(new Ent("Orion Chartering", List.of("Orion Chartering", "Orion Chartering Ltd"), "company"));
        e.add(new Ent("Delta Bunkering", List.of("Delta Bunkering", "Delta Bunkering Ltd"), "company"));
        e.add(new Ent("Kestrel Maritime", List.of("Kestrel Maritime", "Kestrel Maritime SA"), "company"));
        e.add(new Ent("Ravenna Textiles", List.of("Ravenna Textiles", "Ravenna Textiles SpA"), "company"));
        e.add(new Ent("Sable Chemical Works", List.of("Sable Chemical Works", "Sable Chemical"), "company"));
        e.add(new Ent("Torrent Energy Partners", List.of("Torrent Energy Partners", "Torrent Energy"), "company"));
        e.add(new Ent("Quill and Sons", List.of("Quill and Sons", "Quill & Sons"), "company"));
        e.add(new Ent("Viktor Anisimov", List.of("Viktor Anisimov", "V. Anisimov", "Anisimov"), "person"));
        e.add(new Ent("Elena Marchetti", List.of("Elena Marchetti", "E. Marchetti"), "person"));
        e.add(new Ent("James Okonkwo", List.of("James Okonkwo", "J. Okonkwo"), "person"));
        e.add(new Ent("Petra Lindqvist", List.of("Petra Lindqvist", "P. Lindqvist"), "person"));
        e.add(new Ent("Cyprus", List.of("Cyprus"), "jurisdiction"));
        e.add(new Ent("Malta", List.of("Malta"), "jurisdiction"));
        e.add(new Ent("Netherlands", List.of("Netherlands", "the Netherlands"), "jurisdiction"));
        e.add(new Ent("Singapore", List.of("Singapore"), "jurisdiction"));
        e.add(new Ent("Panama", List.of("Panama"), "jurisdiction"));
        e.add(new Ent("OFAC SDN List", List.of("OFAC SDN List", "the OFAC list", "SDN List"), "list"));
        return List.copyOf(e);
    }

    // --------------------------------------------------------------- documents

    private static final List<Doc> DOCS = build();

    public static List<Doc> documents() {
        return DOCS;
    }

    public static Doc doc(int id) {
        return DOCS.get(id);
    }

    private static List<Doc> build() {
        List<Doc> d = new ArrayList<>();
        // The long chain: Ashford <- Meridian <- Baltic <- Silverline <- Anisimov (sanctioned).
        // Each link is stated in isolation, with a different surface form of the
        // entity, exactly as separate filings by separate registrars would be.
        add(d, "Meridian Shipping Limited is a registered supplier to Ashford Components plc.",
                "Meridian Shipping Ltd", "SUPPLIES", "Ashford Components");
        add(d, "MERIDIAN SHIPPING LTD is a wholly owned subsidiary of Baltic Freight AG.",
                "Meridian Shipping Ltd", "SUBSIDIARY_OF", "Baltic Freight AG");
        add(d, "Baltic Freight AG is controlled by Silverline Holdings SA.",
                "Baltic Freight AG", "SUBSIDIARY_OF", "Silverline Holdings");
        add(d, "Silverline Holdings is beneficially owned by Viktor Anisimov.",
                "Silverline Holdings", "OWNED_BY", "Viktor Anisimov");
        add(d, "V. Anisimov was added to the OFAC SDN List in March 2022.",
                "Viktor Anisimov", "LISTED_ON", "OFAC SDN List");

        // A shorter chain to the same conclusion, so multi-hop questions have
        // more than one true answer and a system cannot get full marks by
        // memorising one path.
        add(d, "Kestrel Maritime SA supplies bunker fuel to Ashford Components.",
                "Kestrel Maritime", "SUPPLIES", "Ashford Components");
        add(d, "Kestrel Maritime is owned by Silverline Holdings.",
                "Kestrel Maritime", "OWNED_BY", "Silverline Holdings");

        // The Halcyon chain -- clean two-hop, and the surface forms differ.
        add(d, "Northwind Logistics BV supplies rolled steel to Pemberton Metals.",
                "Northwind Logistics", "SUPPLIES", "Pemberton Metals");
        add(d, "Northwind Logistics is a subsidiary of Halcyon Trading Company.",
                "Northwind Logistics", "SUBSIDIARY_OF", "Halcyon Trading Co");
        add(d, "Halcyon Trading Co is registered in Singapore.",
                "Halcyon Trading Co", "REGISTERED_IN", "Singapore");
        add(d, "Halcyon Trading is owned by Petra Lindqvist.",
                "Halcyon Trading Co", "OWNED_BY", "Petra Lindqvist");

        // The trap entity. Meridian Freight Services has its own, innocent
        // ownership, and supplies a company that is otherwise unconnected.
        add(d, "Meridian Freight Services supplies packaging to Ravenna Textiles SpA.",
                "Meridian Freight Services", "SUPPLIES", "Ravenna Textiles");
        add(d, "Meridian Freight is owned by James Okonkwo.",
                "Meridian Freight Services", "OWNED_BY", "James Okonkwo");
        add(d, "Meridian Freight Services is registered in the Netherlands.",
                "Meridian Freight Services", "REGISTERED_IN", "Netherlands");

        // Directors.
        add(d, "Elena Marchetti serves as a director of Meridian Shipping.",
                "Elena Marchetti", "DIRECTOR_OF", "Meridian Shipping Ltd");
        add(d, "E. Marchetti is also a director of Orion Chartering Ltd.",
                "Elena Marchetti", "DIRECTOR_OF", "Orion Chartering");
        add(d, "James Okonkwo is a director of Ravenna Textiles.",
                "James Okonkwo", "DIRECTOR_OF", "Ravenna Textiles");
        add(d, "Petra Lindqvist is a director of Northwind Logistics.",
                "Petra Lindqvist", "DIRECTOR_OF", "Northwind Logistics");
        add(d, "Viktor Anisimov is a director of Baltic Freight.",
                "Viktor Anisimov", "DIRECTOR_OF", "Baltic Freight AG");

        // Registrations.
        add(d, "Meridian Shipping Ltd is registered in Cyprus.",
                "Meridian Shipping Ltd", "REGISTERED_IN", "Cyprus");
        add(d, "Baltic Freight AG is registered in Malta.",
                "Baltic Freight AG", "REGISTERED_IN", "Malta");
        add(d, "Silverline Holdings SA is registered in Panama.",
                "Silverline Holdings", "REGISTERED_IN", "Panama");
        add(d, "Ashford Components plc is registered in the Netherlands.",
                "Ashford Components", "REGISTERED_IN", "Netherlands");
        add(d, "Pemberton Metals Ltd is registered in Singapore.",
                "Pemberton Metals", "REGISTERED_IN", "Singapore");
        add(d, "Kestrel Maritime SA is registered in Panama.",
                "Kestrel Maritime", "REGISTERED_IN", "Panama");
        add(d, "Orion Chartering is registered in Cyprus.",
                "Orion Chartering", "REGISTERED_IN", "Cyprus");
        add(d, "Ravenna Textiles is registered in Malta.",
                "Ravenna Textiles", "REGISTERED_IN", "Malta");
        add(d, "Vector Marine Services is registered in Singapore.",
                "Vector Marine Services", "REGISTERED_IN", "Singapore");
        add(d, "Delta Bunkering Ltd is registered in Cyprus.",
                "Delta Bunkering", "REGISTERED_IN", "Cyprus");
        add(d, "Sable Chemical Works is registered in the Netherlands.",
                "Sable Chemical Works", "REGISTERED_IN", "Netherlands");
        add(d, "Torrent Energy Partners is registered in Panama.",
                "Torrent Energy Partners", "REGISTERED_IN", "Panama");
        add(d, "Quill and Sons is registered in Malta.",
                "Quill and Sons", "REGISTERED_IN", "Malta");
        add(d, "Northwind Logistics BV is registered in the Netherlands.",
                "Northwind Logistics", "REGISTERED_IN", "Netherlands");

        // A supply web dense enough that traversal has to actually search.
        add(d, "Vector Marine supplies navigation equipment to Meridian Shipping.",
                "Vector Marine Services", "SUPPLIES", "Meridian Shipping Ltd");
        add(d, "Delta Bunkering supplies fuel to Orion Chartering.",
                "Delta Bunkering", "SUPPLIES", "Orion Chartering");
        add(d, "Orion Chartering supplies chartering services to Ashford Components.",
                "Orion Chartering", "SUPPLIES", "Ashford Components");
        add(d, "Sable Chemical Works supplies solvents to Ravenna Textiles.",
                "Sable Chemical Works", "SUPPLIES", "Ravenna Textiles");
        add(d, "Torrent Energy supplies electricity to Sable Chemical.",
                "Torrent Energy Partners", "SUPPLIES", "Sable Chemical Works");
        add(d, "Quill & Sons supplies dyes to Ravenna Textiles.",
                "Quill and Sons", "SUPPLIES", "Ravenna Textiles");
        add(d, "Pemberton Metals supplies fittings to Vector Marine Services.",
                "Pemberton Metals", "SUPPLIES", "Vector Marine Services");
        add(d, "Halcyon Trading supplies raw materials to Quill and Sons.",
                "Halcyon Trading Co", "SUPPLIES", "Quill and Sons");
        add(d, "Delta Bunkering is owned by Elena Marchetti.",
                "Delta Bunkering", "OWNED_BY", "Elena Marchetti");
        add(d, "Vector Marine Services is owned by Petra Lindqvist.",
                "Vector Marine Services", "OWNED_BY", "Petra Lindqvist");
        add(d, "Orion Chartering is a subsidiary of Silverline Holdings.",
                "Orion Chartering", "SUBSIDIARY_OF", "Silverline Holdings");
        add(d, "Sable Chemical Works is owned by James Okonkwo.",
                "Sable Chemical Works", "OWNED_BY", "James Okonkwo");
        add(d, "Torrent Energy Partners is owned by Petra Lindqvist.",
                "Torrent Energy Partners", "OWNED_BY", "Petra Lindqvist");
        add(d, "Quill and Sons is owned by Elena Marchetti.",
                "Quill and Sons", "OWNED_BY", "Elena Marchetti");
        add(d, "Ravenna Textiles is a subsidiary of Halcyon Trading Co.",
                "Ravenna Textiles", "SUBSIDIARY_OF", "Halcyon Trading Co");
        add(d, "Pemberton Metals is owned by James Okonkwo.",
                "Pemberton Metals", "OWNED_BY", "James Okonkwo");
        add(d, "Ashford Components is owned by Elena Marchetti.",
                "Ashford Components", "OWNED_BY", "Elena Marchetti");

        // Distractor documents. No relation, high lexical overlap with the
        // questions. These exist so that top-k retrieval has something plausible
        // and wrong to return, which is the realistic failure -- an empty result
        // is obvious, a confident irrelevant one is not.
        addNoise(d, "Ashford Components plc reported record quarterly revenue driven by shipping demand.");
        addNoise(d, "Sanctions compliance costs across the shipping sector rose sharply in 2022.");
        addNoise(d, "The OFAC SDN List is maintained by the US Treasury and updated continuously.");
        addNoise(d, "Meridian Shipping announced a new vessel order at the Singapore maritime expo.");
        addNoise(d, "Baltic Freight AG opened a Malta office to serve Mediterranean routes.");
        addNoise(d, "Silverline Holdings declined to comment on questions about its ownership.");
        addNoise(d, "Ownership structures registered in Panama are frequently opaque to investigators.");
        addNoise(d, "Northwind Logistics was named supplier of the year by a trade association.");
        addNoise(d, "Halcyon Trading expanded its Singapore warehousing capacity last quarter.");
        addNoise(d, "Ravenna Textiles reported a supply disruption affecting dye deliveries.");
        addNoise(d, "Kestrel Maritime SA operates a fleet of twelve bulk carriers.");
        addNoise(d, "Vector Marine Services published a whitepaper on navigation safety.");
        addNoise(d, "Sanctioned individuals are prohibited from holding beneficial ownership in EU entities.");
        addNoise(d, "Pemberton Metals invested in a new rolling mill in Singapore.");
        addNoise(d, "Delta Bunkering faced scrutiny over fuel quality at Cyprus terminals.");
        addNoise(d, "Orion Chartering signed a long-term charter with an undisclosed counterparty.");
        addNoise(d, "Torrent Energy Partners commissioned a solar array in Panama.");
        addNoise(d, "Quill & Sons celebrated its centenary with a new product line.");
        addNoise(d, "Corporate registries in Cyprus and Malta are not consistently machine-readable.");
        addNoise(d, "A director may serve on the boards of multiple unrelated companies.");
        addNoise(d, "Beneficial ownership is not the same as legal ownership under most jurisdictions.");
        addNoise(d, "Supply chain due diligence requires tracing relationships beyond direct suppliers.");
        addNoise(d, "Elena Marchetti spoke at a maritime logistics conference in Rotterdam.");
        addNoise(d, "James Okonkwo was profiled in a trade magazine on textile manufacturing.");
        addNoise(d, "Petra Lindqvist has twenty years of experience in Nordic freight operations.");
        addNoise(d, "Sable Chemical Works upgraded its effluent treatment plant.");
        addNoise(d, "Meridian Freight Services rebranded its packaging division.");
        addNoise(d, "Trade sanctions enforcement increasingly targets indirect ownership chains.");
        addNoise(d, "Ashford Components uses a tiered supplier qualification process.");
        addNoise(d, "The maritime sector accounts for a large share of sanctions evasion cases.");
        return List.copyOf(d);
    }

    private static void add(List<Doc> d, String text, String s, String r, String o) {
        d.add(new Doc(d.size(), text, s, r, o));
    }

    private static void addNoise(List<Doc> d, String text) {
        d.add(new Doc(d.size(), text, null, null, null));
    }

    /** Documents that state a relation, as opposed to the distractors. */
    public static List<Doc> factual() {
        return DOCS.stream().filter(x -> x.relation() != null).toList();
    }

    // --------------------------------------------------------------- questions

    public static List<Question> questions() {
        List<Question> q = new ArrayList<>();

        // --- 1 hop: the answer is stated verbatim in a single document.
        q.add(new Question("Q1", "Who is a director of Meridian Shipping?", Kind.LOOKUP, 1,
                List.of(14), Set.of("Elena Marchetti")));
        q.add(new Question("Q2", "Where is Baltic Freight AG registered?", Kind.LOOKUP, 1,
                List.of(20), Set.of("Malta")));
        q.add(new Question("Q3", "Who owns Silverline Holdings?", Kind.LOOKUP, 1,
                List.of(3), Set.of("Viktor Anisimov")));
        q.add(new Question("Q4", "Which company supplies Pemberton Metals?", Kind.LOOKUP, 1,
                List.of(7), Set.of("Northwind Logistics")));

        // --- 1 hop, paraphrased: the answer is stated, but not in the words of
        // the question. This is the class a better encoder improves and a graph
        // does not, and it is in the corpus so the report can say so honestly.
        q.add(new Question("Q5", "Which firm has its corporate seat in Panama and is held by Anisimov?",
                Kind.PARAPHRASE, 1, List.of(3, 21), Set.of("Silverline Holdings")));
        q.add(new Question("Q6", "Who sits on the board at Orion Chartering?", Kind.PARAPHRASE, 1,
                List.of(15), Set.of("Elena Marchetti")));

        // --- 2 hops: composition of two stated facts. Nothing states the answer.
        q.add(new Question("Q7", "Who is the ultimate parent of Meridian Shipping's parent?",
                Kind.MULTIHOP, 2, List.of(1, 2), Set.of("Silverline Holdings")));
        q.add(new Question("Q8", "Which company is the parent of Pemberton Metals' supplier?",
                Kind.MULTIHOP, 2, List.of(7, 8), Set.of("Halcyon Trading Co")));
        q.add(new Question("Q9", "In which jurisdiction is the parent of Northwind Logistics registered?",
                Kind.MULTIHOP, 2, List.of(8, 9), Set.of("Singapore")));

        // --- 3 hops.
        q.add(new Question("Q10", "Who beneficially owns the ultimate parent of Meridian Shipping?",
                Kind.MULTIHOP, 3, List.of(1, 2, 3), Set.of("Viktor Anisimov")));
        q.add(new Question("Q11", "Who owns the parent of the company that supplies Pemberton Metals?",
                Kind.MULTIHOP, 3, List.of(7, 8, 10), Set.of("Petra Lindqvist")));

        // --- 4 hops: the question the whole project exists for.
        q.add(new Question("Q12",
                "Which suppliers of Ashford Components are ultimately controlled by a sanctioned person?",
                Kind.MULTIHOP, 4, List.of(0, 1, 2, 3, 4, 5, 6, 35, 43),
                Set.of("Meridian Shipping Ltd", "Kestrel Maritime", "Orion Chartering")));

        // --- Aggregation: counting and set-building, which retrieval cannot do
        // at all because the answer is a property of the whole corpus.
        q.add(new Question("Q13", "How many companies are registered in Panama?", Kind.AGGREGATE, 1,
                List.of(21, 24, 30), Set.of("3")));
        q.add(new Question("Q14", "Which companies does Elena Marchetti own or direct?",
                Kind.AGGREGATE, 1, List.of(14, 15, 41, 46, 49), Set.of("Delta Bunkering",
                        "Quill and Sons", "Ashford Components", "Meridian Shipping Ltd",
                        "Orion Chartering")));

        // --- Negative: the correct answer is "nothing". Both systems are scored
        // on whether they can say so, which is a different skill from finding
        // something and is where confident retrieval does the most damage.
        q.add(new Question("Q15", "Which suppliers of Ravenna Textiles are controlled by a sanctioned person?",
                Kind.NEGATIVE, 4, List.of(), Set.of()));
        q.add(new Question("Q16", "Is Meridian Freight Services owned by Silverline Holdings?",
                Kind.NEGATIVE, 1, List.of(), Set.of()));

        // --- Open: the answer is in the corpus, in plain text, and is not in
        // the graph's schema. These exist because a comparison that only asks
        // questions the graph was built to answer is not a comparison. The
        // extractor recognises five relation types; the corpus, like every real
        // corpus, contains facts that are none of them.
        q.add(new Question("Q17", "Who maintains the OFAC SDN List?", Kind.OPEN, 1,
                List.of(52), Set.of("US Treasury")));
        q.add(new Question("Q18", "Why are Cyprus and Malta registries hard to work with?",
                Kind.OPEN, 1, List.of(68), Set.of("not consistently machine-readable")));
        q.add(new Question("Q19", "How many vessels does Kestrel Maritime operate?", Kind.OPEN, 1,
                List.of(60), Set.of("twelve")));
        return List.copyOf(q);
    }
}
