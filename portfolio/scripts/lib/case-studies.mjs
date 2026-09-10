// Resolves the curated case-study source content (case-studies.source.json)
// into the final CaseStudy[] contract, turning each evidence/ownership path
// (given relative to the case study's own project folder, matching how
// project READMEs reference their own repository-relative paths) into a
// real GitHub blob/tree URL. Every referenced path must actually exist in
// the tracked-file registry: these are curated, hand-verified references,
// so an unresolvable path is treated as a content bug and fails the build
// loudly rather than silently emitting a dead link.
import path from "node:path";

function encodeRepoPath(repoRelativePath) {
  return repoRelativePath.split("/").map(encodeURIComponent).join("/");
}

function resolveSourceLink(relativePath, { repoBaseUrl, ref, resolveRepoPath, projectFolder }, caseNumber, field) {
  const repoRelativePath = path.posix.normalize(path.posix.join(projectFolder, relativePath));
  if (repoRelativePath === ".." || repoRelativePath.startsWith("../") || repoRelativePath.startsWith("/")) {
    throw new Error(
      `Case study #${caseNumber} ${field} path "${relativePath}" escapes the repository root.`,
    );
  }
  const info = resolveRepoPath(repoRelativePath);
  if (!info || !info.exists) {
    throw new Error(
      `Case study #${caseNumber} ${field} references "${repoRelativePath}", which is not a tracked path in the repository.`,
    );
  }
  const kind = info.isDirectory ? "tree" : "blob";
  return `${repoBaseUrl}/${kind}/${ref}/${encodeRepoPath(repoRelativePath)}`;
}

function requireArray(value, label, caseNumber) {
  if (!Array.isArray(value) || value.length === 0) {
    throw new Error(`Case study #${caseNumber} is missing a non-empty "${label}" array.`);
  }
  return value;
}

/** Build one CaseStudy from its curated source entry. */
export function buildCaseStudy(entry, context) {
  const { projectsByNumber } = context;
  if (!Number.isInteger(entry.number)) {
    throw new Error(`Case study entry has an invalid "number": ${JSON.stringify(entry.number)}.`);
  }
  if (!projectsByNumber.has(entry.number)) {
    throw new Error(`Case study #${entry.number} does not correspond to a known project.`);
  }
  const projectFolder = projectsByNumber.get(entry.number).sourcePath;
  const linkContext = { ...context, projectFolder };
  for (const field of [
    "leading", "accent", "summary", "setting", "focus", "tools", "image",
    "imageAlt", "imageCaption", "theme", "responsibility", "architectureNote",
    "resultNote", "limitations", "nextSteps",
  ]) {
    if (typeof entry[field] !== "string" || !entry[field].trim()) {
      throw new Error(`Case study #${entry.number} is missing a non-empty "${field}".`);
    }
  }

  const problem = requireArray(entry.problem, "problem", entry.number);
  const architecture = requireArray(entry.architecture, "architecture", entry.number).map((item) => ({
    title: item.title,
    description: item.description,
  }));
  const decisions = requireArray(entry.decisions, "decisions", entry.number).map((item) => ({
    title: item.title,
    decision: item.decision,
    alternative: item.alternative,
    tradeoff: item.tradeoff,
  }));
  const results = requireArray(entry.results, "results", entry.number).map((item) => ({
    value: item.value,
    label: item.label,
    description: item.description,
  }));
  const ownership = requireArray(entry.ownership, "ownership", entry.number).map((item) => ({
    title: item.title,
    description: item.description,
    url: resolveSourceLink(item.path, linkContext, entry.number, `ownership entry "${item.title}"`),
    label: item.label,
  }));
  const evidence = requireArray(entry.evidence, "evidence", entry.number).map((item) => ({
    title: item.title,
    description: item.description,
    url: resolveSourceLink(item.path, linkContext, entry.number, `evidence entry "${item.title}"`),
    label: item.path,
  }));

  const caseStudy = {
    number: entry.number,
    leading: entry.leading,
    accent: entry.accent,
    summary: entry.summary,
    setting: entry.setting,
    focus: entry.focus,
    tools: entry.tools,
    image: entry.image,
    imageAlt: entry.imageAlt,
    imageCaption: entry.imageCaption,
    theme: entry.theme,
    responsibility: entry.responsibility,
    problem,
    architecture,
    architectureNote: entry.architectureNote,
    decisions,
    ownership,
    results,
    resultNote: entry.resultNote,
    evidence,
    limitations: entry.limitations,
    nextSteps: entry.nextSteps,
  };
  if (typeof entry.mobileImage === "string" && entry.mobileImage.trim()) {
    caseStudy.mobileImage = entry.mobileImage;
  }
  return caseStudy;
}

/** Build the full CaseStudy[] from curated source entries, sorted by project number. */
export function buildCaseStudies(sourceEntries, context) {
  if (!Array.isArray(sourceEntries) || sourceEntries.length === 0) {
    throw new Error("case-studies.source.json must contain at least one entry.");
  }
  const numbers = new Set();
  for (const entry of sourceEntries) {
    if (numbers.has(entry.number)) {
      throw new Error(`Duplicate case study for project #${entry.number}.`);
    }
    numbers.add(entry.number);
  }
  return sourceEntries
    .map((entry) => buildCaseStudy(entry, context))
    .sort((a, b) => a.number - b.number);
}
