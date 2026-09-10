// Enumerates a project's tracked documentation (README + docs/**/*.md),
// classifies each document by kind, extracts a human title from its first
// heading, and builds GitHub source links for each document plus the
// project's README/docs/source URLs as a whole.
import fs from "node:fs";
import path from "node:path";

const ADR_SEGMENT_PATTERN = /^(adr|adrs|decisions)$/i;
const ADR_FILENAME_PATTERN = /^adr-/i;

/**
 * Classify a documentation file's "kind" from its path (relative to the
 * project's docs/ folder) using the directory conventions actually present
 * in this repository, with a filename-based fallback for direct-in-docs
 * files that don't sit in a named subfolder.
 */
export function classifyDocumentKind(relativeToDocsDir) {
  const segments = relativeToDocsDir.split("/");
  const filename = segments[segments.length - 1];
  const dirSegments = segments.slice(0, -1);

  if (dirSegments.some((segment) => ADR_SEGMENT_PATTERN.test(segment)) || ADR_FILENAME_PATTERN.test(filename)) {
    return "Decision record";
  }
  if (dirSegments.some((segment) => /^architecture$/i.test(segment))) return "Architecture";
  if (dirSegments.some((segment) => /^runbooks?$/i.test(segment))) return "Runbook";
  if (dirSegments.some((segment) => /^security$/i.test(segment))) return "Security";
  if (dirSegments.some((segment) => /^portfolio$/i.test(segment))) return "Portfolio";

  const lowerName = filename.toLowerCase();
  if (lowerName === "known-limitations.md") return "Limitations";
  if (lowerName === "security-review.md") return "Security";
  if (lowerName === "database-schema.md") return "Reference";
  if (lowerName === "test-results.md") return "Test results";
  if (/results/.test(lowerName)) return "Results";
  return "Reference";
}

/** Strip common inline markdown emphasis/backtick markers from a title line. */
function stripInlineMarkdown(text) {
  return text
    .replace(/`([^`]*)`/g, "$1")
    .replace(/\*\*([^*]*)\*\*/g, "$1")
    .replace(/\*([^*]*)\*/g, "$1")
    .replace(/_([^_]*)_/g, "$1")
    .trim();
}

/** Derive a readable fallback title from a filename when no heading exists. */
function titleFromFilename(filename) {
  const base = filename.replace(/\.md$/i, "");
  const spaced = base.replace(/[-_]+/g, " ").trim();
  return spaced ? spaced.charAt(0).toUpperCase() + spaced.slice(1) : filename;
}

/** Extract a document's title from its first markdown heading, or its filename. */
export function extractTitle(content, filename) {
  const headingMatch = /^#{1,6}\s+(.+?)\s*#*\s*$/m.exec(content);
  if (headingMatch) {
    const cleaned = stripInlineMarkdown(headingMatch[1]);
    if (cleaned) return cleaned;
  }
  return titleFromFilename(filename);
}

function encodeRepoPath(repoRelativePath) {
  return repoRelativePath.split("/").map(encodeURIComponent).join("/");
}

/**
 * Build the DocumentLink[] for one project, plus documentationCount and
 * decisionRecordCount, from the repository's tracked file list.
 *
 * @param {object} options
 * @param {string} options.repoRoot - absolute filesystem repo root.
 * @param {string} options.projectFolder - repo-root-relative project folder.
 * @param {string[]} options.trackedDocPaths - repo-root-relative *.md paths under this project's docs/ dir.
 * @param {string} options.repoBaseUrl
 * @param {string} options.ref
 */
export function buildProjectDocuments({ repoRoot, projectFolder, trackedDocPaths, repoBaseUrl, ref }) {
  const docsPrefix = `${projectFolder}/docs/`;
  const documents = trackedDocPaths
    .slice()
    .sort((a, b) => a.localeCompare(b))
    .map((docPath) => {
      if (!docPath.startsWith(docsPrefix)) {
        throw new Error(`Document path "${docPath}" is not under ${docsPrefix}.`);
      }
      const relativeToDocsDir = docPath.slice(docsPrefix.length);
      const filename = relativeToDocsDir.split("/").pop();
      const absolutePath = path.join(repoRoot, docPath);
      const content = fs.readFileSync(absolutePath, "utf8");
      const title = extractTitle(content, filename);
      const kind = classifyDocumentKind(relativeToDocsDir);
      const url = `${repoBaseUrl}/blob/${ref}/${encodeRepoPath(docPath)}`;
      return { title, url, kind };
    });

  const documentationCount = documents.length;
  const decisionRecordCount = documents.filter((doc) => doc.kind === "Decision record").length;

  return { documents, documentationCount, decisionRecordCount };
}
