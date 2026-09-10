import assert from "node:assert/strict";
import { test } from "node:test";
import { renderReadme, resolveHref } from "../../scripts/lib/markdown.mjs";

const BASE = {
  projectFolder: "projects/foundation/01-x",
  repoBaseUrl: "https://github.com/pauxru/Paul-Professional-Projects",
  ref: "main",
};

function alwaysMissing() {
  return { exists: false, isDirectory: false };
}

test("same-document anchors are rewritten with a doc- prefix regardless of resolution", () => {
  const result = renderReadme({
    ...BASE,
    markdown: "# Title\n\n## Section One\n\nSee [section one](#section-one) and [a made-up anchor](#nowhere).\n",
    resolveRepoPath: alwaysMissing,
  });
  assert.match(result.html, /<h2 id="doc-title">Title<\/h2>/);
  assert.match(result.html, /<h2 id="doc-section-one">Section One<\/h2>/);
  assert.match(result.html, /href="#doc-section-one"/);
  assert.match(result.html, /href="#doc-nowhere"/);
  // Unresolved (unknown) same-doc anchors are surfaced for reporting, not treated as build errors.
  assert.deepEqual(result.unresolvedAnchors, ["#nowhere"]);
});

test("duplicate heading text is de-duplicated by github-slugger and each heading keeps a stable, unique id", () => {
  const result = renderReadme({
    ...BASE,
    markdown: "# Title\n\n## Section\n\nFirst.\n\n## Section\n\nSecond.\n",
    resolveRepoPath: alwaysMissing,
  });
  const ids = result.headings.map((heading) => heading.id);
  assert.deepEqual(ids, ["doc-title", "doc-section", "doc-section-1"]);
  assert.equal(new Set(ids).size, ids.length);
  // The README's own H1 is demoted to H2 so it never collides with the
  // page's own <h1>, while every other level is left as-is.
  assert.deepEqual(result.headings.map((heading) => heading.level), [2, 2, 2]);
});

test("relative repository links resolve to GitHub blob/tree URLs based on tracked-path kind", () => {
  const resolveRepoPath = (repoRelativePath) => {
    if (repoRelativePath === "projects/foundation/01-x/docs/results.md") return { exists: true, isDirectory: false };
    if (repoRelativePath === "projects/foundation/01-x/tests") return { exists: true, isDirectory: true };
    return { exists: false, isDirectory: false };
  };
  const result = renderReadme({
    ...BASE,
    markdown: "[results](docs/results.md#outcome) and [tests](tests) and [root](/docs/other.md)",
    resolveRepoPath,
  });
  assert.match(result.html, /href="https:\/\/github\.com\/pauxru\/Paul-Professional-Projects\/blob\/main\/projects\/foundation\/01-x\/docs\/results\.md#outcome"/);
  assert.match(result.html, /href="https:\/\/github\.com\/pauxru\/Paul-Professional-Projects\/tree\/main\/projects\/foundation\/01-x\/tests"/);
  // A leading "/" is repo-root relative, not project-folder relative.
  assert.match(result.html, /href="https:\/\/github\.com\/pauxru\/Paul-Professional-Projects\/blob\/main\/docs\/other\.md"/);
  // The root-level link doesn't exist in this fixture's tracked index; it is
  // still linked (illustrative/nonexistent paths are surfaced, not fatal).
  assert.deepEqual(result.unresolvedLinks, ["/docs/other.md"]);
});

test("external http(s) and mailto links pass through unchanged", () => {
  const result = renderReadme({
    ...BASE,
    markdown: "[site](https://example.com/page) and [insecure](http://localhost:5000/dev) and [contact](mailto:test@example.com)",
    resolveRepoPath: alwaysMissing,
  });
  assert.match(result.html, /href="https:\/\/example\.com\/page"/);
  assert.match(result.html, /href="http:\/\/localhost:5000\/dev"/);
  assert.match(result.html, /href="mailto:test@example\.com"/);
});

test("unsafe schemes, protocol-relative links, path traversal and backslash paths are rejected explicitly", () => {
  const cases = [
    "[bad](javascript:alert(1))",
    "[bad](data:text/html,x)",
    "[bad](vbscript:msgbox(1))",
    "[bad](//evil.example.com/x)",
    "[bad](../../../../etc/passwd)",
    "[bad](docs\\windows-path.md)",
  ];
  for (const markdown of cases) {
    assert.throws(
      () => renderReadme({ ...BASE, markdown, resolveRepoPath: alwaysMissing }),
      undefined,
      `expected an explicit throw for: ${markdown}`,
    );
  }
});

test("resolveHref rejects an empty link target rather than silently producing a broken href", () => {
  assert.throws(() => resolveHref("", {
    projectFolder: BASE.projectFolder,
    repoBaseUrl: BASE.repoBaseUrl,
    ref: BASE.ref,
    resolveRepoPath: alwaysMissing,
    knownIds: new Set(),
    unresolved: { anchors: [], links: [] },
  }));
});

test("markdown images never become <img>: they render as descriptive links, local or external", () => {
  const resolveRepoPath = (repoRelativePath) =>
    repoRelativePath === "projects/foundation/01-x/diagram.png"
      ? { exists: true, isDirectory: false }
      : { exists: false, isDirectory: false };
  const result = renderReadme({
    ...BASE,
    markdown: "![architecture diagram](diagram.png)\n\n![remote](https://cdn.example.com/pic.png)",
    resolveRepoPath,
  });
  assert.doesNotMatch(result.html, /<img/);
  assert.match(result.html, /Image: architecture diagram/);
  assert.match(result.html, /href="https:\/\/github\.com\/pauxru\/Paul-Professional-Projects\/blob\/main\/projects\/foundation\/01-x\/diagram\.png"/);
  assert.match(result.html, /Image: remote/);
  assert.match(result.html, /href="https:\/\/cdn\.example\.com\/pic\.png"/);
});

test("sanitization strips scripts, styles, iframes, inline event handlers and any non-heading id (no DOM clobbering)", () => {
  const result = renderReadme({
    ...BASE,
    markdown: [
      "# Title",
      "",
      "Legit text.",
      "",
      "<script>alert(1)</script>",
      "",
      "<style>body{background:red}</style>",
      "",
      "<iframe src=\"https://evil.example.com\"></iframe>",
      "",
      "<img src=\"x\" onerror=\"alert(1)\">",
      "",
      "<p id=\"getElementById\">clobbering attempt</p>",
      "",
      "<a id=\"submit\" href=\"https://example.com\">weird anchor id</a>",
      "",
    ].join("\n"),
    resolveRepoPath: alwaysMissing,
  });
  assert.doesNotMatch(result.html, /<script/i);
  assert.doesNotMatch(result.html, /<style/i);
  assert.doesNotMatch(result.html, /<iframe/i);
  assert.doesNotMatch(result.html, /onerror/i);
  assert.doesNotMatch(result.html, /<img/i);
  // Only h2-h6 may carry an id, and only the ones we assign ourselves.
  assert.doesNotMatch(result.html, /id="getElementById"/);
  assert.doesNotMatch(result.html, /id="submit"/);
  assert.match(result.html, /<h2 id="doc-title">Title<\/h2>/);
});

test("tables, code blocks and task-list checkboxes are preserved", () => {
  const result = renderReadme({
    ...BASE,
    markdown: [
      "# Title",
      "",
      "```js",
      "console.log(1)",
      "```",
      "",
      "| A | B |",
      "|---|---|",
      "| 1 | 2 |",
      "",
      "- [ ] todo",
      "- [x] done",
    ].join("\n"),
    resolveRepoPath: alwaysMissing,
  });
  assert.match(result.html, /<pre><code class="language-js">console\.log\(1\)/);
  assert.match(result.html, /<table>/);
  assert.match(result.html, /<th>A<\/th>/);
  assert.match(result.html, /type="checkbox"/);
});

test("reading minutes is a positive integer derived from rendered text length", () => {
  const short = renderReadme({ ...BASE, markdown: "# T\n\nHi.", resolveRepoPath: alwaysMissing });
  assert.equal(short.readingMinutes, 1);
  const long = renderReadme({ ...BASE, markdown: `# T\n\n${"word ".repeat(1000)}`, resolveRepoPath: alwaysMissing });
  assert.ok(long.readingMinutes >= 5);
});

test("a fragment on a link to a different document is preserved but not doc- prefixed (that anchor belongs to the target file's own render, not this one)", () => {
  const resolveRepoPath = () => ({ exists: true, isDirectory: false });
  const result = renderReadme({
    ...BASE,
    markdown: "[other doc heading](docs/other.md#some-heading)",
    resolveRepoPath,
  });
  assert.match(result.html, /#some-heading"/);
  assert.doesNotMatch(result.html, /#doc-some-heading/);
});

test("raw HTML links are rewritten without losing their inline labels", () => {
  const result = renderReadme({
    ...BASE,
    markdown: '# Title\n\nRead <a href="docs/results.md">the results</a> and <a href="#title">the introduction</a>.',
    resolveRepoPath: () => ({ exists: true, isDirectory: false }),
  });
  assert.match(result.html, /<a href="https:\/\/github\.com\/pauxru\/Paul-Professional-Projects\/blob\/main\/projects\/foundation\/01-x\/docs\/results.md">the results<\/a>/);
  assert.match(result.html, /<a href="#doc-title">the introduction<\/a>/);
  assert.throws(() => renderReadme({ ...BASE, markdown: '<a href="javascript:alert(1)">bad</a>', resolveRepoPath: alwaysMissing }));
});

test("raw headings and form controls cannot introduce application IDs or duplicate document IDs", () => {
  const result = renderReadme({
    ...BASE,
    markdown: '# Title\n\n<h1 id="main">Raw heading</h1>\n\n<h2 id="top">Other heading</h2>\n\n<input type="password">',
    resolveRepoPath: alwaysMissing,
  });
  assert.doesNotMatch(result.html, /<h1|id="main"|id="top"|type="password"/);
  assert.match(result.html, /id="doc-title"/);
  assert.throws(() => renderReadme({ ...BASE, markdown: '# Title\n\n<h2 id="doc-title">Duplicate</h2>', resolveRepoPath: alwaysMissing }), /Duplicate document heading/);
});

test("encoded paths, query strings, Unicode fragments and existing prefixes retain their meaning", () => {
  const result = renderReadme({
    ...BASE,
    markdown: '# Caf\u00e9\n\n[fragment](#caf%C3%A9) [prefixed](#doc-caf%C3%A9) [file](docs/with%20space.md?plain=1#detail)',
    resolveRepoPath: (value) => ({ exists: value === "projects/foundation/01-x/docs/with space.md", isDirectory: false }),
  });
  assert.deepEqual(result.unresolvedAnchors, []);
  assert.deepEqual(result.unresolvedLinks, []);
  assert.match(result.html, /href="#doc-caf\u00e9"/);
  assert.match(result.html, /with%20space.md\?plain=1#detail/);
  assert.throws(() => renderReadme({ ...BASE, markdown: "[bad](%2e%2e/%2e%2e/%2e%2e/%2e%2e/private)", resolveRepoPath: alwaysMissing }), /outside the repository/);
});
