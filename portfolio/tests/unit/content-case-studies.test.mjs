import assert from "node:assert/strict";
import { test } from "node:test";

import { buildCaseStudy, buildCaseStudies } from "../../scripts/lib/case-studies.mjs";

function fixtureEntry(overrides = {}) {
  return {
    number: 35,
    leading: "Modernize the interface.",
    accent: "Preserve the core.",
    summary: "Summary text.",
    setting: "Windows interop experiment",
    focus: "Contracts / lifetime / compatibility",
    tools: "C++ / C# / .NET",
    image: "boundary.svg",
    imageAlt: "alt text",
    imageCaption: "caption text",
    theme: "bridge",
    responsibility: "Define and evaluate the integration contract.",
    problem: ["Problem paragraph one.", "Problem paragraph two."],
    architecture: [{ title: "Managed host", description: "desc" }],
    architectureNote: "note",
    decisions: [{ title: "t", decision: "d", alternative: "a", tradeoff: "tr" }],
    ownership: [{ title: "Define the contract", description: "desc", path: "native/include/pricing_abi.h", label: "Inspect the ABI contract" }],
    results: [{ value: "328 / 600", label: "Legacy unsafe outcomes", description: "desc" }],
    resultNote: "result note",
    evidence: [{ path: "native/src/abi.cpp", title: "Boundary implementation", description: "desc" }],
    limitations: "limitations text",
    nextSteps: "next steps text",
    ...overrides,
  };
}

const project35 = { number: 35, sourcePath: "projects/dotnet-azure-modernization/35-crown-jewels-bridge" };
function context({ resolveRepoPath } = {}) {
  return {
    repoBaseUrl: "https://github.com/pauxru/Paul-Professional-Projects",
    ref: "main",
    projectsByNumber: new Map([[35, project35]]),
    resolveRepoPath: resolveRepoPath ?? (() => ({ exists: true, isDirectory: false })),
  };
}

test("evidence/ownership paths are resolved relative to the case study's own project folder", () => {
  const seen = [];
  const resolveRepoPath = (repoRelativePath) => {
    seen.push(repoRelativePath);
    return { exists: true, isDirectory: false };
  };
  const study = buildCaseStudy(fixtureEntry(), context({ resolveRepoPath }));
  assert.deepEqual(seen.sort(), [
    "projects/dotnet-azure-modernization/35-crown-jewels-bridge/native/include/pricing_abi.h",
    "projects/dotnet-azure-modernization/35-crown-jewels-bridge/native/src/abi.cpp",
  ].sort());
  assert.equal(study.ownership[0].url, "https://github.com/pauxru/Paul-Professional-Projects/blob/main/projects/dotnet-azure-modernization/35-crown-jewels-bridge/native/include/pricing_abi.h");
});

test("evidence label is the raw path text (displayed to the reader), while ownership label is its own curated action text", () => {
  const study = buildCaseStudy(fixtureEntry(), context());
  assert.equal(study.evidence[0].label, "native/src/abi.cpp");
  assert.equal(study.evidence[0].title, "Boundary implementation");
  assert.equal(study.ownership[0].label, "Inspect the ABI contract");
});

test("a directory path resolves to a GitHub tree URL, a file path resolves to a blob URL", () => {
  const resolveRepoPath = (repoRelativePath) => ({
    exists: true,
    isDirectory: repoRelativePath.endsWith("pricing_abi.h") === false,
  });
  const study = buildCaseStudy(fixtureEntry(), context({ resolveRepoPath }));
  assert.match(study.ownership[0].url, /\/blob\//);
  assert.match(study.evidence[0].url, /\/tree\//);
});

test("an untracked evidence/ownership path fails the build loudly instead of emitting a dead link", () => {
  const resolveRepoPath = () => ({ exists: false, isDirectory: false });
  assert.throws(() => buildCaseStudy(fixtureEntry(), context({ resolveRepoPath })), /not a tracked path/);
});

test("a case study referencing an unknown project number is rejected", () => {
  assert.throws(() => buildCaseStudy(fixtureEntry({ number: 999 }), context()), /does not correspond to a known project/);
});

test("required narrative fields and non-empty arrays are validated explicitly", () => {
  assert.throws(() => buildCaseStudy(fixtureEntry({ summary: "" }), context()));
  assert.throws(() => buildCaseStudy(fixtureEntry({ problem: [] }), context()));
  assert.throws(() => buildCaseStudy(fixtureEntry({ architecture: [] }), context()));
});

test("mobileImage is included only when provided", () => {
  const withoutMobile = buildCaseStudy(fixtureEntry(), context());
  assert.equal("mobileImage" in withoutMobile, false);
  const withMobile = buildCaseStudy(fixtureEntry({ mobileImage: "retrieval-mobile.svg" }), context());
  assert.equal(withMobile.mobileImage, "retrieval-mobile.svg");
});

test("buildCaseStudies rejects duplicate project numbers and sorts by project number", () => {
  const entryA = fixtureEntry({ number: 43 });
  const entryB = fixtureEntry({ number: 35 });
  const ctx = {
    repoBaseUrl: "https://github.com/pauxru/Paul-Professional-Projects",
    ref: "main",
    projectsByNumber: new Map([
      [35, project35],
      [43, { number: 43, sourcePath: "projects/ai-application-engineering/43-retrieval-lab" }],
    ]),
    resolveRepoPath: () => ({ exists: true, isDirectory: false }),
  };
  const built = buildCaseStudies([entryA, entryB], ctx);
  assert.deepEqual(built.map((study) => study.number), [35, 43]);

  assert.throws(() => buildCaseStudies([fixtureEntry(), fixtureEntry()], ctx), /Duplicate case study/);
});
