import assert from "node:assert/strict";
import { test } from "node:test";

import { notes } from "../../src/data/notes.ts";

const FORBIDDEN_PATTERNS = [
  /my (team|manager|direct reports?)/i,
  /the company (saved|earned)/i,
  /\$[\d,]+(\.\d+)? (million|thousand|in savings)/i,
  /guarantee(d|s)?\b/i,
  /production[- ]proven/i,
  /real (customers?|clients?) (used|relied on)/i,
];

test("exactly three engineering notes exist, one per curated case-study project", () => {
  assert.equal(notes.length, 3);
  assert.deepEqual(notes.map((note) => note.projectNumber).sort((a, b) => a - b), [35, 43, 50]);
  assert.equal(new Set(notes.map((note) => note.slug)).size, 3);
});

test("every note is published 2026-09-10 and has 3-5 substantive sections", () => {
  for (const note of notes) {
    assert.equal(note.published, "2026-09-10");
    assert.ok(note.sections.length >= 3 && note.sections.length <= 5, `note "${note.slug}" has ${note.sections.length} sections`);
    for (const section of note.sections) {
      assert.ok(section.heading.trim().length > 0);
      assert.ok(section.paragraphs.length >= 1);
      for (const paragraph of section.paragraphs) {
        assert.ok(paragraph.trim().length > 20, `section "${section.heading}" has a suspiciously short paragraph`);
      }
    }
  }
});

test("notes contain no fabricated teams, direct reports, business savings or broad guarantees", () => {
  for (const note of notes) {
    const text = [note.summary, ...note.sections.flatMap((section) => [section.heading, ...section.paragraphs, section.code ?? ""])].join(" ");
    for (const pattern of FORBIDDEN_PATTERNS) {
      assert.doesNotMatch(text, pattern, `note "${note.slug}" matched forbidden pattern ${pattern}`);
    }
  }
});

test("the bridge note (project 35) cites the exact bounded hostile-input and compiler-comparison figures", () => {
  const note = notes.find((item) => item.projectNumber === 35);
  const text = note.sections.flatMap((section) => section.paragraphs).join(" ");
  assert.match(text, /328/);
  assert.match(text, /600/);
  assert.match(text, /2,818/);
  assert.match(text, /4,000/);
  assert.match(text, /no real 60,000-line customer system/i);
});

test("the retrieval note (project 43) cites the heading-severance and prediction figures and scopes the retrieval method", () => {
  const note = notes.find((item) => item.projectNumber === 43);
  const text = note.sections.flatMap((section) => section.paragraphs).join(" ");
  assert.match(text, /45/);
  assert.match(text, /\b0\b/);
  assert.match(text, /12 predictions/);
  assert.match(text, /7 held/);
  assert.match(text, /5 (were contradicted|failed)/);
  assert.match(text, /lexical|LSA|latent-semantic/i);
  assert.match(text, /not a trained neural retriever|no answer-generation stage/i);
});

test("the observability note (project 50) cites the detection/delay/budget figures and scopes the feature representation", () => {
  const note = notes.find((item) => item.projectNumber === 50);
  const text = note.sections.flatMap((section) => section.paragraphs).join(" ");
  assert.match(text, /8%/);
  assert.match(text, /12 days/);
  assert.match(text, /24 calls/);
  assert.match(text, /4-gram/);
  assert.match(text, /256-dimensional/);
  assert.match(text, /not a semantic embedding model|not.*(OpenTelemetry|live trace)/i);
});
