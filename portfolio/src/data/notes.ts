import type { EngineeringNote } from "./types";

// Three engineering notes distilled from the three case-study projects
// (35, 43, 50). These are first-person accounts of a self-directed
// technical study, not employer material: no teams, no direct reports, no
// business savings figures and no production performance guarantees are
// claimed anywhere below — only the bounded, sampled findings the projects
// actually produced.
export const notes: EngineeringNote[] = [
  {
    slug: "boundary-is-where-modernization-risk-lives",
    title: "The boundary is where modernization risk lives",
    summary:
      "A controlled C++/.NET interop study that separates boundary hardening from compiler settings, so a hostile-input comparison and a compiler comparison each isolate exactly one variable.",
    published: "2026-09-10",
    projectNumber: 35,
    topic: "Modernization interop",
    sections: [
      {
        heading: "The claim and the constraint",
        paragraphs: [
          "A modernization study is informative when its comparisons isolate the relevant variables. Project 35 keeps the computational engine source unchanged across three builds, varying the boundary code and compiler settings in separate controlled comparisons.",
          "That constraint sounds restrictive, but it is what makes the comparison meaningful. If a hardened boundary and a legacy boundary behave differently around the same unchanged core, the difference is attributable to the boundary. If two builds of the identical source differ, the difference is attributable to the compiler, not to a quietly rewritten formula.",
        ],
      },
      {
        heading: "What the hostile-input comparison showed",
        paragraphs: [
          "The documented experiment ran the same 600 sampled hostile inputs through both boundaries. The legacy boundary produced an unsafe outcome on 328 of those 600 inputs. The hardened boundary, given the identical 600 inputs, produced zero unsafe outcomes.",
          "I want to be precise about what that does and doesn't say. This is bounded evidence over one documented, sampled input set in one local environment — it is not a universal safety proof, and there is no real 60,000-line customer system behind it. The scale of \"a large legacy core\" is a constructed stand-in for the class of problem, not a claim about a specific client codebase. The finding generalizes only as far as the sampled inputs represent the hostile-input space a real system would actually see.",
        ],
      },
      {
        heading: "Compilers are part of the compatibility surface",
        paragraphs: [
          "A separate compiler comparison rebuilt the identical engine source with fast-floating-point settings. With zero engine-source changes, 2,818 of 4,000 sampled numeric output positions differed between the two builds.",
          "The practical lesson is that a modernization sign-off which only diffs source code is not sufficient. The full build — compiler, flags and target architecture — is part of what is being migrated, and it needs its own compatibility check, separate from a source review.",
        ],
      },
      {
        heading: "What this does and doesn't establish",
        paragraphs: [
          "This was a local Windows, MSVC and .NET experiment around a deliberately constrained core, not a customer engagement. It does not establish cutover safety for a real system, financial certification, or Azure deployment reliability.",
          "Before I would treat any of this as evidence for a real migration, I would want domain-approved fixtures in place of synthetic inputs, workload-specific compatibility checks, shadow traffic run against the legacy path, and a staged rollback plan. Those are next steps I would take, not project outcomes I am claiming already happened.",
        ],
      },
    ],
  },
  {
    slug: "diagnosing-retrieval-evidence-loss",
    title: "Diagnosing where a retrieval pipeline loses evidence",
    summary:
      "An offline evaluation lab that attributes evidence loss to a specific pipeline stage — chunking, retrieval or ranking — using a synthetic corpus with known evidence spans and a predict-then-measure discipline.",
    published: "2026-09-10",
    projectNumber: 43,
    topic: "Retrieval evaluation",
    sections: [
      {
        heading: "Why one aggregate score doesn't tell you what to fix",
        paragraphs: [
          "A retrieval pipeline can lose the evidence a query needs at more than one stage: the chunker can separate a fact from the context that qualifies it, the retriever can rank the right chunk too low, or the final selection step can drop it. A single end-to-end retrieval score can't tell you which of those happened.",
          "Project 43 addresses that by building a synthetic corpus where the evidence span each query needs is known in advance, so a miss can be attributed to a specific stage — chunking, retrieval or ranking — rather than reported as one undifferentiated failure rate.",
        ],
      },
      {
        heading: "A controlled comparison: heading context",
        paragraphs: [
          "One controlled comparison held the chunk boundaries fixed and varied only whether each chunk retained the heading context above it. Across the corpus, heading severances — cases where a fact was separated from the heading that qualified it — fell from 45 to 0 between the two configurations.",
          "Because the chunk boundaries themselves did not change, the result isolates heading-context preservation as the variable that mattered in this comparison, rather than where the text happened to be split.",
        ],
      },
      {
        heading: "Registering predictions before measuring",
        paragraphs: [
          "The evaluation harness required stating an expectation before running the measurement that would confirm or refute it — an explicit predict-then-measure discipline rather than narrating results after the fact.",
          "Of 12 predictions registered this way, 7 held and 5 were contradicted by the data. Keeping those contradictory findings visible makes the report auditable and limits post-hoc interpretation.",
        ],
      },
      {
        heading: "The statistical ceiling",
        paragraphs: [
          "The permutation-test budget used for significance testing could not clear the strictest multiple-comparison correction at the sample sizes involved. That bounds how confidently any single \"improvement\" can be claimed from this experiment — some comparisons are suggestive rather than statistically conclusive.",
        ],
      },
      {
        heading: "Scope",
        paragraphs: [
          "The retrieval methods evaluated are lexical and latent-semantic (BM25/TF-IDF-style scoring and LSA), not a trained neural retriever, and there is no answer-generation stage in scope — only retrieval and ranking are measured. The corpus is synthetic, built specifically to have known evidence spans.",
          "What this demonstrates is a controlled attribution methodology for retrieval pipelines, built around the rqlab experiment-orchestration and reporting modules, not a measurement of production RAG answer quality.",
        ],
      },
    ],
  },
  {
    slug: "detecting-ai-quality-regressions-that-dont-throw-errors",
    title: "Detecting AI answer-quality regressions that don't throw errors",
    summary:
      "A simulation of the gap between request-level monitoring and answer-quality monitoring, comparing aggregate and topic-sliced drift detectors on deterministic, non-semantic features under a modeled operating budget.",
    published: "2026-09-10",
    projectNumber: 50,
    topic: "AI observability",
    sections: [
      {
        heading: "The gap application monitoring can't see",
        paragraphs: [
          "Standard request monitoring — status codes, latency, error rates — says nothing about whether the content an AI system returns is still correct. A quality regression can live entirely inside a successful response payload, invisible to conventional APM. Project 50 is a simulation built to study that gap directly, not a production monitoring deployment.",
        ],
      },
      {
        heading: "A feature representation without semantics",
        paragraphs: [
          "The features driving detection are deterministic character 4-gram hashes projected into a 256-dimensional space — not a semantic embedding model, and not connected to any live OpenTelemetry trace pipeline. That keeps the simulation fully reproducible, but it also means the detectors are reacting to lexical and statistical shifts in text, not to meaning.",
        ],
      },
      {
        heading: "Slicing helps, and it costs something",
        paragraphs: [
          "A population-stability-index detector run per topic slice caught a localized regression affecting about 8% of simulated traffic that an aggregate, whole-population detector missed entirely.",
          "The same slicing approach was slower on a different scenario: a diffuse, population-wide regression was detected roughly 12 days later by the sliced detector than by the aggregate one in that scenario. That is a real sensitivity-versus-timeliness trade-off inside this simulation, not a general argument that slicing is always better or worse.",
        ],
      },
      {
        heading: "Pricing the detector set, not just the detector",
        paragraphs: [
          "Under a modeled operating-cost assumption — a \"quality canary\" detector priced at roughly 24 calls a day — the canary was not part of the cheapest combination of detectors that still covered every simulated failure scenario; two lower-cost detectors covered the same ground and more. This used simulated budget assumptions and a modeled call volume, not real provider billing or usage.",
        ],
      },
      {
        heading: "What would have to change for production use",
        paragraphs: [
          "There is no real model provider behind this, no live trace data, and no actual customer traffic. Before any of this informed a real observability system, it would need privacy-preserving telemetry, genuine quality labels in place of simulated ones, correlation with provider and model version, and an actual on-call routing path — none of which exist here.",
        ],
      },
    ],
  },
];
