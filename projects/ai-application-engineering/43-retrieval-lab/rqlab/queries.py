"""Queries, derived from the ground truth rather than written against it.

Five classes, chosen because they fail differently:

lexical      the wording a reader of the document would use
paraphrase   the wording of someone who has not read it
implicit     the qualifier is implied, never named ("for our largest accounts")
unqualified  no qualifier at all; every variant of the family is relevant
multi        two unrelated facts in one question

``implicit`` is the class that separates lexical matching from distributional
matching: no amount of term weighting maps "our largest accounts" to
"Enterprise", because the two strings share no token.

Relevance is binary
-------------------
A chunk is relevant when it fully covers a target fact: the value *and* the
context that says which plan or region the value applies to. Graded relevance
was considered and rejected. The natural intermediate case -- a chunk holding
the right number under no qualifier, or the wrong plan's number -- is not
partially useful. Handed to a generator it produces a confident, wrong,
well-sourced answer, which is worse than returning nothing. Awarding it a
fractional gain would score a harm as a small success.

Those cases are measured instead by ``misleading@k`` (see metrics.py), which
counts them as what they are.
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass

from .facts import BY_FAMILY, FACTS, FAMILIES, Fact

QUERY_CLASSES = ("lexical", "paraphrase", "implicit", "unqualified", "multi")

_PLAN_PHRASE = {
    "starter": "on the Starter plan",
    "business": "on the Business plan",
    "enterprise": "on the Enterprise plan",
}
_REGION_PHRASE = {
    "us": "in the US region",
    "eu": "in the EU region",
    "apac": "in the APAC region",
}

# Qualifiers named by implication only. None of these strings shares a token
# with the heading that carries the answer.
_PLAN_IMPLICIT = {
    "starter": ("for a customer on our cheapest tier",
                "for someone who just signed up self serve"),
    "business": ("for a mid sized paying customer",
                 "for an account on the middle tier"),
    "enterprise": ("for our largest accounts",
                   "for a customer with a negotiated contract"),
}
_REGION_IMPLICIT = {
    "us": ("for our North American customers", "for accounts served from Ohio"),
    "eu": ("for our German subsidiary", "for customers covered by GDPR"),
    "apac": ("for our Sydney office", "for customers in Australia"),
}


@dataclass(frozen=True)
class Query:
    qid: str
    text: str
    cls: str
    #: Facts that fully answer the question.
    targets: tuple[str, ...]
    #: Facts of the same family that do *not* answer it. Retrieving one of
    #: these looks like success to a topical judge and is a wrong answer.
    siblings: tuple[str, ...]

    def split(self) -> str:
        """Deterministic half, used to separate tuning from reporting.

        Hash-based rather than index-based so that adding a query class later
        does not reshuffle the existing assignment and silently invalidate a
        previously reported held-out number.
        """
        h = hashlib.sha256(self.qid.encode()).digest()[0]
        return "tune" if h % 2 == 0 else "report"


def _siblings_of(fact: Fact) -> tuple[str, ...]:
    return tuple(
        f.fid for f in BY_FAMILY[fact.family.key] if f.fid != fact.fid
    )


def _qualifier_phrase(fact: Fact) -> str:
    if fact.family.varies_by == "plan":
        return " " + _PLAN_PHRASE[fact.qualifier]
    if fact.family.varies_by == "region":
        return " " + _REGION_PHRASE[fact.qualifier]
    return ""


def _implicit_phrase(fact: Fact, pick: int) -> str | None:
    if fact.family.varies_by == "plan":
        return " " + _PLAN_IMPLICIT[fact.qualifier][pick % 2]
    if fact.family.varies_by == "region":
        return " " + _REGION_IMPLICIT[fact.qualifier][pick % 2]
    return None


def build_queries() -> tuple[Query, ...]:
    out: list[Query] = []

    for i, fact in enumerate(FACTS):
        sib = _siblings_of(fact)
        qual = _qualifier_phrase(fact)

        out.append(
            Query(
                f"lex-{fact.fid}",
                f"{fact.family.lexical}{qual}",
                "lexical",
                (fact.fid,),
                sib,
            )
        )
        out.append(
            Query(
                f"par-{fact.fid}",
                f"{fact.family.paraphrases[i % len(fact.family.paraphrases)]}{qual}",
                "paraphrase",
                (fact.fid,),
                sib,
            )
        )
        imp = _implicit_phrase(fact, i)
        if imp is not None:
            out.append(
                Query(
                    f"imp-{fact.fid}",
                    f"{fact.family.paraphrases[(i + 1) % len(fact.family.paraphrases)]}{imp}",
                    "implicit",
                    (fact.fid,),
                    sib,
                )
            )

    for fam in FAMILIES:
        members = BY_FAMILY[fam.key]
        out.append(
            Query(
                f"unq-{fam.key}",
                fam.lexical,
                "unqualified",
                tuple(f.fid for f in members),
                (),
            )
        )

    # Pair families from different topics so a single well-chosen chunk cannot
    # satisfy both; recall@k must genuinely reach two places in the index.
    fams = [f for f in FAMILIES]
    for i in range(0, len(fams) - 1, 2):
        a, b = fams[i], fams[i + 1]
        if a.topic == b.topic:
            b = fams[(i + 5) % len(fams)]
        if a.key == b.key:
            continue
        fa = BY_FAMILY[a.key][0]
        fb = BY_FAMILY[b.key][0]
        out.append(
            Query(
                f"mul-{a.key}+{b.key}",
                f"{a.lexical} and also {b.lexical}",
                "multi",
                (fa.fid, fb.fid),
                (),
            )
        )

    seen: set[str] = set()
    for q in out:
        if q.qid in seen:
            raise AssertionError(f"duplicate query id {q.qid}")
        seen.add(q.qid)
    return tuple(out)


QUERIES: tuple[Query, ...] = build_queries()


def assert_queries_do_not_quote_answers() -> None:
    """No query may contain the value it is asking for.

    A query that quotes its own answer is retrievable by exact match and
    measures nothing. This is the most common way a generated evaluation set
    becomes trivially easy, and it is silent: every configuration scores well
    and the comparison between them collapses.
    """
    from .facts import BY_FID

    for q in QUERIES:
        for fid in q.targets:
            value = BY_FID[fid].value
            if value.lower() in q.text.lower():
                raise AssertionError(
                    f"query {q.qid} quotes its own answer {value!r}: {q.text!r}"
                )


def by_class() -> dict[str, tuple[Query, ...]]:
    out: dict[str, list[Query]] = {c: [] for c in QUERY_CLASSES}
    for q in QUERIES:
        out[q.cls].append(q)
    return {k: tuple(v) for k, v in out.items()}
