using AgentPlatform.Domain.Prompts;

namespace AgentPlatform.Infrastructure.Catalog;

/// <summary>
/// The versioned prompt templates used by the seeded workflows. Templates are code-defined and
/// version-controlled (not user-uploaded) — the exact version used is recorded on every run and can
/// be A/B compared in evals.
/// </summary>
public static class PromptCatalog
{
    public const string TriageClassify = "triage-classify";
    public const string SummariseDoc = "summarise-doc";

    public static IReadOnlyList<PromptTemplate> All() =>
    [
        new PromptTemplate(
            id: "prompt-triage-classify-v1",
            name: TriageClassify,
            version: 1,
            body: "You are a deterministic support-triage assistant. Read the ticket context and decide "
                + "exactly one of: auto_resolve (safe, low-risk, self-serve) or escalate (needs a human). "
                + "You may call search_knowledge_base at most once to ground your answer. Never take a "
                + "mutating or external action yourself. Respond with the single decision word.\n\n"
                + "Ticket context:\n{{context}}",
            description: "Support-ticket triage classification (v1)."),

        new PromptTemplate(
            id: "prompt-triage-classify-v2",
            name: TriageClassify,
            version: 2,
            body: "Classify the following support ticket for routing. Output only 'auto_resolve' for "
                + "routine low-risk requests, or 'escalate' when it involves anger, legal/GDPR/fraud "
                + "signals, or anything ambiguous. You may consult the knowledge base once. Do not act.\n\n"
                + "Context:\n{{context}}",
            description: "Support-ticket triage classification (v2, more explicit escalation cues)."),

        new PromptTemplate(
            id: "prompt-summarise-doc-v1",
            name: SummariseDoc,
            version: 1,
            body: "You are a careful summarisation assistant. Summarise the document faithfully and "
                + "concisely. Do not invent facts, figures or names that are not present. You may call "
                + "summarise_document to help. Structured field extraction is performed by deterministic "
                + "code afterwards.\n\nDocument:\n{{document}}",
            description: "Document summarisation grounding prompt (v1)."),
    ];
}
