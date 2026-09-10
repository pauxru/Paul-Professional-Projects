#!/usr/bin/env node
// CLI entry point for `npm run sync`. Builds the full project/case-study
// content set from tracked repository source (see scripts/lib/build.mjs)
// and writes the generated JSON + icon assets this pipeline owns. Cleanup
// is always targeted at exactly the files this script generates — it never
// touches anything else under the repository root.
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { findRepoRoot } from "./lib/repo.mjs";
import { buildAll } from "./lib/build.mjs";

const SCRIPT_DIR = path.dirname(fileURLToPath(import.meta.url));
const PORTFOLIO_ROOT = path.resolve(SCRIPT_DIR, "..");
const DATA_DIR = path.join(PORTFOLIO_ROOT, "src", "data");
const ICONS_DIR = path.join(PORTFOLIO_ROOT, "public", "project-icons");

function writeJson(filePath, value) {
  fs.writeFileSync(filePath, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

/** Remove only previously generated icon files this script owns (NN.svg not in the current set). */
function cleanStaleIcons(dir, keepFilenames) {
  if (!fs.existsSync(dir)) return;
  for (const filename of fs.readdirSync(dir)) {
    if (/^\d{2}\.svg$/.test(filename) && !keepFilenames.has(filename)) {
      fs.unlinkSync(path.join(dir, filename));
    }
  }
}

function copyIcons(repoRoot, projects) {
  fs.mkdirSync(ICONS_DIR, { recursive: true });
  const keep = new Set();
  for (const project of projects) {
    const filename = `${String(project.number).padStart(2, "0")}.svg`;
    keep.add(filename);
    const source = path.join(repoRoot, "assets", "projects", filename);
    const destination = path.join(ICONS_DIR, filename);
    fs.copyFileSync(source, destination);
  }
  cleanStaleIcons(ICONS_DIR, keep);
  return keep.size;
}

export function syncProjects() {
  const repoRoot = findRepoRoot(SCRIPT_DIR);
  const { projects, caseStudies, repoBaseUrl, ref } = buildAll({ repoRoot, dataDir: DATA_DIR });

  writeJson(path.join(DATA_DIR, "projects.json"), projects);
  writeJson(path.join(DATA_DIR, "case-studies.json"), caseStudies);
  const iconCount = copyIcons(repoRoot, projects);

  const totalDocs = projects.reduce((sum, project) => sum + project.documentationCount, 0);
  const totalAdrs = projects.reduce((sum, project) => sum + project.decisionRecordCount, 0);

  console.log("Content sync complete.");
  console.log(`  Repository:            ${repoBaseUrl} @ ${ref}`);
  console.log(`  Projects:              ${projects.length}`);
  console.log(`  Case studies:          ${caseStudies.length}`);
  console.log(`  Tracked documents:     ${totalDocs}`);
  console.log(`  Decision records:      ${totalAdrs}`);
  console.log(`  Icons copied:          ${iconCount}`);
  console.log(`  Wrote:                 src/data/projects.json`);
  console.log(`  Wrote:                 src/data/case-studies.json`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) syncProjects();
