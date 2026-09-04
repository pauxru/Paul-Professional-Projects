"""The ground truth.

Every evaluation corpus has the same defect: somebody read the documents and
guessed which ones answer each query. Missed relevant passages are invisible,
and every metric computed on top of them is biased in an unknown direction.

This corpus is built the other way round. Facts are declared first, as data.
Documents are *rendered* from the facts, so the set of passages that answer a
query is known exactly rather than assessed, and the labels are complete by
construction. The cost of this choice is that the corpus is easier than reality
in specific ways, which are enumerated in docs/known-limitations.md rather than
being left for a reader to discover.

Structure that matters for the experiment
-----------------------------------------
A fact family is one (subject, attribute) pair. A family may take a different
value per *plan* or per *region*. Those qualifiers are rendered into section
headings, not into the sentence that states the value. So the sentence

    "Retained for 400 days before automatic deletion."

is not an answer to anything on its own -- it only answers a question once you
also know which section it sits in. That is the mechanism this lab exists to
measure: a chunk that contains the answer sentence but not its heading is
worthless, and no reranker can repair it.
"""

from __future__ import annotations

from dataclasses import dataclass

PLANS = ("starter", "business", "enterprise")
REGIONS = ("us", "eu", "apac")

PLAN_LABEL = {"starter": "Starter", "business": "Business", "enterprise": "Enterprise"}
REGION_LABEL = {"us": "US", "eu": "EU", "apac": "APAC"}


@dataclass(frozen=True)
class Family:
    """One (subject, attribute) pair and how its value varies."""

    key: str
    topic: str
    subject: str
    attribute: str
    #: "plan", "region" or "none" -- which qualifier the value depends on.
    varies_by: str
    #: qualifier value -> the stated value. Single key "" when varies_by == "none".
    values: dict[str, str]
    #: Sentence template. {v} is the value; it must NOT mention the qualifier,
    #: which is what forces the heading to be part of the answer.
    template: str
    #: Wording a user who has read the document would use.
    lexical: str
    #: Wording a user who has not.
    paraphrases: tuple[str, ...]
    #: Terms that appear in no other family. Used to check query discriminability.
    anchors: tuple[str, ...] = ()


@dataclass(frozen=True)
class Fact:
    fid: str
    family: Family
    qualifier: str  # "" | plan | region

    @property
    def topic(self) -> str:
        return self.family.topic

    @property
    def subject(self) -> str:
        return self.family.subject

    @property
    def value(self) -> str:
        return self.family.values[self.qualifier]

    @property
    def sentence(self) -> str:
        return self.family.template.format(v=self.value)

    @property
    def qualifier_kind(self) -> str:
        return self.family.varies_by

    @property
    def qualifier_label(self) -> str:
        if self.family.varies_by == "plan":
            return PLAN_LABEL[self.qualifier]
        if self.family.varies_by == "region":
            return REGION_LABEL[self.qualifier]
        return ""


def _f(
    key: str,
    topic: str,
    subject: str,
    attribute: str,
    varies_by: str,
    values: dict[str, str],
    template: str,
    lexical: str,
    paraphrases: tuple[str, ...],
    anchors: tuple[str, ...] = (),
) -> Family:
    return Family(
        key=key,
        topic=topic,
        subject=subject,
        attribute=attribute,
        varies_by=varies_by,
        values=values,
        template=template,
        lexical=lexical,
        paraphrases=paraphrases,
        anchors=anchors,
    )


FAMILIES: tuple[Family, ...] = (
    # ---- retention -------------------------------------------------------
    _f(
        "audit_log_retention", "retention", "audit logs", "retention period", "plan",
        {"starter": "30 days", "business": "180 days", "enterprise": "400 days"},
        "Audit log entries are retained for {v} before automatic deletion.",
        "audit log retention period",
        ("how long are administrator action records kept",
         "when do we stop storing the trail of who changed what"),
        ("audit",),
    ),
    _f(
        "metric_retention", "retention", "metrics", "raw sample retention", "plan",
        {"starter": "14 days", "business": "90 days", "enterprise": "395 days"},
        "Raw metric samples remain queryable for {v}, after which only hourly rollups survive.",
        "raw metric sample retention",
        ("how far back can i graph unaggregated numbers",
         "when do fine grained measurements disappear"),
        ("rollup", "sample"),
    ),
    _f(
        "trace_retention", "retention", "distributed traces", "retention period", "plan",
        {"starter": "3 days", "business": "15 days", "enterprise": "45 days"},
        "Captured traces stay searchable for {v} from the time of ingestion.",
        "distributed trace retention",
        ("how long can i look up a request span",
         "when are recorded call graphs purged"),
        ("trace", "span"),
    ),
    _f(
        "deleted_account_purge", "retention", "deleted accounts", "purge delay", "region",
        {"us": "30 days", "eu": "7 days", "apac": "30 days"},
        "Records belonging to a closed account are erased {v} after closure.",
        "deleted account purge delay",
        ("how soon is data destroyed once someone cancels",
         "when is a closed customer wiped"),
        ("purge", "closure"),
    ),
    _f(
        "backup_retention", "backup", "database backups", "retention period", "plan",
        {"starter": "7 days", "business": "35 days", "enterprise": "365 days"},
        "Point-in-time snapshots are kept for {v}.",
        "database backup retention",
        ("how many days of restore points do we hold",
         "how far back can a restore go"),
        ("snapshot", "point-in-time"),
    ),
    # ---- limits ----------------------------------------------------------
    _f(
        "api_rate_limit", "limits", "the public API", "request rate limit", "plan",
        {"starter": "60 requests per minute", "business": "600 requests per minute",
         "enterprise": "6000 requests per minute"},
        "Clients may issue up to {v} before receiving HTTP 429.",
        "public API request rate limit",
        ("how many calls a minute before throttling",
         "what triggers too many requests errors"),
        ("429", "throttl"),
    ),
    _f(
        "ingest_limit", "limits", "event ingestion", "sustained ingest ceiling", "plan",
        {"starter": "2 MB/s", "business": "40 MB/s", "enterprise": "400 MB/s"},
        "Sustained ingestion above {v} is shed rather than queued.",
        "sustained event ingest ceiling",
        ("how much data can we push in continuously",
         "at what throughput do writes start being dropped"),
        ("shed", "ingest"),
    ),
    _f(
        "query_timeout", "limits", "analytical queries", "server side timeout", "plan",
        {"starter": "10 seconds", "business": "60 seconds", "enterprise": "300 seconds"},
        "A query still running after {v} is cancelled and its partial result discarded.",
        "analytical query server side timeout",
        ("how long before a long report gets killed",
         "when does a slow search give up"),
        ("cancelled",),
    ),
    _f(
        "max_payload", "limits", "the ingest endpoint", "maximum payload size", "none",
        {"": "5 MB"},
        "A single request body may not exceed {v}.",
        "maximum ingest payload size",
        ("how big can one upload be",
         "what is the largest request body accepted"),
        ("payload", "body"),
    ),
    _f(
        "concurrent_exports", "limits", "bulk exports", "concurrency cap", "plan",
        {"starter": "1 concurrent job", "business": "4 concurrent jobs",
         "enterprise": "16 concurrent jobs"},
        "At most {v} may run at the same time per organisation.",
        "bulk export concurrency cap",
        ("how many downloads can run at once",
         "parallel extract job limit"),
        ("export",),
    ),
    # ---- sla -------------------------------------------------------------
    _f(
        "uptime_sla", "sla", "the platform", "monthly uptime commitment", "plan",
        {"starter": "99.0%", "business": "99.9%", "enterprise": "99.95%"},
        "The committed monthly availability is {v}, measured over calendar months.",
        "monthly uptime commitment",
        ("what availability are we promised",
         "how much downtime is contractually allowed"),
        ("availability", "uptime"),
    ),
    _f(
        "support_response", "sla", "critical support tickets", "first response target", "plan",
        {"starter": "next business day", "business": "4 hours", "enterprise": "30 minutes"},
        "A severity-one ticket receives its first human response within {v}.",
        "critical ticket first response target",
        ("how fast does someone reply to an outage report",
         "sev1 acknowledgement time"),
        ("severity", "ticket"),
    ),
    _f(
        "credit_threshold", "sla", "service credits", "eligibility threshold", "plan",
        {"starter": "no credits are offered", "business": "99.5%", "enterprise": "99.9%"},
        "Credits become payable when measured availability falls below {v}.",
        "service credit eligibility threshold",
        ("when do we get money back for downtime",
         "what outage level earns compensation"),
        ("credit", "payable"),
    ),
    # ---- auth ------------------------------------------------------------
    _f(
        "access_token_lifetime", "auth", "access tokens", "lifetime", "none",
        {"": "15 minutes"},
        "An issued access token is valid for {v} and cannot be extended.",
        "access token lifetime",
        ("how long before a bearer credential expires",
         "when does the short lived key stop working"),
        ("bearer",),
    ),
    _f(
        "refresh_token_lifetime", "auth", "refresh tokens", "lifetime", "plan",
        {"starter": "7 days", "business": "30 days", "enterprise": "90 days"},
        "A refresh token remains usable for {v} of inactivity before it is revoked.",
        "refresh token lifetime",
        ("how long can a signed in session sit idle",
         "when is a long lived credential invalidated"),
        ("refresh",),
    ),
    _f(
        "session_idle", "auth", "console sessions", "idle timeout", "plan",
        {"starter": "8 hours", "business": "8 hours", "enterprise": "30 minutes"},
        "An unattended console session is signed out after {v}.",
        "console session idle timeout",
        ("how long can i leave the dashboard open",
         "when am i logged out for inactivity"),
        ("console", "dashboard"),
    ),
    _f(
        "sso_protocol", "auth", "single sign-on", "supported protocol", "none",
        {"": "SAML 2.0 and OIDC"},
        "Federated login is available over {v}; no other protocol is accepted.",
        "single sign-on supported protocol",
        ("which federation standard can we plug into",
         "what identity protocols work here"),
        ("saml", "oidc", "federat"),
    ),
    _f(
        "mfa_requirement", "auth", "administrator accounts", "second factor requirement", "region",
        {"us": "strongly recommended but not enforced", "eu": "mandatory and enforced at login",
         "apac": "mandatory and enforced at login"},
        "A second authentication factor is {v} for accounts holding the owner role.",
        "administrator second factor requirement",
        ("do owners have to use two step verification",
         "is mfa forced for privileged users"),
        ("factor", "owner"),
    ),
    # ---- storage ---------------------------------------------------------
    _f(
        "encryption_at_rest", "storage", "stored data", "encryption algorithm", "none",
        {"": "AES-256-GCM"},
        "All data at rest is encrypted with {v}.",
        "encryption at rest algorithm",
        ("what cipher protects data on disk",
         "how is stored information scrambled"),
        ("aes", "cipher"),
    ),
    _f(
        "key_rotation", "storage", "encryption keys", "rotation interval", "region",
        {"us": "every 90 days", "eu": "every 30 days", "apac": "every 90 days"},
        "Data encryption keys are rotated {v} without customer action.",
        "encryption key rotation interval",
        ("how often are the keys changed",
         "key rollover frequency"),
        ("rotat",),
    ),
    _f(
        "residency", "storage", "customer data", "residency guarantee", "region",
        {"us": "us-east-2 and us-west-1", "eu": "eu-central-1 only",
         "apac": "ap-southeast-2 and ap-northeast-1"},
        "Primary copies never leave {v}.",
        "customer data residency guarantee",
        ("where physically does our information live",
         "which datacentres hold the primary copy"),
        ("residency", "primary"),
    ),
    # ---- deploy ----------------------------------------------------------
    _f(
        "maintenance_window", "deploy", "planned maintenance", "weekly window", "region",
        {"us": "Sunday 02:00-06:00 UTC", "eu": "Sunday 23:00-03:00 UTC",
         "apac": "Saturday 14:00-18:00 UTC"},
        "Disruptive work is confined to {v}.",
        "planned maintenance weekly window",
        ("when do upgrades happen",
         "what hours might things be interrupted"),
        ("maintenance", "disruptive"),
    ),
    _f(
        "deploy_notice", "deploy", "breaking API changes", "advance notice", "plan",
        {"starter": "30 days", "business": "90 days", "enterprise": "180 days"},
        "Incompatible changes are announced at least {v} before they take effect.",
        "breaking change advance notice",
        ("how much warning before an api breaks",
         "deprecation lead time"),
        ("deprecat", "incompatible"),
    ),
    _f(
        "rollback_window", "deploy", "a release", "rollback window", "none",
        {"": "45 minutes"},
        "A release may be reverted without a data migration for {v} after promotion.",
        "release rollback window",
        ("how long can we undo a deploy",
         "revert deadline after shipping"),
        ("revert", "promotion"),
    ),
    # ---- backup ----------------------------------------------------------
    _f(
        "rpo", "backup", "disaster recovery", "recovery point objective", "plan",
        {"starter": "24 hours", "business": "1 hour", "enterprise": "5 minutes"},
        "The recovery point objective is {v} of data loss in a regional failure.",
        "disaster recovery recovery point objective",
        ("how much work could we lose in a disaster",
         "acceptable data loss window"),
        ("rpo",),
    ),
    _f(
        "rto", "backup", "disaster recovery", "recovery time objective", "plan",
        {"starter": "24 hours", "business": "4 hours", "enterprise": "1 hour"},
        "The recovery time objective is {v} to restore service in another region.",
        "disaster recovery recovery time objective",
        ("how long until we are back up after a region dies",
         "target restoration duration"),
        ("rto",),
    ),
    _f(
        "backup_test", "backup", "restore drills", "frequency", "none",
        {"": "once every calendar quarter"},
        "A full restore is exercised {v} against a scratch environment.",
        "restore drill frequency",
        ("how often is recovery actually practised",
         "when do we rehearse bringing data back"),
        ("drill", "rehears"),
    ),
    # ---- billing ---------------------------------------------------------
    _f(
        "overage_rate", "billing", "ingest overage", "unit price", "plan",
        {"starter": "$0.40 per GB", "business": "$0.22 per GB", "enterprise": "$0.09 per GB"},
        "Volume beyond the included allowance is billed at {v}.",
        "ingest overage unit price",
        ("what do extra gigabytes cost",
         "price once we go past the quota"),
        ("overage", "allowance"),
    ),
    _f(
        "invoice_terms", "billing", "invoices", "payment terms", "plan",
        {"starter": "due on receipt", "business": "net 30", "enterprise": "net 60"},
        "Issued invoices are {v}.",
        "invoice payment terms",
        ("how long do we have to pay",
         "when is the bill due"),
        ("invoice", "net"),
    ),
    _f(
        "currency", "billing", "billing", "settlement currency", "region",
        {"us": "United States dollars", "eu": "euro", "apac": "Australian dollars"},
        "Charges are settled in {v} regardless of the currency shown in the console.",
        "settlement currency",
        ("which money are we actually charged in",
         "what currency clears on the card"),
        ("currency", "settle"),
    ),
)


def _distinctive(value: str) -> bool:
    """A value is distinctive if substring search for it is meaningful.

    Two ways to fail: too short to be unlikely, or composed only of digits and
    spaces. The second is the one that bit -- "99.0%" has no letter in it but
    is perfectly distinctive, so the rule cannot be "must contain a letter".
    """
    if len(value) < 4:
        return False
    return not all(c.isdigit() or c.isspace() for c in value)


def assert_values_are_distinctive() -> None:
    """Every governed value must be a distinctive string.

    This is not cosmetic. The corpus contains generated near-miss documents,
    and the guarantee that none of them accidentally states an answer is
    enforced by substring search. A value like "4" makes that search
    meaningless: any sentence containing the digit would trip it, and -- worse,
    because it is silent -- a near-miss sentence reading "the default fan-out
    is 4" really would be an unlabelled correct answer for a bag-of-words
    retriever. The original fact table contained exactly that. See
    docs/portfolio/failure-log.md.
    """
    for fam in FAMILIES:
        for qual, value in fam.values.items():
            if not _distinctive(value):
                raise AssertionError(
                    f"{fam.key}@{qual}: value {value!r} is not distinctive -- "
                    "it is either shorter than four characters or a bare "
                    "number, and would match generated prose by chance"
                )


def build_facts() -> tuple[Fact, ...]:
    out: list[Fact] = []
    for fam in FAMILIES:
        if fam.varies_by == "none":
            out.append(Fact(fid=f"{fam.key}", family=fam, qualifier=""))
        elif fam.varies_by == "plan":
            for plan in PLANS:
                out.append(Fact(fid=f"{fam.key}@{plan}", family=fam, qualifier=plan))
        elif fam.varies_by == "region":
            for region in REGIONS:
                out.append(Fact(fid=f"{fam.key}@{region}", family=fam, qualifier=region))
        else:  # pragma: no cover - guarded by a test
            raise ValueError(f"unknown varies_by {fam.varies_by!r}")
    return tuple(out)


FACTS: tuple[Fact, ...] = build_facts()
BY_FID: dict[str, Fact] = {f.fid: f for f in FACTS}
BY_FAMILY: dict[str, tuple[Fact, ...]] = {
    fam.key: tuple(f for f in FACTS if f.family.key == fam.key) for fam in FAMILIES
}
TOPICS: tuple[str, ...] = tuple(dict.fromkeys(fam.topic for fam in FAMILIES))
