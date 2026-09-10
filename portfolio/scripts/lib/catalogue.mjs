// Loads and normalizes docs/catalogue.json. The catalogue's field shapes vary
// by track (foundation projects describe problem/tech/skills; the two later
// tracks describe story/what/signal), so this module reconciles both shapes
// into one internal representation the rest of the pipeline can rely on.
import fs from "node:fs";
import path from "node:path";

const KNOWN_TRACKS = new Set([
  "foundation",
  "dotnet-azure-modernization",
  "ai-application-engineering",
]);

/** Split a comma-separated phrase list without splitting inside parentheses. */
export function splitTopLevelCommas(text) {
  const parts = [];
  let depth = 0;
  let current = "";
  for (const char of text) {
    if (char === "(") depth += 1;
    if (char === ")") depth = Math.max(0, depth - 1);
    if (char === "," && depth === 0) {
      parts.push(current.trim());
      current = "";
      continue;
    }
    current += char;
  }
  if (current.trim()) parts.push(current.trim());
  return parts.filter(Boolean);
}

/** Capitalize only the first character; leaves embedded acronyms untouched. */
export function capitalizeFirst(text) {
  if (!text) return text;
  return text.charAt(0).toUpperCase() + text.slice(1);
}

/**
 * Derive short focus tags from a foundation-track catalogue entry's `skills`
 * prose. Curated tracks (dotnet-azure-modernization,
 * ai-application-engineering) instead supply hand-picked focus tags via
 * project-summaries.json, since their catalogue entries have no equivalent
 * comma-separated skills field.
 */
export function deriveFocusFromSkills(skillsText, limit = 4) {
  const phrases = splitTopLevelCommas(skillsText)
    .map((phrase) => phrase.replace(/\.$/, "").trim())
    .filter(Boolean)
    .map(capitalizeFirst);
  return phrases.slice(0, limit);
}

/** Read and JSON-parse docs/catalogue.json from the repo root. */
export function loadCatalogueRaw(repoRoot) {
  const filePath = path.join(repoRoot, "docs", "catalogue.json");
  const raw = fs.readFileSync(filePath, "utf8");
  const parsed = JSON.parse(raw);
  if (!Array.isArray(parsed)) {
    throw new Error(`docs/catalogue.json must be a JSON array; got ${typeof parsed}.`);
  }
  return parsed;
}

/**
 * Normalize one raw catalogue entry into a consistent shape, regardless of
 * which of the two field layouts it uses. Throws on any missing/malformed
 * required field rather than silently defaulting.
 */
export function normalizeCatalogueEntry(entry) {
  if (!entry || typeof entry !== "object") {
    throw new Error(`Catalogue entry is not an object: ${JSON.stringify(entry)}`);
  }
  const { number, title, track, built, slug, scan } = entry;
  if (!Number.isInteger(number) || number < 1) {
    throw new Error(`Catalogue entry has an invalid "number": ${JSON.stringify(entry)}`);
  }
  if (typeof title !== "string" || !title.trim()) {
    throw new Error(`Catalogue entry #${number} is missing a "title".`);
  }
  if (!KNOWN_TRACKS.has(track)) {
    throw new Error(`Catalogue entry #${number} has an unknown track "${track}".`);
  }
  if (typeof slug !== "string" || !/^[a-z0-9]+(-[a-z0-9]+)*$/.test(slug)) {
    throw new Error(`Catalogue entry #${number} has an invalid "slug": ${JSON.stringify(slug)}`);
  }
  if (!scan || typeof scan !== "object") {
    throw new Error(`Catalogue entry #${number} is missing "scan" metadata.`);
  }
  const { files, total_lines: totalLines, lines_by_language: linesByLanguage, languages, has_readme: hasReadme } = scan;
  if (!Array.isArray(languages) || !languages.length || languages.some((language) => typeof language !== "string" || !language.trim()) || new Set(languages).size !== languages.length) {
    throw new Error(`Catalogue entry #${number} is missing "scan.languages".`);
  }
  if (hasReadme !== true) {
    throw new Error(`Catalogue entry #${number} (${slug}) reports scan.has_readme=false; a README is required.`);
  }

  // Reconcile the two narrative-field shapes used across tracks.
  const narrativeProblem = entry.problem ?? entry.story;
  const narrativeSummary = entry.tech ?? entry.what;
  const skillsOrSignal = entry.skills ?? entry.signal;
  if (typeof narrativeProblem !== "string" || !narrativeProblem.trim()) {
    throw new Error(`Catalogue entry #${number} (${slug}) is missing a problem/story field.`);
  }
  if (typeof narrativeSummary !== "string" || !narrativeSummary.trim()) {
    throw new Error(`Catalogue entry #${number} (${slug}) is missing a tech/what field.`);
  }
  if (typeof skillsOrSignal !== "string" || !skillsOrSignal.trim()) {
    throw new Error(`Catalogue entry #${number} (${slug}) is missing a skills/signal field.`);
  }

  return {
    number,
    title: title.trim(),
    track,
    built: typeof built === "string" ? built : null,
    slug,
    languages,
    totalLines: typeof totalLines === "number" ? totalLines : null,
    linesByLanguage: linesByLanguage && typeof linesByLanguage === "object" ? linesByLanguage : {},
    filesScanned: typeof files === "number" ? files : null,
    problemOrStory: narrativeProblem.trim(),
    techOrWhat: narrativeSummary.trim(),
    skillsOrSignal: skillsOrSignal.trim(),
    scanDocCount: typeof scan.doc_count === "number" ? scan.doc_count : null,
    scanAdrCount: typeof scan.adr_count === "number" ? scan.adr_count : null,
  };
}

/** Load, normalize, and structurally validate the full 50-entry catalogue. */
export function loadCatalogue(repoRoot) {
  const raw = loadCatalogueRaw(repoRoot);
  const entries = raw.map(normalizeCatalogueEntry);

  if (entries.length !== 50) {
    throw new Error(`Expected exactly 50 catalogue entries; found ${entries.length}.`);
  }
  const numbers = entries.map((entry) => entry.number).sort((a, b) => a - b);
  for (let index = 0; index < 50; index += 1) {
    if (numbers[index] !== index + 1) {
      throw new Error(
        `Catalogue project numbers must be exactly 1..50 with no gaps or duplicates; found ${JSON.stringify(numbers)}.`,
      );
    }
  }
  const slugs = new Set();
  for (const entry of entries) {
    if (slugs.has(entry.slug)) {
      throw new Error(`Duplicate catalogue slug "${entry.slug}" (project #${entry.number}).`);
    }
    slugs.add(entry.slug);
  }

  return entries.sort((a, b) => a.number - b.number);
}
