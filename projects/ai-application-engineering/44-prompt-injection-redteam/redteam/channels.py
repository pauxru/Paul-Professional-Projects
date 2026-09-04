"""Provenance tracking: where a span of text came from, and what it may authorise.

The central claim of this project is that prompt injection is an
*information-flow* problem wearing a natural-language costume. An injection
succeeds when text that arrived through a low-trust channel causes an action
that only a high-trust channel was supposed to be able to request. Phrased that
way it stops being a linguistics problem and becomes a problem with sixty years
of prior art.

This module implements the flow half: a trust lattice, tainted strings that
carry their provenance through concatenation and slicing, and the join
operation that says what happens when text from two channels is mixed.

Nothing here reasons about the *content* of text. That is deliberate. Every
defence in this repository that reasons about content is defeated somewhere in
the corpus; the defences that reason about provenance are not, and the
difference is the point of the report.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import IntEnum
from typing import Iterable, Iterator


class Trust(IntEnum):
    """A total order on channels, from most to least authoritative.

    A total order rather than a general lattice is a real simplification and
    it is the subject of ADR-0002. Briefly: real systems have incomparable
    channels (content from finance and content from HR are both "internal" but
    neither dominates the other), and modelling that needs a partial order.
    A total order is sound for the question this project asks -- can untrusted
    text reach a privileged action -- because any partial order collapses to a
    total one once you only care about "at least as trusted as the sink".

    The numeric values matter: ``join`` takes the minimum, so lower is less
    trusted, and ``UNTRUSTED`` must be zero so that it absorbs.
    """

    SYSTEM = 40
    """The developer's own prompt. Fixed at build time, never derived from input."""

    OPERATOR = 30
    """A human operator's runtime configuration. Trusted, but not compiled in."""

    USER = 20
    """The person the agent is acting for. Trusted to direct the agent, not to
    escalate it -- a user may ask for their own data and not someone else's."""

    TOOL = 10
    """Output of a tool the agent called. Trusted to be *well-formed*, never
    trusted to be *instructions*. This is the channel most systems get wrong,
    because tool output looks like it came from the system."""

    UNTRUSTED = 0
    """Third-party content: retrieved documents, inbound email, web pages,
    file contents, anything a stranger can write. Never authorises anything."""

    @property
    def label(self) -> str:
        return self.name.lower()


def join(*levels: Trust) -> Trust:
    """The trust of a value derived from several inputs: the least of them.

    This is the whole security argument in one line. If you concatenate a
    SYSTEM prompt with an UNTRUSTED email, the result is UNTRUSTED, because an
    attacker who controls any part of a string controls what the string can be
    made to look like. The alternative -- taking the maximum, or taking the
    trust of the "main" input -- is how injection works.
    """
    if not levels:
        return Trust.SYSTEM
    return Trust(min(int(level) for level in levels))


@dataclass(frozen=True)
class Span:
    """A contiguous run of characters with a single provenance."""

    text: str
    trust: Trust
    origin: str
    """A human-readable source id, e.g. ``"email:msg-0041"``. Carried for
    forensics: when the broker denies an action we want to name the document
    that caused it, not merely its trust level."""

    def __len__(self) -> int:
        return len(self.text)


class Tainted:
    """A string that remembers, per character, which channel it came from.

    Character granularity rather than whole-string granularity is what lets the
    broker answer the question that actually matters: not "did any untrusted
    text enter this prompt" -- of course it did, that is the agent's job -- but
    "is the specific substring that became this tool argument untrusted".

    The implementation is a list of spans, not a per-character array, because
    the spans are few and long. Slicing is O(spans), not O(1), which is fine
    at the sizes involved and is measured in the report rather than assumed.
    """

    __slots__ = ("_spans",)

    def __init__(self, spans: Iterable[Span] = ()) -> None:
        merged: list[Span] = []
        for span in spans:
            if not span.text:
                continue
            if (merged and merged[-1].trust == span.trust
                    and merged[-1].origin == span.origin):
                last = merged.pop()
                merged.append(Span(last.text + span.text, last.trust, last.origin))
            else:
                merged.append(span)
        self._spans: tuple[Span, ...] = tuple(merged)

    # -- construction ------------------------------------------------------

    @classmethod
    def of(cls, text: str, trust: Trust, origin: str = "?") -> "Tainted":
        return cls([Span(text, trust, origin)])

    @classmethod
    def system(cls, text: str, origin: str = "system") -> "Tainted":
        return cls.of(text, Trust.SYSTEM, origin)

    @classmethod
    def user(cls, text: str, origin: str = "user") -> "Tainted":
        return cls.of(text, Trust.USER, origin)

    @classmethod
    def untrusted(cls, text: str, origin: str = "untrusted") -> "Tainted":
        return cls.of(text, Trust.UNTRUSTED, origin)

    # -- string-like behaviour --------------------------------------------

    @property
    def text(self) -> str:
        return "".join(span.text for span in self._spans)

    @property
    def spans(self) -> tuple[Span, ...]:
        return self._spans

    def __len__(self) -> int:
        return sum(len(span) for span in self._spans)

    def __str__(self) -> str:
        return self.text

    def __repr__(self) -> str:
        return f"Tainted({self.text!r}, min_trust={self.min_trust.label})"

    def __eq__(self, other: object) -> bool:
        if not isinstance(other, Tainted):
            return NotImplemented
        return self._spans == other._spans

    def __hash__(self) -> int:
        return hash(self._spans)

    def __add__(self, other: "Tainted | str") -> "Tainted":
        if isinstance(other, str):
            # A bare str has no provenance. Refusing to guess is deliberate:
            # silently defaulting to SYSTEM is exactly the bug this class
            # exists to prevent, and defaulting to UNTRUSTED would make the
            # class annoying enough that people would stop using it.
            raise TypeError(
                "cannot concatenate Tainted with a bare str: wrap it with "
                "Tainted.of(text, trust) so its provenance is explicit")
        return Tainted(self._spans + other._spans)

    # -- the queries the defences actually ask -----------------------------

    @property
    def min_trust(self) -> Trust:
        """The trust of the whole value: the least trusted part of it."""
        if not self._spans:
            return Trust.SYSTEM
        return join(*(span.trust for span in self._spans))

    def origins_at_or_below(self, level: Trust) -> tuple[str, ...]:
        """Which sources contributed text at or below ``level``.

        Used for the denial message. "Blocked: untrusted" is not actionable;
        "blocked: the argument contains text from email:msg-0041" is.
        """
        seen: list[str] = []
        for span in self._spans:
            if span.trust <= level and span.origin not in seen:
                seen.append(span.origin)
        return tuple(seen)

    def slice_trust(self, start: int, end: int) -> Trust:
        """Trust of the substring ``[start:end)`` -- the join over its spans.

        An empty or inverted range is SYSTEM, because a value derived from no
        input is derived from nothing an attacker controls. This is the
        identity of ``join`` and it is asserted in the tests; getting it
        backwards would make every empty tool argument look untrusted and the
        broker would deny everything, which is a failure mode that looks like
        security.
        """
        if end <= start:
            return Trust.SYSTEM
        levels: list[Trust] = []
        cursor = 0
        for span in self._spans:
            span_end = cursor + len(span)
            if cursor < end and span_end > start:
                levels.append(span.trust)
            cursor = span_end
            if cursor >= end:
                break
        return join(*levels) if levels else Trust.SYSTEM

    def trust_of_substring(self, needle: str) -> Trust | None:
        """Trust of the first occurrence of ``needle``, or None if absent.

        This is the query the broker makes: a model emitted a tool call with
        argument ``X``; where in the prompt did ``X`` come from? Substring
        search is a genuine approximation -- the model may paraphrase, and then
        the argument appears nowhere and this returns None.         ADR-0003 covers what that costs and why the broker treats None as
        untrusted.

        An empty needle returns None rather than a trust level. ``"".find``
        succeeds at index 0 and the resulting zero-width slice joins to
        ``SYSTEM`` by the vacuous-truth rule in ``join()``, so the natural
        implementation reports that an empty argument originated in the
        system prompt -- the highest trust in the lattice, awarded to the
        one string that contains no evidence at all. Two individually
        correct decisions composing into the maximally unsafe answer is the
        recurring shape of the bugs in this repository.
        """
        if not needle:
            return None
        index = self.text.find(needle)
        if index < 0:
            return None
        return self.slice_trust(index, index + len(needle))

    def __iter__(self) -> Iterator[Span]:
        return iter(self._spans)


@dataclass
class Capability:
    """A permission to invoke one tool, and the trust required to ask for it.

    ``min_trust`` is the level an *instruction* must have to cause this call.
    ``taint_sensitive_args`` names the arguments whose *content* must also
    clear the bar -- a ``send_email`` call may legitimately quote untrusted
    text in its body, but its ``to`` address must not be attacker-chosen.

    That distinction is the reason this is a capability object and not a
    boolean. A defence that blocks every tool call touched by untrusted data
    blocks the product; the useful question is always which *argument*.
    """

    tool: str
    min_trust: Trust = Trust.USER
    taint_sensitive_args: frozenset[str] = field(default_factory=frozenset)
    description: str = ""

    def requires_clean(self, arg: str) -> bool:
        return arg in self.taint_sensitive_args
