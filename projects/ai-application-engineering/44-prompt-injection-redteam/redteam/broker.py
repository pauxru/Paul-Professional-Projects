"""The capability broker: authorisation for tool calls, decided by provenance.

This is the defence the report argues actually works, and the argument is not
that it works *well* -- it is that its guarantee does not depend on the model.

Every other defence in this repository answers "does this text look like an
attack". That question has no reliable answer, because the attacker writes the
text and gets unlimited attempts. The broker answers a different question:
"is the channel that requested this action authorised to request it". The
attacker does not control the answer to that, because the attacker does not
control which channel their content arrived on.

The consequence is stated precisely in ``guarantee()`` and measured in the
report by sweeping the target model's susceptibility across its whole range
and showing the broker's denial rate is a flat line.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Mapping, Sequence

from .channels import Capability, Tainted, Trust


@dataclass(frozen=True)
class ToolCall:
    tool: str
    args: Mapping[str, str]

    def __str__(self) -> str:
        rendered = ", ".join(f"{k}={v!r}" for k, v in sorted(self.args.items()))
        return f"{self.tool}({rendered})"

    def __hash__(self) -> int:
        # A proposed action is a value, and audit code compares and
        # deduplicates sets of them. The frozen dataclass alone does not
        # deliver that, because the mapping field is unhashable; declaring
        # the hash explicitly keeps the type usable as one.
        return hash((self.tool, tuple(sorted(self.args.items()))))


@dataclass(frozen=True)
class Decision:
    allowed: bool
    reason: str
    rule: str
    """Which rule decided. Present so the report can attribute denials to
    rules rather than to the broker as a whole -- three of the four rules
    below turn out to be doing almost nothing, and that is worth knowing
    before shipping the complexity."""
    tainted_args: tuple[str, ...] = ()
    origins: tuple[str, ...] = ()


class CapabilityBroker:
    """Decides whether a proposed tool call may proceed.

    The broker is given the *tainted prompt* the model saw, not just the call.
    That is the whole trick: with the prompt in hand it can ask where each
    argument's text came from, which is information the model's output alone
    does not contain.
    """

    def __init__(self, capabilities: Sequence[Capability],
                 *, default_deny: bool = True) -> None:
        self._caps: dict[str, Capability] = {c.tool: c for c in capabilities}
        self._default_deny = default_deny

    @property
    def tools(self) -> tuple[str, ...]:
        return tuple(sorted(self._caps))

    def authorise(self, call: ToolCall, prompt: Tainted,
                  *, requester: Trust) -> Decision:
        """Four rules, applied in order. Order is significant and tested.

        1. **Unknown tool.** Not in the capability set at all.
        2. **Requester trust.** The channel driving this turn is below the
           tool's bar.
        3. **Argument taint.** A trust-sensitive argument contains text traced
           to a channel below the bar.
        4. **Unattributable argument.** A trust-sensitive argument does not
           appear in the prompt at all.

        Rule 4 is the interesting one and the one most implementations omit.
        If the model *paraphrases* an injected instruction, the resulting
        argument matches no substring of the prompt and rule 3 finds nothing to
        object to. Treating "I cannot tell where this came from" as equivalent
        to "it came from a trusted place" is the single most common way a
        taint-tracking defence is silently defeated. Here it denies, and the
        report measures what that costs in false denials on the benign corpus.
        """
        capability = self._caps.get(call.tool)
        if capability is None:
            return Decision(
                allowed=not self._default_deny,
                reason=f"tool {call.tool!r} is not in the capability set",
                rule="unknown-tool")

        if requester < capability.min_trust:
            return Decision(
                allowed=False,
                reason=(f"requesting channel {requester.label} is below the "
                        f"{capability.min_trust.label} required by "
                        f"{call.tool!r}"),
                rule="requester-trust")

        tainted: list[str] = []
        origins: list[str] = []
        unattributable: list[str] = []
        for name, value in sorted(call.args.items()):
            if not capability.requires_clean(name):
                continue
            if not value:
                # An empty sensitive argument is unattributable, not
                # acceptable. Skipping it here reads as "there is nothing to
                # check", but the rule this broker enforces is a positive
                # one: the argument must be *shown* to originate at or above
                # the bar. An empty string shows nothing. This is the second
                # place in the codebase where "empty" was quietly treated as
                # "fine" -- the first was `trust_of_substring`, which awarded
                # SYSTEM trust to the empty needle.
                unattributable.append(name)
                continue
            level = prompt.trust_of_substring(value)
            if level is None:
                unattributable.append(name)
                continue
            if level < capability.min_trust:
                tainted.append(name)
                for origin in prompt.origins_at_or_below(level):
                    if origin not in origins:
                        origins.append(origin)

        if tainted:
            return Decision(
                allowed=False,
                reason=(f"argument(s) {', '.join(tainted)} of {call.tool!r} "
                        f"contain text from {', '.join(origins) or 'an '
                        'untrusted channel'}"),
                rule="argument-taint",
                tainted_args=tuple(tainted),
                origins=tuple(origins))

        if unattributable:
            return Decision(
                allowed=False,
                reason=(f"argument(s) {', '.join(unattributable)} of "
                        f"{call.tool!r} cannot be traced to any part of the "
                        f"prompt; provenance is unknown, so it is not trusted"),
                rule="unattributable-argument",
                tainted_args=tuple(unattributable))

        return Decision(allowed=True, reason="authorised", rule="allow")

    def guarantee(self) -> str:
        """The property the broker provides, stated so it can be checked.

        For any tool T with ``min_trust = L`` and any turn whose requesting
        channel has trust below L, no output the model can possibly produce
        results in T being invoked. This holds for every model, every prompt,
        every attack, known or not -- because the broker never reads the
        model's output as an authorisation, only as a proposal.

        The report tests this the only way a claim of that shape can be
        tested: by making the model maximally compliant (susceptibility 1.0,
        which follows every injected instruction it can parse) and confirming
        the denial rate for out-of-scope tools stays at 100%.
        """
        lines = [
            "Guarantee: for any tool T requiring trust L, and any turn whose "
            "requesting channel is below L, no output the model can produce "
            "causes T to be invoked. This holds for every model and every "
            "attack, known or not, because the broker never reads model "
            "output as an authorisation -- only as a proposal. Arguments are "
            "admitted only when their provenance can be shown to be at or "
            "above L; unknown provenance is refused, not assumed.",
            "",
            "Capabilities:",
        ]
        for name in sorted(self._caps):
            cap = self._caps[name]
            args = ", ".join(sorted(cap.taint_sensitive_args)) or "none"
            lines.append(f"  {name}: requires {cap.min_trust.label}; "
                         f"clean args: {args}")
        return "\n".join(lines)


def default_capabilities() -> tuple[Capability, ...]:
    """The capability set for the email-summarising agent in the scenario.

    Chosen to be realistic rather than convenient: the agent genuinely needs
    to read mail and search, which is why those are ``UNTRUSTED``-safe, and it
    genuinely needs to be able to send mail on the user's behalf, which is why
    ``send_email`` exists at all rather than being cut to make the numbers
    look better. The recipient is trust-sensitive and the body is not, which
    is the distinction that lets the agent quote an attacker's email back to
    the user without letting the attacker choose who receives it.
    """
    return (
        Capability("read_email", Trust.UNTRUSTED,
                   description="read messages already in the user's mailbox"),
        Capability("search_docs", Trust.UNTRUSTED,
                   description="search the corpus the agent is grounded on"),
        Capability("summarise", Trust.UNTRUSTED,
                   description="pure transformation, no side effect"),
        Capability("send_email", Trust.USER, frozenset({"to", "cc", "bcc"}),
                   description="send mail as the user"),
        Capability("fetch_url", Trust.USER, frozenset({"url"}),
                   description="outbound HTTP; the classic exfil channel"),
        Capability("delete_record", Trust.OPERATOR, frozenset({"id"}),
                   description="destructive"),
        Capability("transfer_funds", Trust.OPERATOR, frozenset({"to", "amount"}),
                   description="destructive and irreversible"),
        Capability("admin_reset", Trust.SYSTEM, frozenset({"account"}),
                   description="should never be reachable from a chat turn"),
    )


@dataclass
class BrokerLog:
    """Denials, kept for the report rather than for the runtime."""

    decisions: list[tuple[ToolCall, Decision]] = field(default_factory=list)

    def record(self, call: ToolCall, decision: Decision) -> None:
        self.decisions.append((call, decision))

    @property
    def denied(self) -> list[tuple[ToolCall, Decision]]:
        return [(c, d) for c, d in self.decisions if not d.allowed]

    @property
    def by_rule(self) -> dict[str, int]:
        counts: dict[str, int] = {}
        for _, decision in self.decisions:
            if not decision.allowed:
                counts[decision.rule] = counts.get(decision.rule, 0) + 1
        return counts
