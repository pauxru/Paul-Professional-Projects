using System.Text.Json.Nodes;
using AgentPlatform.Domain.Workflows;

namespace AgentPlatform.Infrastructure.Catalog;

/// <summary>
/// The three seeded, code-defined workflows. Authoring is deliberately code-first and version
/// controlled (not runtime upload of arbitrary graphs) — a security choice that keeps the set of
/// executable workflows closed and reviewable. Each definition is validated at startup.
/// </summary>
public static class WorkflowCatalog
{
    public const string Triage = "support-ticket-triage";
    public const string Summarise = "document-summarisation-extraction";
    public const string Refund = "refund-approval";

    public static IReadOnlyList<WorkflowDefinition> All() =>
    [
        TriageWorkflow(),
        SummarisationWorkflow(),
        RefundWorkflow(),
    ];

    // 1. Support-ticket triage: classify, enrich, auto-respond for low-risk, else escalate.
    private static WorkflowDefinition TriageWorkflow() => new()
    {
        Name = Triage,
        Version = 1,
        Description = "Classify a support ticket, enrich from the knowledge base, and either auto-resolve or escalate.",
        StartStepId = "get_ticket",
        InputVariables = ["ticket_id"],
        Steps =
        [
            new ToolStep
            {
                Id = "get_ticket",
                ToolName = "get_ticket",
                ArgumentsTemplate = new JsonObject { ["ticket_id"] = "$ticket_id" },
                OutputVariable = "ticket",
                Next = "triage_context",
            },
            new TransformStep { Id = "triage_context", TransformId = "triage_context", OutputVariable = "context", Next = "classify" },
            new ModelStep
            {
                Id = "classify",
                PromptTemplateId = PromptCatalog.TriageClassify,
                InputVariable = "context",
                AllowedTools = ["search_knowledge_base"],
                OutputVariable = "triage_decision",
                MaxIterations = 4,
                Next = "branch",
            },
            new ConditionStep
            {
                Id = "branch",
                Variable = "triage_decision",
                Operator = ConditionOperator.Contains,
                Value = "auto_resolve",
                WhenTrue = "auto_resolve",
                WhenFalse = "escalate",
            },
            new ToolStep
            {
                Id = "auto_resolve",
                ToolName = "update_ticket_status",
                ArgumentsTemplate = new JsonObject { ["ticket_id"] = "$ticket.id", ["status"] = "resolved" },
                OutputVariable = "status_update",
                Next = "done_resolved",
            },
            new ToolStep
            {
                Id = "escalate",
                ToolName = "update_ticket_status",
                ArgumentsTemplate = new JsonObject { ["ticket_id"] = "$ticket.id", ["status"] = "escalated" },
                OutputVariable = "status_update_escalated",
                Next = "done_escalated",
            },
            new TerminalStep { Id = "done_resolved", Outcome = WorkflowOutcome.Succeeded, MessageVariable = "triage_decision" },
            new TerminalStep { Id = "done_escalated", Outcome = WorkflowOutcome.Escalated, MessageVariable = "triage_decision" },
        ],
    };

    // 2. Document summarisation & extraction: chunk, summarise (loop), extract, validate, flag.
    private static WorkflowDefinition SummarisationWorkflow() => new()
    {
        Name = Summarise,
        Version = 1,
        Description = "Chunk a document, summarise each chunk, extract structured fields, and flag low-confidence output.",
        StartStepId = "chunk",
        InputVariables = ["document"],
        Steps =
        [
            new TransformStep { Id = "chunk", TransformId = "chunk_document", OutputVariable = "chunks", Next = "loop" },
            new LoopStep
            {
                Id = "loop",
                OverVariable = "chunks",
                ItemVariable = "current_chunk",
                MaxIterations = 8,
                Body =
                [
                    new TransformStep { Id = "summarise_chunk", TransformId = "summarise_chunk", OutputVariable = "chunk_summaries" },
                ],
                Next = "summarise",
            },
            new ModelStep
            {
                Id = "summarise",
                PromptTemplateId = PromptCatalog.SummariseDoc,
                InputVariable = "document",
                AllowedTools = ["summarise_document"],
                OutputVariable = "narrative",
                MaxIterations = 3,
                Next = "extract",
            },
            new TransformStep { Id = "extract", TransformId = "extract_fields", OutputVariable = "extraction", Next = "validate" },
            new TransformStep { Id = "validate", TransformId = "validate_extraction", OutputVariable = "validation", Next = "branch" },
            new ConditionStep
            {
                Id = "branch",
                Variable = "validation.low_confidence",
                Operator = ConditionOperator.Equals,
                Value = true,
                WhenTrue = "flag_review",
                WhenFalse = "done_ok",
            },
            new TerminalStep { Id = "flag_review", Outcome = WorkflowOutcome.Escalated },
            new TerminalStep { Id = "done_ok", Outcome = WorkflowOutcome.Succeeded },
        ],
    };

    // 3. Refund approval: gather evidence, compute eligibility deterministically, require approval, execute.
    private static WorkflowDefinition RefundWorkflow() => new()
    {
        Name = Refund,
        Version = 1,
        Description = "Compute refund eligibility with deterministic code, require human approval, then execute idempotently.",
        StartStepId = "lookup",
        InputVariables = ["customer_id", "order_amount", "currency", "days_since_purchase", "reason_category", "item_returned"],
        Steps =
        [
            new ToolStep
            {
                Id = "lookup",
                ToolName = "lookup_customer",
                ArgumentsTemplate = new JsonObject { ["customer_id"] = "$customer_id" },
                OutputVariable = "customer",
                Next = "build_input",
            },
            new TransformStep { Id = "build_input", TransformId = "build_refund_input", OutputVariable = "refund_input", Next = "compute" },
            new TransformStep { Id = "compute", TransformId = "compute_refund_eligibility", OutputVariable = "eligibility", Next = "eligible_branch" },
            new ConditionStep
            {
                Id = "eligible_branch",
                Variable = "eligibility.eligible",
                Operator = ConditionOperator.Equals,
                Value = true,
                WhenTrue = "build_action",
                WhenFalse = "done_rejected",
            },
            new TransformStep { Id = "build_action", TransformId = "build_proposed_action", OutputVariable = "proposed_refund", Next = "approval" },
            new HumanApprovalStep
            {
                Id = "approval",
                ProposedActionVariable = "proposed_refund",
                ActionTitle = "Approve customer refund",
                TimeoutSeconds = 3600,
                DefaultOnTimeout = ApprovalDefault.Reject,
                OnApprove = "execute",
                OnReject = "done_rejected",
            },
            new ToolStep
            {
                Id = "execute",
                ToolName = "create_refund_request",
                ArgumentsTemplate = new JsonObject
                {
                    ["customer_id"] = "$proposed_refund.customer_id",
                    ["amount_usd"] = "$proposed_refund.amount_usd",
                    ["reason"] = "$proposed_refund.reason",
                },
                OutputVariable = "refund_result",
                Next = "done_success",
            },
            new TerminalStep { Id = "done_success", Outcome = WorkflowOutcome.Succeeded },
            new TerminalStep { Id = "done_rejected", Outcome = WorkflowOutcome.Rejected },
        ],
    };
}
