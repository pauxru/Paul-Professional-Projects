import assert from "node:assert/strict";
import { test } from "node:test";
import { normalizeBase, resolveSite, withBase } from "../../config/site.mjs";
import { testOutputDirectory } from "../../config/testing.mjs";
import { matchesFilters, projectSearchText, readFilters, updateFilterUrl } from "../../src/lib/filters.ts";
import { formatDate, readingMinutes } from "../../src/lib/format.ts";
import profile from "../../src/data/profile.json" with { type: "json" };

test("deployment supports repository and custom-domain roots without doubled prefixes", () => {
  assert.deepEqual(resolveSite({}), { site: "https://paulrukwaro.com", base: "/", home: "https://paulrukwaro.com/" });
  assert.equal(normalizeBase(), "/");
  assert.equal(resolveSite({ SITE_URL: "https://paulrukwaro.com" }).home, "https://paulrukwaro.com/");
  assert.equal(resolveSite({ SITE_URL: "https://pauxru.github.io", SITE_BASE_PATH: "/Paul-Professional-Projects" }).home, "https://pauxru.github.io/Paul-Professional-Projects/");
  assert.equal(resolveSite({ SITE_URL: "https://example.test", SITE_BASE_PATH: "/" }).home, "https://example.test/");
  assert.equal(withBase("/projects/", "/Paul-Professional-Projects/"), "/Paul-Professional-Projects/projects/");
  assert.equal(withBase("/resume/Paul-Rukwaro-Resume.pdf", "/"), "/resume/Paul-Rukwaro-Resume.pdf");
  assert.equal(normalizeBase("/portfolio/"), "/portfolio");
});

test("browser output selection permits only known isolated build directories", () => {
  assert.equal(testOutputDirectory({}), "dist");
  for (const directory of ["dist", ".root-build", ".repo-build"]) {
    assert.equal(testOutputDirectory({ PORTFOLIO_TEST_OUT_DIR: directory }), directory);
  }
  for (const directory of ["", "../", "public", "/tmp/site", "dist; echo unsafe"]) {
    assert.throws(() => testOutputDirectory({ PORTFOLIO_TEST_OUT_DIR: directory }));
  }
});

test("invalid public site settings fail rather than emitting incorrect or private canonical URLs", () => {
  for (const value of ["http://example.test", "https://user:password@example.test", "https://example.test/portfolio", "https://example.test?key=private", "https://example.test/#fragment", "not-a-url"]) {
    assert.throws(() => resolveSite({ SITE_URL: value }));
  }
  for (const value of ["portfolio", "//example.test", "/../private", "/a//b", "/space here", "/?q=1"]) {
    assert.throws(() => normalizeBase(value));
  }
  assert.throws(() => withBase("//example.test", "/"));
  assert.throws(() => withBase("\\private", "/"));
});

test("search combines words and filters while retaining programming-language punctuation", () => {
  const project = { number: 35, title: "Crown Jewels Bridge", summary: "C++ and .NET interoperability with explicit ownership.", track: "dotnet-azure-modernization", languages: ["C#", "C++"], focus: ["Interop", "Memory safety"] };
  const text = projectSearchText(project);
  const state = { q: "c++ ownership", track: "", language: "C#" };
  assert.equal(matchesFilters(text, project.track, project.languages, state), true);
  assert.equal(matchesFilters(text, project.track, project.languages, { ...state, q: "Python" }), false);
  assert.equal(matchesFilters(text, project.track, project.languages, { ...state, track: "foundation" }), false);
  assert.equal(matchesFilters(text, project.track, project.languages, { ...state, q: "CSHARP" }), true);
  assert.equal(matchesFilters(text, project.track, project.languages, { ...state, q: "35 bridge" }), true);
  assert.equal(matchesFilters(text, project.track, project.languages, { ...state, q: "<script>alert(1)</script>" }), false);
});

test("bookmark state round-trips C#, C++ and query terms without losing unrelated parameters", () => {
  const url = new URL("https://example.test/portfolio/projects/?source=referral#foundation");
  const state = { q: "C++ lifetime", track: "dotnet-azure-modernization", language: "C#" };
  const result = updateFilterUrl(url, state);
  assert.equal(result.searchParams.get("language"), "C#");
  assert.equal(result.searchParams.get("q"), "C++ lifetime");
  assert.equal(result.searchParams.get("source"), "referral");
  assert.equal(result.hash, "");
  assert.deepEqual(readFilters(result.searchParams, ["dotnet-azure-modernization"], ["C#"]), { state, invalid: [] });
  assert.equal(url.hash, "#foundation");
  assert.equal(updateFilterUrl(result, { q: "", track: "", language: "" }).search, "?source=referral");
});

test("invalid shared-link filters are surfaced while preserving valid filters", () => {
  const parsed = readFilters(new URLSearchParams("q=banking&track=invalid&language=C%23"), ["foundation"], ["C#"]);
  assert.deepEqual(parsed, { state: { q: "banking", track: "", language: "C#" }, invalid: ["track"] });
});

test("publication dates and reading estimates have stable, explicit semantics", () => {
  assert.equal(formatDate("2026-09-10"), "September 10, 2026");
  assert.throws(() => formatDate("not-a-date"), RangeError);
  assert.equal(readingMinutes({ sections: [] }), 1);
  assert.equal(readingMinutes({ sections: [{ heading: "A section", paragraphs: [Array(201).fill("word").join(" ")] }] }), 2);
});

test("professional leadership links reference explicit employers rather than parsing display labels", () => {
  const employers = new Set(profile.experience.map((job) => job.company));
  for (const item of profile.leadership) {
    assert.ok(item.employers.length > 0);
    for (const company of item.employers) assert.ok(employers.has(company), `Unknown employer ${company}`);
  }
  assert.ok(profile.leadership[2].employers.includes("Prime Bank Kenya"));
});
