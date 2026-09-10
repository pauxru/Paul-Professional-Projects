// Git-backed repository introspection used to make the content pipeline
// reproducible from tracked source alone (no reliance on the working tree's
// untracked/build/cache noise, and no reliance on paths outside this repo).
import { execFileSync, spawnSync } from "node:child_process";
import path from "node:path";

/**
 * Run a git command against a repository root and return trimmed stdout.
 * Throws (does not swallow) on any non-zero exit so pipeline failures are loud.
 */
function git(repoRoot, args) {
  return execFileSync("git", args, {
    cwd: repoRoot,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
  }).trim();
}

/** Resolve the root of the git repository containing `startDir`. */
export function findRepoRoot(startDir) {
  const root = git(startDir, ["rev-parse", "--show-toplevel"]);
  if (!root) {
    throw new Error(`Unable to resolve git repository root from "${startDir}".`);
  }
  // git always reports posix-style paths; normalize for local fs use.
  return path.resolve(root);
}

/**
 * List tracked files under the repo root, optionally scoped to path
 * prefixes/globs understood by `git ls-files`. Untracked, ignored and
 * build/cache files are never included because this shells out to git
 * rather than walking the filesystem.
 */
export function listTrackedFiles(repoRoot, pathspecs = []) {
  const args = ["ls-files", "-z", "--", ...pathspecs];
  const raw = execFileSync("git", args, { cwd: repoRoot, encoding: "utf8" });
  return raw.split("\u0000").filter(Boolean);
}

/** Resolve the GitHub https base URL (no trailing slash) from `origin`. */
export function getRemoteBaseUrl(repoRoot) {
  const remote = git(repoRoot, ["config", "--get", "remote.origin.url"]);
  if (!remote) {
    throw new Error(`Repository at ${repoRoot} has no "origin" remote configured.`);
  }
  let httpsUrl;
  const sshMatch = /^git@github\.com:([a-zA-Z0-9_.-]+\/[a-zA-Z0-9_.-]+?)(?:\.git)?$/.exec(remote);
  const httpsMatch = /^https:\/\/github\.com\/([a-zA-Z0-9_.-]+\/[a-zA-Z0-9_.-]+?)(?:\.git)?$/.exec(remote);
  if (sshMatch) {
    httpsUrl = `https://github.com/${sshMatch[1]}`;
  } else if (httpsMatch) {
    httpsUrl = `https://github.com/${httpsMatch[1]}`;
  } else {
    throw new Error(
      "Unsupported origin format. Use a standard github.com SSH or HTTPS remote without embedded credentials or URL parameters.",
    );
  }
  return httpsUrl;
}

/**
 * Resolve the ref (branch name) that generated source links should point at.
 * Prefers the remote's recorded default branch, then the current branch,
 * and only falls back to a literal constant when neither is available
 * (e.g. a detached HEAD checkout with no remote-tracking information).
 */
export function getDefaultRef(repoRoot) {
  for (const args of [
    ["symbolic-ref", "--quiet", "refs/remotes/origin/HEAD"],
    ["symbolic-ref", "--quiet", "--short", "HEAD"],
  ]) {
    const result = spawnSync("git", args, { cwd: repoRoot, encoding: "utf8" });
    if (result.error) throw result.error;
    if (result.status === 0) return result.stdout.trim().replace(/^refs\/remotes\/origin\//, "");
    if (result.status !== 1) throw new Error(`Unable to resolve source ref: ${result.stderr.trim()}`);
  }
  // A detached CI checkout may not have origin/HEAD; production deploys use main.
  return "main";
}

/**
 * An in-memory, reproducible index of every tracked path in the repository
 * (files and the directories that contain them), used to classify link
 * targets as blob/tree/missing without touching the filesystem directly
 * from the markdown renderer.
 */
export class TrackedIndex {
  constructor(trackedFiles) {
    this.files = new Set(trackedFiles);
    this.dirs = new Set();
    for (const file of trackedFiles) {
      let dir = path.posix.dirname(file);
      while (dir && dir !== ".") {
        this.dirs.add(dir);
        dir = path.posix.dirname(dir);
      }
    }
  }

  /** @returns {{exists:boolean,isDirectory:boolean}} */
  resolve(repoRelativePath) {
    const normalized = path.posix.normalize(repoRelativePath).replace(/\/+$/, "");
    if (normalized === "." && this.files.size > 0) return { exists: true, isDirectory: true };
    if (this.files.has(normalized)) return { exists: true, isDirectory: false };
    if (this.dirs.has(normalized)) return { exists: true, isDirectory: true };
    return { exists: false, isDirectory: false };
  }

  /** Files directly under `dirPrefix/` (non-recursive is not needed here; recursive). */
  filesUnder(dirPrefix) {
    const prefix = `${dirPrefix}/`;
    return [...this.files].filter((file) => file.startsWith(prefix));
  }
}

export function buildTrackedIndex(repoRoot, pathspecs = []) {
  return new TrackedIndex(listTrackedFiles(repoRoot, pathspecs));
}
