using RagAssistant.Application.Evaluation;
using RagAssistant.Domain.Documents;

namespace RagAssistant.Eval;

public static class EvalGoldenDataset
{
    public static IReadOnlyList<GoldenExample> Build(UserPrincipal answerable, UserPrincipal restrictedUser)
    {
        return
        [
            new("q1", "How many days of paid time off do full-time employees receive?", ["HR Policy: Paid time off"], false, answerable),
            new("q2", "What is the PTO carry-over cap?", ["HR Policy: Paid time off"], false, answerable),
            new("q3", "How many paid sick days do employees receive?", ["HR Policy: Paid time off"], false, answerable),
            new("q4", "Can engineering staff work remotely and how many days per week?", ["HR Policy: Remote work"], false, answerable),
            new("q5", "What are the core hours for remote workers?", ["HR Policy: Remote work"], false, answerable),
            new("q6", "What is the domestic travel pre-approval threshold?", ["Expense Policy: Travel and reimbursements"], false, answerable),
            new("q7", "What is the dinner reimbursement cap?", ["Expense Policy: Travel and reimbursements"], false, answerable),
            new("q8", "Who approves corporate credit card requests?", ["Expense Policy: Corporate credit cards"], false, answerable, AcceptableAlternates: ["Expense Policy: Travel and reimbursements"]),
            new("q9", "How quickly must employees report phishing attempts?", ["Security Policy: Acceptable use"], false, answerable),
            new("q10", "Who must be notified within 30 minutes of a Severity 1 security incident?", ["Security Policy: Incident response"], false, answerable, AcceptableAlternates: ["Incident Runbook: Payment ingestion outage"]),
            new("q11", "What is the mitigation for a payment ingestion outage?", ["Incident Runbook: Payment ingestion outage"], false, answerable),
            new("q12", "How long must warehouse safety incidents be reported within?", ["Operations Standard: Safety training", "Incident Runbook: Warehouse robotics stop"], false, answerable),
            new("q13", "When must code reviewers involve the security team?", ["Engineering Standard: Code review"], false, answerable),
            new("q14", "What day of the week is production deployment restricted after 14:00?", ["Engineering Standard: Deployment"], false, answerable),
            new("q15", "What are the four data classifications used at Acme?", ["Engineering Standard: Data classification"], false, answerable),
            new("q16", "How often are order fulfilment reconciliations run?", ["Operations Runbook: Order fulfilment reconciliation"], false, answerable),
            new("q17", "When are performance reviews held?", ["HR Policy: Performance reviews"], false, answerable),
            new("q18", "What is the on-call acknowledgement time limit?", ["Engineering Standard: On-call rotation"], false, answerable),
            new("q19", "Are vendors allowed to use shared credentials?", ["Security Policy: Vendor access"], false, answerable),
            new("q20", "How often must warehouse employees complete safety training?", ["Operations Standard: Safety training"], false, answerable),
            new("q21", "What length must new passwords be?", ["IT Support: Password reset"], false, answerable),
            new("q22", "Who receives physical building access badges?", ["Facilities: Building access"], false, answerable),
            new("q23", "What are the principles in the Acme code of conduct?", ["Public: Company code of conduct"], false, answerable),
            new("q24", "What is the CEO LTIP pool for the current fiscal year?", ["Board-only compensation policy (RESTRICTED)"], true, restrictedUser),
            new("q25", "Which acquisitions is Acme evaluating right now?", ["M&A Pipeline (RESTRICTED)"], true, restrictedUser),
            new("q26", "How do employees reset their password if the second factor is lost?", ["IT Support: Password reset"], false, answerable),
            new("q27", "How long are vendor account audit logs retained?", ["Security Policy: Vendor access"], false, answerable),

            // Paraphrase slice — deliberately synonym-heavy / vocabulary-mismatched queries.
            // These target the case hybrid retrieval exists to solve (query vocabulary != document vocabulary).
            new("p1", "How much annual leave am I entitled to?", ["HR Policy: Paid time off"], false, answerable, Tag: "paraphrase"),
            new("p2", "Am I allowed to work from home?", ["HR Policy: Remote work"], false, answerable, Tag: "paraphrase"),
            new("p3", "Meal allowance limit during business trips?", ["Expense Policy: Travel and reimbursements"], false, answerable, Tag: "paraphrase"),
            new("p4", "What happens if I spot a suspicious email?", ["Security Policy: Acceptable use"], false, answerable, Tag: "paraphrase"),
            new("p5", "Payment service down — what should the on-call engineer do?", ["Incident Runbook: Payment ingestion outage"], false, answerable, Tag: "paraphrase"),
            new("p6", "Can we ship a release on a Friday afternoon?", ["Engineering Standard: Deployment"], false, answerable, Tag: "paraphrase"),
            new("p7", "How do outside contractors get into our systems?", ["Security Policy: Vendor access"], false, answerable, Tag: "paraphrase"),
            new("p8", "I forgot my authenticator token — how do I get back in?", ["IT Support: Password reset"], false, answerable, Tag: "paraphrase"),
        ];
    }
}

