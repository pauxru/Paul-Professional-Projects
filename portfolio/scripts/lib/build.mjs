// Orchestrates the content pipeline: combines the git-tracked source of
// truth (catalogue.json, project READMEs/docs, icon assets) with the
// curated authoring inputs (project-summaries.json, case-studies.source.json)
// into the final in-memory {projects, caseStudies} structures. Pure/no
// side effects beyond reading repository files — never writes anything, so
// it is safe to import from unit tests without triggering a sync.
import fs from "node:fs";
import path from "node:path";

import { buildTrackedIndex, getDefaultRef, getRemoteBaseUrl } from "./repo.mjs";
import { loadCatalogue, deriveFocusFromSkills } from "./catalogue.mjs";
import { renderReadme } from "./markdown.mjs";
import { buildProjectDocuments } from "./documents.mjs";
import { buildCaseStudies } from "./case-studies.mjs";

const SCOPE_BY_TRACK = {
  foundation:
    "Self-directed reference implementation with fictional data and local infrastructure; not client work, not a production deployment, and not evidence of compliance certification.",
  "dotnet-azure-modernization":
    "Self-directed modernization study measured in a local or simulated environment; not a customer engagement, and not evidence of production migration outcomes.",
  "ai-application-engineering":
    "Self-directed evaluation using synthetic or offline data and deterministic/simulated components; not a deployed AI service and not evidence of production model behavior.",
};

const DATA_DIR = path.resolve(import.meta.dirname, "..", "..", "src", "data");
const ASSETS_ICON_PREFIX = "assets/projects";

function iconAssetPath(number) {
  return `${ASSETS_ICON_PREFIX}/${String(number).padStart(2, "0")}.svg`;
}

/** Load the curated per-project summaries/focus tags, keyed by project number. */
export function loadProjectSummaries(dataDir = DATA_DIR) {
  const filePath = path.join(dataDir, "project-summaries.json");
  const raw = JSON.parse(fs.readFileSync(filePath, "utf8"));
  if (!Array.isArray(raw)) {
    throw new Error("project-summaries.json must be a JSON array.");
  }
  const byNumber = new Map();
  for (const entry of raw) {
    if (!Number.isInteger(entry.number)) {
      throw new Error(`project-summaries.json entry has an invalid "number": ${JSON.stringify(entry)}`);
    }
    if (typeof entry.summary !== "string" || !entry.summary.trim()) {
      throw new Error(`project-summaries.json entry #${entry.number} is missing a "summary".`);
    }
    if (entry.title !== undefined && (typeof entry.title !== "string" || !entry.title.trim())) {
      throw new Error(`project-summaries.json entry #${entry.number} has an invalid title.`);
    }
    if (entry.focus !== undefined && (!Array.isArray(entry.focus) || entry.focus.length === 0 || entry.focus.some((tag) => typeof tag !== "string" || !tag.trim()))) {
      throw new Error(`project-summaries.json entry #${entry.number} has an invalid "focus" array.`);
    }
    if (byNumber.has(entry.number)) {
      throw new Error(`project-summaries.json has a duplicate entry for project #${entry.number}.`);
    }
    byNumber.set(entry.number, entry);
  }
  return byNumber;
}

/** Load the curated raw case-study source entries. */
export function loadCaseStudySource(dataDir = DATA_DIR) {
  const filePath = path.join(dataDir, "case-studies.source.json");
  const raw = JSON.parse(fs.readFileSync(filePath, "utf8"));
  if (!Array.isArray(raw)) {
    throw new Error("case-studies.source.json must be a JSON array.");
  }
  return raw;
}

/** Build one Project record from its normalized catalogue entry. */
export function buildProject({ entry, repoRoot, repoBaseUrl, ref, trackedIndex, summaryEntry, featuredNumbers }) {
  if (!summaryEntry) {
    throw new Error(`Missing project-summaries.json entry for project #${entry.number} (${entry.slug}).`);
  }

  const projectFolder = `projects/${entry.track}/${entry.slug}`;
  const readmeRepoPath = `${projectFolder}/README.md`;
  const readmeInfo = trackedIndex.resolve(readmeRepoPath);
  if (!readmeInfo.exists || readmeInfo.isDirectory) {
    throw new Error(`Project #${entry.number} (${entry.slug}) has no tracked README at ${readmeRepoPath}.`);
  }

  const iconRepoPath = iconAssetPath(entry.number);
  const iconInfo = trackedIndex.resolve(iconRepoPath);
  if (!iconInfo.exists || iconInfo.isDirectory) {
    throw new Error(`Project #${entry.number} (${entry.slug}) has no tracked icon asset at ${iconRepoPath}.`);
  }

  const markdown = fs.readFileSync(path.join(repoRoot, readmeRepoPath), "utf8");
  const resolveRepoPath = (repoRelativePath) => trackedIndex.resolve(repoRelativePath);
  const rendered = renderReadme({ markdown, projectFolder, repoBaseUrl, ref, resolveRepoPath });
  if (rendered.unresolvedAnchors.length || rendered.unresolvedLinks.length) {
    throw new Error(`Unresolved README references in ${readmeRepoPath}: ${[...rendered.unresolvedAnchors, ...rendered.unresolvedLinks].join(", ")}`);
  }

  const trackedDocPaths = trackedIndex
    .filesUnder(`${projectFolder}/docs`)
    .filter((filePath) => filePath.toLowerCase().endsWith(".md"));
  const { documents, documentationCount, decisionRecordCount } = buildProjectDocuments({
    repoRoot, projectFolder, trackedDocPaths, repoBaseUrl, ref,
  });

  const focus = summaryEntry.focus && summaryEntry.focus.length
    ? summaryEntry.focus
    : deriveFocusFromSkills(entry.skillsOrSignal);
  if (!focus.length) {
    throw new Error(`Project #${entry.number} (${entry.slug}) resolved to zero focus tags.`);
  }

  return {
    number: entry.number,
    slug: entry.slug,
    title: summaryEntry.title?.trim() ?? entry.title.split(":")[0].trim(),
    summary: summaryEntry.summary,
    track: entry.track,
    languages: entry.languages,
    focus,
    scope: SCOPE_BY_TRACK[entry.track],
    sourcePath: projectFolder,
    sourceUrl: `${repoBaseUrl}/tree/${ref}/${projectFolder}`,
    readmeUrl: `${repoBaseUrl}/blob/${ref}/${readmeRepoPath}`,
    docsUrl: `${repoBaseUrl}/tree/${ref}/${projectFolder}/docs`,
    documentationCount,
    decisionRecordCount,
    documents,
    readmeHtml: rendered.html,
    headings: rendered.headings,
    readingMinutes: rendered.readingMinutes,
    featured: featuredNumbers.has(entry.number),
  };
}

function assertUniqueSlugs(projects) {
  const seen = new Set();
  for (const project of projects) {
    if (seen.has(project.slug)) {
      throw new Error(`Duplicate project slug "${project.slug}".`);
    }
    seen.add(project.slug);
  }
}

function assertDocumentationTotals(projects, expectedDocs, expectedAdrs) {
  const totalDocs = projects.reduce((sum, project) => sum + project.documentationCount, 0);
  const totalAdrs = projects.reduce((sum, project) => sum + project.decisionRecordCount, 0);
  if (expectedDocs !== undefined && totalDocs !== expectedDocs) {
    throw new Error(`Expected ${expectedDocs} total tracked documents across all projects; counted ${totalDocs}.`);
  }
  if (expectedAdrs !== undefined && totalAdrs !== expectedAdrs) {
    throw new Error(`Expected ${expectedAdrs} total decision records across all projects; counted ${totalAdrs}.`);
  }
}

/**
 * Build the complete content set from a git repository root.
 *
 * @param {object} [options]
 * @param {string} [options.repoRoot] - absolute path to the git repo root; auto-detected if omitted.
 * @param {string} [options.dataDir] - directory containing project-summaries.json / case-studies.source.json.
 * @param {number} [options.expectedDocumentationCount] - hard-fail cross-check against known-good total.
 * @param {number} [options.expectedDecisionRecordCount] - hard-fail cross-check against known-good total.
 */
export function buildAll({
  repoRoot,
  dataDir = DATA_DIR,
  expectedDocumentationCount,
  expectedDecisionRecordCount,
} = {}) {
  if (!repoRoot) {
    throw new Error("buildAll() requires an explicit repoRoot.");
  }
  const repoBaseUrl = getRemoteBaseUrl(repoRoot);
  const ref = getDefaultRef(repoRoot);
  const trackedIndex = buildTrackedIndex(repoRoot);

  const catalogue = loadCatalogue(repoRoot);
  const summaries = loadProjectSummaries(dataDir);
  if (summaries.size !== catalogue.length) {
    throw new Error("Every catalogue project must have exactly one curated summary.");
  }
  const caseStudySource = loadCaseStudySource(dataDir);
  const featuredNumbers = new Set(caseStudySource.map((entry) => entry.number));

  const projects = catalogue.map((entry) =>
    buildProject({
      entry,
      repoRoot,
      repoBaseUrl,
      ref,
      trackedIndex,
      summaryEntry: summaries.get(entry.number),
      featuredNumbers,
    }),
  );

  assertUniqueSlugs(projects);
  assertDocumentationTotals(projects, expectedDocumentationCount, expectedDecisionRecordCount);

  const projectsByNumber = new Map(projects.map((project) => [project.number, project]));
  const resolveRepoPath = (repoRelativePath) => trackedIndex.resolve(repoRelativePath);
  const caseStudies = buildCaseStudies(caseStudySource, {
    repoBaseUrl, ref, resolveRepoPath, projectsByNumber,
  });

  const featuredCount = projects.filter((project) => project.featured).length;
  if (featuredCount !== caseStudies.length) {
    throw new Error(
      `Featured project count (${featuredCount}) does not match case study count (${caseStudies.length}).`,
    );
  }

  return { projects, caseStudies, repoBaseUrl, ref };
}
