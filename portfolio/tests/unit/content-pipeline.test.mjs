import assert from "node:assert/strict";
import { test } from "node:test";
import path from "node:path";
import fs from "node:fs";
import { fileURLToPath } from "node:url";

import { findRepoRoot, listTrackedFiles, TrackedIndex } from "../../scripts/lib/repo.mjs";
import {
  splitTopLevelCommas,
  deriveFocusFromSkills,
  capitalizeFirst,
  normalizeCatalogueEntry,
  loadCatalogue,
} from "../../scripts/lib/catalogue.mjs";
import { classifyDocumentKind, extractTitle } from "../../scripts/lib/documents.mjs";
import { buildAll } from "../../scripts/lib/build.mjs";

const TESTS_DIR = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = findRepoRoot(TESTS_DIR);

test("splitTopLevelCommas does not split inside parentheses", () => {
  assert.deepEqual(
    splitTopLevelCommas("retrieval evaluation (Recall@k, MRR, nDCG), HMAC webhooks"),
    ["retrieval evaluation (Recall@k, MRR, nDCG)", "HMAC webhooks"],
  );
  assert.deepEqual(splitTopLevelCommas("a, b, c"), ["a", "b", "c"]);
  assert.deepEqual(splitTopLevelCommas(""), []);
});

test("capitalizeFirst only touches the first character, preserving embedded acronyms", () => {
  assert.equal(capitalizeFirst("HMAC webhooks"), "HMAC webhooks");
  assert.equal(capitalizeFirst("idempotency keys"), "Idempotency keys");
  assert.equal(capitalizeFirst(""), "");
});

test("deriveFocusFromSkills produces a short, capitalized, non-empty tag list", () => {
  const focus = deriveFocusFromSkills(
    "idempotency keys, transactional outbox, provider-timeout recovery, HMAC webhooks, refund lifecycle.",
  );
  assert.deepEqual(focus, ["Idempotency keys", "Transactional outbox", "Provider-timeout recovery", "HMAC webhooks"]);
});

test("normalizeCatalogueEntry rejects malformed entries explicitly instead of defaulting silently", () => {
  const valid = {
    number: 1, title: "X", track: "foundation", built: true, slug: "01-x",
    problem: "p", tech: "t", skills: "s",
    scan: { files: 1, total_lines: 1, lines_by_language: {}, languages: ["C#"], has_readme: true, has_test_script: false, adr_count: 0, doc_count: 0 },
  };
  assert.doesNotThrow(() => normalizeCatalogueEntry(valid));
  assert.throws(() => normalizeCatalogueEntry({ ...valid, number: "1" }));
  assert.throws(() => normalizeCatalogueEntry({ ...valid, track: "unknown-track" }));
  assert.throws(() => normalizeCatalogueEntry({ ...valid, slug: "Not A Slug" }));
  assert.throws(() => normalizeCatalogueEntry({ ...valid, scan: { ...valid.scan, has_readme: false } }));
  assert.throws(() => normalizeCatalogueEntry({ ...valid, problem: undefined, story: undefined }));
  assert.throws(() => normalizeCatalogueEntry({ ...valid, scan: { ...valid.scan, languages: undefined } }));
  assert.throws(() => normalizeCatalogueEntry({ ...valid, scan: { ...valid.scan, languages: [] } }));
  assert.throws(() => normalizeCatalogueEntry({ ...valid, scan: { ...valid.scan, languages: ["C#", "C#"] } }));
  assert.throws(() => normalizeCatalogueEntry({ ...valid, scan: { ...valid.scan, languages: [1] } }));
});

test("classifyDocumentKind uses real repository directory conventions", () => {
  assert.equal(classifyDocumentKind("decisions/ADR-001-outbox-vs-2pc.md"), "Decision record");
  assert.equal(classifyDocumentKind("adr/0001-something.md"), "Decision record");
  assert.equal(classifyDocumentKind("adr-0001-something.md"), "Decision record");
  assert.equal(classifyDocumentKind("architecture/architecture.md"), "Architecture");
  assert.equal(classifyDocumentKind("runbooks/incident.md"), "Runbook");
  assert.equal(classifyDocumentKind("security/security-review.md"), "Security");
  assert.equal(classifyDocumentKind("portfolio/demo-script.md"), "Portfolio");
  assert.equal(classifyDocumentKind("known-limitations.md"), "Limitations");
  assert.equal(classifyDocumentKind("results-stable.md"), "Results");
  assert.equal(classifyDocumentKind("database-schema.md"), "Reference");
});

test("extractTitle reads the first markdown heading and strips inline formatting, falling back to the filename", () => {
  assert.equal(extractTitle("# `ADR-001`: Outbox vs 2PC\n\nBody.", "adr-001.md"), "ADR-001: Outbox vs 2PC");
  assert.equal(extractTitle("Some text with no heading at all.", "known-limitations.md"), "Known limitations");
  assert.equal(extractTitle("## **Bold** Title", "x.md"), "Bold Title");
});

test("the full catalogue has exactly 50 entries numbered 1..50 with unique slugs", () => {
  const catalogue = loadCatalogue(REPO_ROOT);
  assert.equal(catalogue.length, 50);
  assert.deepEqual(catalogue.map((entry) => entry.number), Array.from({ length: 50 }, (_, index) => index + 1));
  assert.equal(new Set(catalogue.map((entry) => entry.slug)).size, 50);
});

test("buildAll produces all 50 projects and derives documentation totals from tracked source", () => {
  const { projects, caseStudies, repoBaseUrl, ref } = buildAll({ repoRoot: REPO_ROOT });

  assert.equal(projects.length, 50);
  assert.deepEqual(projects.map((project) => project.number).sort((a, b) => a - b), Array.from({ length: 50 }, (_, index) => index + 1));
  assert.equal(new Set(projects.map((project) => project.slug)).size, 50);

  const totalDocs = projects.reduce((sum, project) => sum + project.documentationCount, 0);
  const totalAdrs = projects.reduce((sum, project) => sum + project.decisionRecordCount, 0);
  const trackedDocs = listTrackedFiles(REPO_ROOT, ["projects"]).filter((file) => /\/docs\/.*\.md$/i.test(file));
  assert.equal(totalDocs, trackedDocs.length);
  assert.equal(totalAdrs, projects.flatMap((project) => project.documents).filter((document) => document.kind === "Decision record").length);

  assert.equal(caseStudies.length, 3);
  assert.deepEqual(caseStudies.map((study) => study.number).sort((a, b) => a - b), [35, 43, 50]);

  const featured = projects.filter((project) => project.featured);
  assert.equal(featured.length, 3);
  assert.deepEqual(featured.map((project) => project.number).sort((a, b) => a - b), [35, 43, 50]);
  assert.deepEqual(featured.map((project) => project.title), ["Crown Jewels Bridge", "Retrieval Quality Lab", "Silent-Failure Observability"]);

  assert.equal(repoBaseUrl, "https://github.com/pauxru/Paul-Professional-Projects");
  assert.equal(typeof ref, "string");
  assert.ok(ref.length > 0);

  // Every project must expose real, non-empty README HTML with at least one
  // heading, a positive reading time, and either its own tracked icon route
  // input (validated during the build) or an explicit failure — buildAll
  // already throws if the icon/README are missing, so reaching this point is
  // itself part of the assertion.
  for (const project of projects) {
    assert.ok(project.readmeHtml.length > 0, `project ${project.number} has empty readmeHtml`);
    assert.ok(project.headings.length > 0, `project ${project.number} has no headings`);
    assert.ok(project.readingMinutes >= 1, `project ${project.number} has invalid readingMinutes`);
    assert.ok(project.focus.length > 0, `project ${project.number} has no focus tags`);
    assert.ok(project.summary.length <= 260, `Project ${project.number} needs a concise card summary.`);
    assert.match(project.sourceUrl, /^https:\/\/github\.com\//);
    assert.match(project.readmeUrl, /^https:\/\/github\.com\//);
    assert.match(project.docsUrl, /^https:\/\/github\.com\//);
    assert.equal(project.readmeUrl.includes("Paul-Professional-Projects"), true);
  }
});

test("tracked directory links resolve consistently with and without a trailing slash", () => {
  const index = new TrackedIndex(["projects/foundation/01-x/docs/decisions/adr.md"]);
  assert.deepEqual(index.resolve("projects/foundation/01-x/docs/decisions/"), { exists: true, isDirectory: true });
  assert.deepEqual(index.resolve("projects/foundation/01-x/docs/decisions"), { exists: true, isDirectory: true });
  assert.deepEqual(index.resolve("projects/foundation/01-x/docs/decisions/adr.md"), { exists: true, isDirectory: false });
});

test("importing the sync CLI does not write generated content", async () => {
  const generated = new URL("../../src/data/projects.json", import.meta.url);
  const before = fs.statSync(generated, { bigint: true }).mtimeNs;
  const module = await import("../../scripts/sync-projects.mjs");
  assert.equal(typeof module.syncProjects, "function");
  assert.equal(fs.statSync(generated, { bigint: true }).mtimeNs, before);
});

test("buildAll rejects an unknown repository root rather than silently producing empty content", () => {
  assert.throws(() => buildAll({ repoRoot: undefined }));
});
