// Renders a project README to sanitized, embeddable HTML using marked +
// github-slugger + sanitize-html, with strict link/image handling:
//  - same-document anchors are rewritten to a "doc-" prefixed id space so
//    they can never collide with ids elsewhere on the curated page;
//  - relative repository links are rewritten to GitHub blob/tree URLs;
//  - external http(s)/mailto links pass through unchanged;
//  - any other scheme, a protocol-relative URL, or a path that would escape
//    the repository is rejected loudly (no silent stripping/fallback);
//  - markdown images are never turned into <img> (no remote/local image
//    loading from README content); they become descriptive links instead.
import { Marked } from "marked";
import GithubSlugger from "github-slugger";
import sanitizeHtml from "sanitize-html";
import path from "node:path";

const ID_PREFIX = "doc-";
const SAFE_SCHEMES = new Set(["http", "https", "mailto"]);
const SCHEME_PATTERN = /^([a-zA-Z][a-zA-Z0-9+.-]*):/;

const SANITIZE_OPTIONS = {
  allowedTags: [
    "p", "br", "hr",
    "h2", "h3", "h4", "h5", "h6",
    "strong", "em", "del", "blockquote",
    "ul", "ol", "li",
    "code", "pre",
    "table", "thead", "tbody", "tr", "th", "td",
    "a", "sup", "sub", "kbd", "input",
  ],
  allowedAttributes: {
    h2: ["id"], h3: ["id"], h4: ["id"], h5: ["id"], h6: ["id"],
    a: ["href", "title"],
    code: ["class"],
    pre: ["class"],
    th: ["align"],
    td: ["align"],
    input: ["type", "checked", "disabled"],
  },
  allowedSchemes: ["http", "https", "mailto"],
  allowProtocolRelative: false,
  disallowedTagsMode: "discard",
  // No tag is allowed an id except our own controlled heading ids above, so
  // there is no DOM-clobbering surface (no name/id on form-adjacent tags).
};

function escapeHtml(text) {
  return text
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function escapeAttr(text) {
  return escapeHtml(text);
}

function normalizeHref(href, source) {
  if (typeof href !== "string" || !href.trim()) throw new Error(`Empty link target in ${source}.`);
  const value = href.trim();
  if (/[\u0000-\u001f\u007f]/.test(value)) throw new Error(`Control character in a link in ${source}.`);
  if (value.startsWith("//")) throw new Error(`Refusing protocol-relative link in ${source}.`);
  if (value.includes("\\")) throw new Error(`Refusing backslash path in a link in ${source}.`);
  const scheme = SCHEME_PATTERN.exec(value)?.[1].toLowerCase();
  if (scheme && !SAFE_SCHEMES.has(scheme)) throw new Error(`Refusing unsafe link scheme "${scheme}:" in ${source}.`);
  return value;
}

/**
 * Classify and rewrite a single markdown href relative to the project's
 * source folder. Throws for anything unsafe rather than silently
 * dropping/rewriting it away.
 */
export function resolveHref(href, context) {
  const { projectFolder, repoBaseUrl, ref, resolveRepoPath, knownIds, unresolved } = context;
  const source = `${projectFolder}/README.md`;

  href = normalizeHref(href, source);

  if (href.startsWith("#")) {
    const fragment = decodeURIComponent(href.slice(1));
    if (!fragment) return "#";
    const id = fragment.startsWith(ID_PREFIX) ? fragment : `${ID_PREFIX}${fragment}`;
    if (fragment && !knownIds.has(id)) {
      unresolved.anchors.push(href);
    }
    return `#${id}`;
  }

  const schemeMatch = SCHEME_PATTERN.exec(href);
  if (schemeMatch) return href;

  const hashIndex = href.indexOf("#");
  const beforeFragment = hashIndex === -1 ? href : href.slice(0, hashIndex);
  const queryIndex = beforeFragment.indexOf("?");
  const query = queryIndex === -1 ? "" : beforeFragment.slice(queryIndex);
  const rawPath = decodeURIComponent(queryIndex === -1 ? beforeFragment : beforeFragment.slice(0, queryIndex));
  const fragment = hashIndex === -1 ? "" : href.slice(hashIndex + 1);
  if (rawPath.includes("\\") || /[\u0000-\u001f\u007f]/.test(rawPath)) throw new Error(`Invalid encoded path in ${source}.`);

  const repoRelative = (rawPath === "" ? source : rawPath.startsWith("/")
    ? path.posix.normalize(rawPath.slice(1))
    : path.posix.normalize(path.posix.join(projectFolder, rawPath))).replace(/\/+$/, "");

  if (repoRelative === ".." || repoRelative.startsWith("../") || repoRelative.startsWith("/")) {
    throw new Error(`Refusing to link outside the repository ("${href}") in ${source}.`);
  }

  const info = resolveRepoPath ? resolveRepoPath(repoRelative) : { exists: false, isDirectory: false };
  if (!info.exists) {
    unresolved.links.push(href);
  }
  const kind = info.isDirectory ? "tree" : "blob";
  const encodedPath = repoRelative
    .split("/")
    .map((segment) => encodeURIComponent(segment))
    .join("/");
  const url = `${repoBaseUrl}/${kind}/${encodeURIComponent(ref)}/${encodedPath}${query}`;
  // A fragment on a link to a *different* file targets that file's own
  // GitHub-rendered anchor space, not this document's "doc-" prefixed ids.
  return fragment ? `${url}#${fragment}` : url;
}

/** First pass: walk the document purely to collect heading id/text/level. */
function collectHeadings(markdown) {
  const slugger = new GithubSlugger();
  const headings = [];
  const marked = new Marked({ gfm: true });
  marked.use({
    renderer: {
      heading(token) {
        const text = this.parser.parseInline(token.tokens, this.parser.textRenderer);
        const slug = slugger.slug(text);
        const level = token.depth === 1 ? 2 : Math.min(token.depth, 6);
        headings.push({ id: `${ID_PREFIX}${slug}`, text, level });
        return "";
      },
    },
  });
  marked.parse(markdown);
  return headings;
}

/** Second pass: render the actual sanitized HTML, reusing pass-one heading ids. */
function renderHtml(markdown, { headings }) {
  let headingCursor = 0;
  const marked = new Marked({ gfm: true });
  marked.use({
    renderer: {
      heading(token) {
        const heading = headings[headingCursor];
        headingCursor += 1;
        const inner = this.parser.parseInline(token.tokens);
        return `<h${heading.level} id="${escapeAttr(heading.id)}">${inner}</h${heading.level}>\n`;
      },
      link(token) {
        const inner = this.parser.parseInline(token.tokens);
        const titleAttr = token.title ? ` title="${escapeAttr(token.title)}"` : "";
        return `<a href="${escapeAttr(token.href)}"${titleAttr}>${inner}</a>`;
      },
      image(token) {
        // Never emit <img>: README images become descriptive inline links
        // instead of loading any local or remote image resource. Rendered
        // inline (not wrapped in its own <p>) since image tokens appear
        // inside whatever block-level context already wraps them.
        const label = (token.text || token.title || "Image").trim() || "Image";
        return `<a href="${escapeAttr(token.href)}">Image: ${escapeHtml(label)}</a>`;
      },
    },
  });
  return marked.parse(markdown);
}

function countReadingMinutes(html) {
  const text = html
    .replace(/<[^>]*>/g, " ")
    .replace(/&[a-zA-Z0-9#]+;/g, " ")
    .trim();
  const words = text.length ? text.split(/\s+/).filter(Boolean).length : 0;
  return Math.max(1, Math.ceil(words / 200));
}

/**
 * Render a project's README markdown into sanitized HTML plus derived
 * metadata (headings for a table of contents, reading time estimate, and
 * any unresolved same-doc anchors / repo-relative links worth logging).
 *
 * @param {object} options
 * @param {string} options.markdown - raw README source.
 * @param {string} options.projectFolder - repo-root-relative project folder, e.g. "projects/foundation/01-x".
 * @param {string} options.repoBaseUrl - e.g. "https://github.com/owner/repo".
 * @param {string} options.ref - branch/ref for source links, e.g. "main".
 * @param {(repoRelativePath: string) => {exists:boolean,isDirectory:boolean}} [options.resolveRepoPath]
 */
export function renderReadme({ markdown, projectFolder, repoBaseUrl, ref, resolveRepoPath }) {
  if (typeof markdown !== "string") {
    throw new Error(`renderReadme requires a markdown string for ${projectFolder}.`);
  }
  if (!projectFolder || !repoBaseUrl || !ref) {
    throw new Error("renderReadme requires projectFolder, repoBaseUrl and ref.");
  }

  const headings = collectHeadings(markdown);
  const knownIds = new Set(headings.map((heading) => heading.id));
  const unresolved = { anchors: [], links: [] };

  const rawHtml = renderHtml(markdown, {
    headings, projectFolder, repoBaseUrl, ref, resolveRepoPath, knownIds, unresolved,
  });
  const assignedIds = new Set();
  const headingAttributes = (tagName, attribs) => {
    const { id, ...rest } = attribs;
    if (!knownIds.has(id)) return { tagName, attribs: rest };
    if (assignedIds.has(id)) throw new Error(`Duplicate document heading ID "${id}" in ${projectFolder}/README.md.`);
    assignedIds.add(id);
    return { tagName, attribs: { ...rest, id } };
  };
  const html = sanitizeHtml(rawHtml, {
    ...SANITIZE_OPTIONS,
    exclusiveFilter: (frame) => frame.tag === "input" && (frame.attribs.type !== "checkbox" || !("disabled" in frame.attribs)),
    transformTags: {
      h1: () => ({ tagName: "h2", attribs: {} }),
      h2: headingAttributes,
      h3: headingAttributes,
      h4: headingAttributes,
      h5: headingAttributes,
      h6: headingAttributes,
      a: (tagName, attribs) => ({
        tagName,
        attribs: "href" in attribs ? {
          ...attribs,
          href: resolveHref(attribs.href, { projectFolder, repoBaseUrl, ref, resolveRepoPath, knownIds, unresolved }),
        } : attribs,
      }),
    },
  });

  return {
    html,
    headings,
    readingMinutes: countReadingMinutes(html),
    unresolvedAnchors: unresolved.anchors,
    unresolvedLinks: unresolved.links,
  };
}

export const ID_PREFIX_CONSTANT = ID_PREFIX;
