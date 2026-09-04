"""Rendering documents from the ground truth, with exact provenance.

Each fact is written into one or more documents. Every write records the
character span of the sentence that states the value, plus the spans of any
text the sentence *depends on* -- almost always a heading that carries the plan
or region the value applies to.

That dependency is the whole point. Given a chunk and its provenance, whether
the chunk can answer a query is a set-containment question with an exact
answer, decided before any retrieval runs. It yields a ceiling on quality that
belongs to the chunker alone and that no downstream stage can raise.

Six layouts, chosen because they disagree
-----------------------------------------
plan_matrix   heading carries the plan, dozens of lines below it
topic_guide   heading carries the qualifier, few lines below it
faq           self-contained; question restates the qualifier; no dependency
dense_prose   several facts run together in one paragraph, no internal headings
table         answer is a cell; depends on both its row label and the header row
runbook       fact buried mid-paragraph among near-miss sentences on other topics

A chunker that wins on one of these loses on another, which is why a single
aggregate number across a real corpus tells you almost nothing.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from .facts import (
    BY_FAMILY,
    FACTS,
    FAMILIES,
    PLAN_LABEL,
    PLANS,
    REGION_LABEL,
    REGIONS,
    TOPICS,
)


@dataclass(frozen=True, order=True)
class Span:
    start: int
    end: int

    def __post_init__(self) -> None:
        if self.end < self.start:
            raise ValueError(f"inverted span {self.start}..{self.end}")

    def covered_by(self, others: tuple["Span", ...]) -> bool:
        """True when this span lies inside the union of ``others``.

        Implemented as a sweep rather than a per-interval test because a
        prefixed chunk carries two disjoint intervals and a span may in
        principle straddle them.
        """
        if self.start == self.end:
            return any(o.start <= self.start <= o.end for o in others)
        cursor = self.start
        for o in sorted(others):
            if o.start > cursor:
                return False
            cursor = max(cursor, o.end)
            if cursor >= self.end:
                return True
        return cursor >= self.end


@dataclass(frozen=True)
class Placement:
    """One rendering of one fact, in one document."""

    fid: str
    doc_id: str
    layout: str
    answer: Span
    context: tuple[Span, ...]

    @property
    def required(self) -> tuple[Span, ...]:
        return (self.answer,) + self.context

    def covered_by(self, spans: tuple[Span, ...]) -> bool:
        return all(s.covered_by(spans) for s in self.required)


@dataclass
class Document:
    doc_id: str
    title: str
    layout: str
    text: str


class _Builder:
    """Appends text to a document while recording the span of each write."""

    def __init__(self, doc_id: str, title: str, layout: str) -> None:
        self.doc_id = doc_id
        self.title = title
        self.layout = layout
        self._parts: list[str] = []
        self._len = 0

    def emit(self, text: str, *, gap: str = "\n") -> Span:
        start = self._len
        self._parts.append(text)
        self._len += len(text)
        end = self._len
        if gap:
            self._parts.append(gap)
            self._len += len(gap)
        return Span(start, end)

    def blank(self) -> None:
        self._parts.append("\n")
        self._len += 1

    def build(self) -> Document:
        return Document(self.doc_id, self.title, self.layout, "".join(self._parts))


# Families written into exactly one document. Their retrievability is decided
# entirely by how that one document is chunked, which is where every chunker
# difference in this lab actually comes from.
SINGLETON_FAMILIES = frozenset(
    {
        "trace_retention",
        "concurrent_exports",
        "credit_threshold",
        "session_idle",
        "key_rotation",
        "rollback_window",
        "backup_test",
        "currency",
        "deleted_account_purge",
        "query_timeout",
    }
)

TABLE_FAMILIES = ("audit_log_retention", "metric_retention", "backup_retention", "rpo", "rto")
FAQ_FAMILIES = ("api_rate_limit", "uptime_sla", "access_token_lifetime", "encryption_at_rest",
                "max_payload", "sso_protocol")
DENSE_FAMILIES = ("ingest_limit", "overage_rate", "invoice_terms", "deploy_notice",
                  "support_response", "refresh_token_lifetime")
RUNBOOK_FAMILIES = ("maintenance_window", "residency", "mfa_requirement")

_TOPIC_TITLE = {
    "retention": "Data Retention",
    "limits": "Service Limits",
    "sla": "Service Level Commitments",
    "auth": "Authentication and Access",
    "storage": "Storage and Encryption",
    "deploy": "Release and Maintenance",
    "backup": "Backup and Disaster Recovery",
    "billing": "Billing and Metering",
}

_TOPIC_PREAMBLE = {
    "retention": (
        "Retention is enforced by a nightly reaper that walks each tenant's storage "
        "namespace in lexicographic order. The reaper is idempotent and safe to "
        "re-run; operators occasionally do so after a partial region outage."
    ),
    "limits": (
        "Limits are applied at the edge before a request reaches any regional "
        "service. A rejected request is not metered and does not count towards "
        "the ingest allowance."
    ),
    "sla": (
        "Availability is computed from synthetic probes issued once per minute "
        "from three independent networks. A minute counts as unavailable only "
        "when a majority of probes fail."
    ),
    "auth": (
        "Every credential type is issued by the same authority and carries the "
        "same claim set. What differs between them is lifetime and the "
        "conditions under which they are revoked."
    ),
    "storage": (
        "Encryption is applied per object at write time. Keys are held in a "
        "regional key service that never exports private material."
    ),
    "deploy": (
        "Changes reach production through a fixed promotion ladder: canary, then "
        "one region, then the rest. Each rung requires a clean error budget."
    ),
    "backup": (
        "Backups are continuous rather than scheduled: the write-ahead log is "
        "shipped to a second region and replayed there within seconds."
    ),
    "billing": (
        "Usage is aggregated hourly and closed monthly. A correction issued after "
        "a month closes appears as a line item on the following invoice."
    ),
}

# Sentences that sit next to answers and share their vocabulary without stating
# any fact. They exist so that a retriever cannot succeed by matching topic
# words alone.
_DISTRACTORS = (
    "The value below is enforced by the control plane and cannot be overridden "
    "per project.",
    "Changing this requires a written amendment; support cannot adjust it.",
    "Historical values are visible in the change log linked from the console "
    "footer.",
    "The figure applies to the organisation as a whole, not to individual "
    "members.",
    "Exceeding this produces a structured error rather than silent truncation.",
)


def _plan_matrix() -> tuple[list[Document], list[Placement]]:
    docs: list[Document] = []
    places: list[Placement] = []
    for plan in PLANS:
        doc_id = f"plan-{plan}"
        b = _Builder(doc_id, f"{PLAN_LABEL[plan]} Plan Reference", "plan_matrix")
        head = b.emit(f"# {PLAN_LABEL[plan]} Plan Reference")
        b.blank()
        b.emit(
            "This page states every value that differs between plans. Values not "
            "listed here are identical across plans and are documented in the "
            "topic guides."
        )
        b.blank()
        for topic in TOPICS:
            fams = [
                f
                for f in FAMILIES
                if f.topic == topic
                and f.varies_by == "plan"
                and f.key not in SINGLETON_FAMILIES
            ]
            if not fams:
                continue
            b.emit(f"## {_TOPIC_TITLE[topic]}")
            b.blank()
            for i, fam in enumerate(fams):
                fact = next(f for f in BY_FAMILY[fam.key] if f.qualifier == plan)
                b.emit(f"### {fam.subject.title()} -- {fam.attribute}")
                b.blank()
                b.emit(_DISTRACTORS[(i + len(topic)) % len(_DISTRACTORS)])
                ans = b.emit(fact.sentence)
                b.blank()
                places.append(
                    Placement(fact.fid, doc_id, "plan_matrix", ans, (head,))
                )
        docs.append(b.build())
    return docs, places


def _topic_guides() -> tuple[list[Document], list[Placement]]:
    docs: list[Document] = []
    places: list[Placement] = []
    for topic in TOPICS:
        doc_id = f"guide-{topic}"
        b = _Builder(doc_id, _TOPIC_TITLE[topic], "topic_guide")
        b.emit(f"# {_TOPIC_TITLE[topic]}")
        b.blank()
        b.emit(_TOPIC_PREAMBLE[topic])
        b.blank()
        for fam in [f for f in FAMILIES if f.topic == topic]:
            b.emit(f"## {fam.subject.title()}: {fam.attribute}")
            b.blank()
            b.emit(
                f"The {fam.attribute} for {fam.subject} is set centrally. "
                + _DISTRACTORS[len(fam.key) % len(_DISTRACTORS)]
            )
            b.blank()
            if fam.varies_by == "none":
                fact = BY_FAMILY[fam.key][0]
                ans = b.emit(fact.sentence)
                b.blank()
                places.append(Placement(fact.fid, doc_id, "topic_guide", ans, ()))
            else:
                keys = PLANS if fam.varies_by == "plan" else REGIONS
                label = PLAN_LABEL if fam.varies_by == "plan" else REGION_LABEL
                for k in keys:
                    fact = next(f for f in BY_FAMILY[fam.key] if f.qualifier == k)
                    sub = b.emit(f"### {label[k]}")
                    b.blank()
                    ans = b.emit(fact.sentence)
                    b.blank()
                    places.append(
                        Placement(fact.fid, doc_id, "topic_guide", ans, (sub,))
                    )
        docs.append(b.build())
    return docs, places


def _faq() -> tuple[list[Document], list[Placement]]:
    doc_id = "faq-common"
    b = _Builder(doc_id, "Frequently Asked Questions", "faq")
    b.emit("# Frequently Asked Questions")
    b.blank()
    places: list[Placement] = []
    for key in FAQ_FAMILIES:
        fam = next(f for f in FAMILIES if f.key == key)
        if fam.varies_by == "none":
            fact = BY_FAMILY[key][0]
            q = f"**What is the {fam.attribute} for {fam.subject}?**"
            span = b.emit(f"{q} {fact.sentence}")
            b.blank()
            places.append(Placement(fact.fid, doc_id, "faq", span, ()))
        else:
            keys = PLANS if fam.varies_by == "plan" else REGIONS
            label = PLAN_LABEL if fam.varies_by == "plan" else REGION_LABEL
            for k in keys:
                fact = next(f for f in BY_FAMILY[key] if f.qualifier == k)
                q = (
                    f"**On the {label[k]} plan, what is the {fam.attribute} "
                    f"for {fam.subject}?**"
                    if fam.varies_by == "plan"
                    else f"**In {label[k]}, what is the {fam.attribute} "
                    f"for {fam.subject}?**"
                )
                span = b.emit(f"{q} {fact.sentence}")
                b.blank()
                places.append(Placement(fact.fid, doc_id, "faq", span, ()))
    return [b.build()], places


def _dense_prose() -> tuple[list[Document], list[Placement]]:
    """Facts run together inside paragraphs, with the qualifier in a heading."""
    docs: list[Document] = []
    places: list[Placement] = []
    fams = [next(f for f in FAMILIES if f.key == k) for k in DENSE_FAMILIES]
    for plan in ("business", "enterprise"):
        doc_id = f"brief-{plan}"
        b = _Builder(doc_id, f"{PLAN_LABEL[plan]} Commercial Brief", "dense_prose")
        head = b.emit(f"# {PLAN_LABEL[plan]} Commercial Brief")
        b.blank()
        b.emit(
            "The following narrative is written for account teams. It is "
            "descriptive rather than contractual; the plan reference page is "
            "authoritative where the two disagree."
        )
        b.blank()
        for start in (0, 3):
            group = fams[start : start + 3]
            b.emit(
                "Operationally the account behaves as follows.", gap=" "
            )
            for fam in group:
                if fam.varies_by != "plan":
                    continue
                fact = next(f for f in BY_FAMILY[fam.key] if f.qualifier == plan)
                b.emit(
                    f"Regarding {fam.subject}, the {fam.attribute} is the "
                    f"governing constraint.",
                    gap=" ",
                )
                ans = b.emit(fact.sentence, gap=" ")
                places.append(
                    Placement(fact.fid, doc_id, "dense_prose", ans, (head,))
                )
            b.emit(
                "Account teams should confirm these before committing to a "
                "customer in writing."
            )
            b.blank()
        docs.append(b.build())
    return docs, places


def _table() -> tuple[list[Document], list[Placement]]:
    """A matrix. Every cell is meaningless without its row label and header."""
    doc_id = "matrix-plans"
    b = _Builder(doc_id, "Plan Comparison Matrix", "table")
    b.emit("# Plan Comparison Matrix")
    b.blank()
    b.emit(
        "One row per governed value. Cells state the value only; the row label "
        "names what is being measured and the header row names the plan."
    )
    b.blank()
    header = b.emit("| Value | Starter | Business | Enterprise |")
    b.emit("| --- | --- | --- | --- |")
    places: list[Placement] = []
    for key in TABLE_FAMILIES:
        fam = next(f for f in FAMILIES if f.key == key)
        cells = [
            next(f for f in BY_FAMILY[key] if f.qualifier == p).value for p in PLANS
        ]
        label = f"| {fam.subject.title()} {fam.attribute} "
        row_start = b._len
        b.emit(label + "| " + " | ".join(cells) + " |")
        row_label = Span(row_start, row_start + len(label))
        # Each cell is its own answer span, and depends on its row label and
        # on the header row that names the column.
        cursor = row_start + len(label)
        for p, cell in zip(PLANS, cells):
            cell_start = cursor + 2  # skip "| "
            cell_end = cell_start + len(cell)
            fact = next(f for f in BY_FAMILY[key] if f.qualifier == p)
            places.append(
                Placement(
                    fact.fid,
                    doc_id,
                    "table",
                    Span(cell_start, cell_end),
                    (row_label, header),
                )
            )
            cursor = cell_end + 1  # the following " "
    return [b.build()], places


def _runbooks() -> tuple[list[Document], list[Placement]]:
    docs: list[Document] = []
    places: list[Placement] = []
    fams = [next(f for f in FAMILIES if f.key == k) for k in RUNBOOK_FAMILIES]
    for region in ("eu", "apac"):
        doc_id = f"runbook-{region}"
        b = _Builder(doc_id, f"{REGION_LABEL[region]} Operations Runbook", "runbook")
        head = b.emit(f"# {REGION_LABEL[region]} Operations Runbook")
        b.blank()
        b.emit(
            "Follow the steps in order. Do not skip a verification step because "
            "the previous one looked healthy; the failure mode this runbook "
            "exists for is precisely a healthy-looking partial failure."
        )
        b.blank()
        for i, fam in enumerate(fams, start=1):
            b.emit(f"## Step {i}: verify {fam.subject}")
            b.blank()
            b.emit(
                "Confirm the current configuration against the value below "
                "before proceeding. A mismatch here has caused two incidents "
                "and is worth thirty seconds.",
                gap=" ",
            )
            if fam.varies_by == "region":
                fact = next(f for f in BY_FAMILY[fam.key] if f.qualifier == region)
                ans = b.emit(fact.sentence, gap=" ")
                places.append(Placement(fact.fid, doc_id, "runbook", ans, (head,)))
            b.emit(
                "If the observed configuration differs, stop and page the "
                "on-call platform engineer rather than correcting it yourself."
            )
            b.blank()
        docs.append(b.build())
    return docs, places


@dataclass
class Corpus:
    documents: tuple[Document, ...]
    placements: tuple[Placement, ...]

    _by_doc: dict[str, Document] = field(default_factory=dict, repr=False)
    _by_fid: dict[str, tuple[Placement, ...]] = field(default_factory=dict, repr=False)

    def __post_init__(self) -> None:
        self._by_doc = {d.doc_id: d for d in self.documents}
        grouped: dict[str, list[Placement]] = {}
        for p in self.placements:
            grouped.setdefault(p.fid, []).append(p)
        self._by_fid = {k: tuple(v) for k, v in grouped.items()}

    def document(self, doc_id: str) -> Document:
        return self._by_doc[doc_id]

    def placements_for(self, fid: str) -> tuple[Placement, ...]:
        return self._by_fid.get(fid, ())

    def redundancy(self, fid: str) -> int:
        return len(self.placements_for(fid))


def build_corpus() -> Corpus:
    docs: list[Document] = []
    places: list[Placement] = []
    for part in (_topic_guides(), _plan_matrix(), _faq(), _dense_prose(), _table(), _runbooks()):
        d, p = part
        docs.extend(d)
        places.extend(p)

    # Enforce the singleton contract declared above: those families exist in the
    # topic guide only. Anything else is a bug in a layout, and this is where it
    # is caught rather than in a confusing metric three stages downstream.
    keep: list[Placement] = []
    for p in places:
        fam_key = p.fid.split("@")[0]
        if fam_key in SINGLETON_FAMILIES and p.layout != "topic_guide":
            raise AssertionError(
                f"{p.fid} is declared a singleton but was placed in {p.layout}"
            )
        keep.append(p)

    covered = {p.fid for p in keep}
    missing = [f.fid for f in FACTS if f.fid not in covered]
    if missing:
        raise AssertionError(f"facts with no placement: {missing}")

    return Corpus(tuple(docs), tuple(keep))
