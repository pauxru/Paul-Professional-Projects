using RagAssistant.Application.Chunking;
using RagAssistant.Domain.Documents;

namespace RagAssistant.Infrastructure.Seeding;

public sealed record CorpusEntry(
    string Title,
    string Source,
    string Content,
    AccessControlList Acl,
    ChunkingStrategy Strategy = ChunkingStrategy.SentenceAware);

public static class SyntheticCorpus
{
    public static IReadOnlyList<CorpusEntry> Build()
    {
        var employees = new AccessControlList(["employee"], [], Classification.Internal);
        var engineering = new AccessControlList(["employee"], ["engineering"], Classification.Internal);
        var finance = new AccessControlList(["employee"], ["finance"], Classification.Internal);
        var hr = new AccessControlList(["employee"], ["hr"], Classification.Internal);
        var security = new AccessControlList(["employee"], ["security"], Classification.Confidential);
        var boardOnly = new AccessControlList(["board"], [], Classification.Restricted);
        var executivesOnly = new AccessControlList(["executive"], [], Classification.Restricted);

        return
        [
            new(
                "Acme Manufacturing – Fictional company overview",
                "corpus/00-overview.md",
                """
                Acme Manufacturing is a FICTIONAL example enterprise used throughout this documentation. Any resemblance to real companies is coincidental. Acme Manufacturing designs industrial pumps and ships them to distributors across three synthetic regions. All figures, policies and incident reports in this corpus are illustrative only.
                Employees are grouped into Engineering, Operations, Finance, HR, Security and Executive departments. Reporting lines flow from department leads to the Chief Operating Officer. The document set below covers policies, runbooks and standards used by the knowledge assistant for retrieval evaluation.
                """,
                new AccessControlList(["employee"], [], Classification.Public)),
            new(
                "HR Policy: Paid time off",
                "corpus/hr-pto.md",
                """
                Every full-time Acme Manufacturing employee accrues 20 days of paid time off per calendar year, prorated on the joining date. Accrual happens on the first of each month at a rate of 1.667 days.
                Paid time off can be carried over up to 5 days into the next year. Anything above the carry-over cap is forfeited on 1 April.
                Sick leave is separate from PTO. Employees receive 10 paid sick days per year and must submit a doctor's note if the absence exceeds three consecutive days.
                All PTO requests must be submitted at least 5 working days in advance via the People portal. Emergency leave may be approved retroactively by the department head.
                """,
                hr),
            new(
                "HR Policy: Remote work",
                "corpus/hr-remote.md",
                """
                Acme Manufacturing supports hybrid work for eligible roles. Engineering, Finance and HR staff may work remotely up to 3 days per week, subject to manager approval.
                Employees must be available during core hours from 10:00 to 15:00 local time and attend a weekly in-person collaboration day scheduled by the department.
                Remote workers must use company-provided laptops with the standard endpoint security agent installed. Personal devices are not permitted for accessing customer data.
                """,
                hr),
            new(
                "Expense Policy: Travel and reimbursements",
                "corpus/finance-expense.md",
                """
                Domestic travel does not require pre-approval when the total trip cost is below 750 USD. Trips above 750 USD require approval from the requester's department head via the finance portal.
                International travel always requires CFO approval and must be booked at least 14 days in advance to secure the corporate rate.
                Employees may claim breakfast up to 15 USD, lunch up to 25 USD and dinner up to 45 USD. Alcohol is never reimbursable.
                Receipts must be submitted within 30 days. Late submissions may be denied at the discretion of the finance controller.
                """,
                finance),
            new(
                "Expense Policy: Corporate credit cards",
                "corpus/finance-cards.md",
                """
                Corporate credit cards are issued to managers and to individual contributors with recurring travel needs. Card requests are approved by the CFO after a background review by HR.
                Personal expenses are strictly prohibited on the corporate card. Any personal charge must be reimbursed to Acme within 5 business days.
                Cardholders must reconcile transactions monthly in the expense portal. Cards with unreconciled transactions for more than 45 days are suspended automatically.
                """,
                finance),
            new(
                "Security Policy: Acceptable use",
                "corpus/security-aup.md",
                """
                All Acme Manufacturing staff must sign the Acceptable Use Policy annually. The policy prohibits sharing credentials, disabling endpoint protection and connecting company devices to untrusted networks without a VPN.
                Employees must report suspected phishing to the Security team within one hour of receipt using the Report Phish button in the corporate email client.
                Downloading production data to personal storage is prohibited. Data classified Confidential or above must remain inside the sanctioned SharePoint tenants and encrypted laptops.
                """,
                security),
            new(
                "Security Policy: Incident response",
                "corpus/security-ir.md",
                """
                Security incidents are triaged by the on-call Security Engineer using the incident response runbook. The runbook covers containment, eradication, recovery and post-incident review.
                Severity 1 incidents are declared when Confidential or Restricted data is at risk. The CISO and General Counsel must be notified within 30 minutes of declaration.
                All communications during an incident occur in the dedicated Slack channel #ir-active. Voice bridges are opened via the paging tool when live coordination is needed.
                A written post-incident review must be completed within 5 business days of resolution and shared with the leadership team.
                """,
                security),
            new(
                "Incident Runbook: Payment ingestion outage",
                "corpus/runbook-payments.md",
                """
                When the payment ingestion service returns 5xx responses for more than three consecutive minutes, the on-call engineer confirms the outage in the monitoring dashboard and pages the Payments team.
                The immediate mitigation is to failover to the standby ingestion cluster using the runbook step 'switch-primary'. The standby cluster is warm and receives streaming replication every 30 seconds.
                Root causes are captured in the incident tracker within 24 hours. Repeat outages within a rolling 7-day window trigger a formal architecture review.
                """,
                engineering),
            new(
                "Incident Runbook: Warehouse robotics stop",
                "corpus/runbook-warehouse.md",
                """
                If a robotics cell in the warehouse fails a safety check, the cell must be locked out immediately by the shift supervisor. No manual intervention is permitted until the safety officer clears the area.
                A ticket is opened in the operations portal with severity 'Ops-Sev-2'. The mechatronics on-call is paged and must acknowledge within 15 minutes.
                A physical inspection with two-person integrity is required before the cell returns to service.
                """,
                new AccessControlList(["employee"], ["operations"], Classification.Internal)),
            new(
                "Engineering Standard: Code review",
                "corpus/eng-code-review.md",
                """
                All production code at Acme Manufacturing goes through a review by at least one other engineer. Reviewers focus on correctness, tests, security and operational readiness.
                Draft pull requests may be opened for early feedback but must be marked ready before requesting review. Comments should be actionable, kind and specific.
                A pull request that changes the payments service or the customer data pipeline requires a second reviewer from the Security team.
                """,
                engineering),
            new(
                "Engineering Standard: Deployment",
                "corpus/eng-deployment.md",
                """
                Services are deployed via the golden pipeline on GitHub Actions. Deployments to production require a passing test suite, a successful staging deploy and an approval from an authorised deployer.
                Rollbacks are executed by the on-call using the deployment portal. Rollback should be preferred over forward-fix for user-visible regressions.
                No production deployment is performed on Fridays after 14:00 unless the change is a rollback or a security fix approved by the CISO.
                """,
                engineering),
            new(
                "Engineering Standard: Data classification",
                "corpus/eng-classification.md",
                """
                All data stores must be tagged with a classification: Public, Internal, Confidential or Restricted. The tag drives retention, encryption at rest and access review cadence.
                Confidential data must be encrypted at rest with an approved KMS key and access reviewed quarterly. Restricted data requires additional two-person approval for read access.
                Engineers must not export production data to laptops. Sandbox environments use synthetic data generated by the DataGen tool.
                """,
                engineering),
            new(
                "Operations Runbook: Order fulfilment reconciliation",
                "corpus/ops-reconciliation.md",
                """
                The order fulfilment reconciliation job runs nightly at 02:00 UTC. It matches shipping manifests to sales orders and flags mismatches for the operations analyst.
                Discrepancies over 250 USD must be investigated within two business days. The analyst records the outcome in the reconciliation portal and files a written summary.
                Repeat discrepancies from the same warehouse trigger an escalation to the Head of Operations.
                """,
                new AccessControlList(["employee"], ["operations"], Classification.Internal)),
            new(
                "HR Policy: Performance reviews",
                "corpus/hr-reviews.md",
                """
                Performance reviews at Acme Manufacturing happen twice a year: a mid-year check-in in July and a full review in January.
                Employees complete a self-assessment covering achievements, growth areas and goals for the next cycle. Managers meet with each report for at least one hour to discuss the review.
                Review outcomes influence merit increases, promotions and bonus multipliers. Calibration meetings are held at the department level to ensure consistency.
                """,
                hr),
            new(
                "Engineering Standard: On-call rotation",
                "corpus/eng-oncall.md",
                """
                Every production service maintains an on-call rotation with a primary and a secondary responder. Rotations are one week long and handovers happen on Monday at 10:00 local time.
                The on-call carries a paging device and must acknowledge pages within 5 minutes. If acknowledgement fails, the secondary is paged automatically.
                Compensation for on-call duty is aligned with the workforce policy. Employees may not be on-call for two consecutive weeks without written consent.
                """,
                engineering),
            new(
                "Security Policy: Vendor access",
                "corpus/security-vendor.md",
                """
                External vendors are granted time-boxed access to Acme systems through the vendor portal. Access requests must include a scope, an expiry date and an owning employee.
                Vendors receive individual accounts, never shared credentials. All actions performed by vendor accounts are audit-logged and retained for 400 days.
                Vendor access to Restricted data is prohibited except under a signed data processing addendum reviewed by legal.
                """,
                security),
            new(
                "Operations Standard: Safety training",
                "corpus/ops-safety-training.md",
                """
                All warehouse and shop-floor employees at Acme Manufacturing must complete safety training every 12 months. The training covers lock-out tag-out, forklift operation and emergency evacuation.
                Records of training completion are stored in the operations LMS. Employees who miss the deadline are pulled from the floor rotation until compliance is restored.
                Safety incidents must be reported within one hour to the safety officer and logged in the incident register.
                """,
                new AccessControlList(["employee"], ["operations"], Classification.Internal)),
            new(
                "Board-only compensation policy (RESTRICTED)",
                "corpus/board-compensation.md",
                """
                RESTRICTED: This document is available to the Acme Manufacturing board only. Executive compensation is composed of base salary, performance bonus and long-term incentive plan (LTIP) grants.
                The CEO LTIP pool for the current fiscal year is set at 3.5 million USD, vesting over four years with a one-year cliff. Individual grants are approved by the compensation committee.
                Bonus multipliers for the executive team are derived from company OKR attainment weighted 60% and individual performance weighted 40%. No employee outside the board should have visibility into these figures.
                """,
                boardOnly),
            new(
                "M&A Pipeline (RESTRICTED)",
                "corpus/ma-pipeline.md",
                """
                RESTRICTED: The following mergers and acquisitions targets are under evaluation by the Acme executive team. Do not share outside this file's ACL.
                Target Alpha (fictional): a robotics integrator, indicative purchase price 42 million USD, expected to close in Q3.
                Target Beta (fictional): a European distributor, indicative purchase price 18 million USD, currently in due diligence.
                Any leak of this information will trigger internal investigation and possible dismissal.
                """,
                executivesOnly),
            new(
                "Public: Company code of conduct",
                "corpus/public-conduct.md",
                """
                Acme Manufacturing (a fictional company used in this documentation) publishes a code of conduct summarising the principles all employees, contractors and partners are expected to follow. This includes integrity, respect, safety and environmental responsibility.
                The code of conduct is reviewed annually and any material change is communicated to all employees through the company newsletter. Violations can be reported anonymously via the ethics hotline.
                """,
                new AccessControlList([], [], Classification.Public)),
            new(
                "IT Support: Password reset",
                "corpus/it-password.md",
                """
                Employees can reset their password via the self-service portal at portal.acme-fictional.local/password. The portal requires the corporate identity provider and a second factor.
                If the second factor is lost, employees must contact the IT service desk from a company-issued device. Password resets over the phone are not supported.
                New passwords must be at least 14 characters, include a number and a symbol, and must not repeat any of the last 10 passwords used.
                """,
                employees),
            new(
                "Facilities: Building access",
                "corpus/facilities-badges.md",
                """
                All permanent employees receive a physical access badge on their first day. The badge grants access to Acme's fictional Nairobi campus buildings A and B.
                Contractors receive time-boxed badges valid for the duration of their contract. Lost badges must be reported to security within 24 hours; the badge is deactivated automatically upon report.
                Building access outside normal hours (08:00-18:00) is limited to on-call engineering, operations and security staff.
                """,
                new AccessControlList(["employee"], [], Classification.Internal)),
        ];
    }
}
