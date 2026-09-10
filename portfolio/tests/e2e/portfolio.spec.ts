import { test, expect } from "@playwright/test";
import { existsSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import projects from "../../src/data/projects.json" with { type: "json" };
import { notes } from "../../src/data/notes";
import { resolveSite } from "../../config/site.mjs";
import { testOutputDirectory } from "../../config/testing.mjs";

const deployment = resolveSite();
const publicBase = deployment.base === "/" ? "/" : `${deployment.base}/`;
const projectRoute = (number: number) => {
  const project = projects.find((item) => item.number === number);
  if (!project) throw new Error(`Missing fixture project ${number}`);
  return `projects/${project.slug}/`;
};

test("all 50 projects are discoverable with direct source and documentation links", async ({ page }) => {
  await page.goto("projects/");
  await expect(page.locator("[data-project-number]")).toHaveCount(50);
  await expect(page.locator(".readme-link")).toHaveCount(50);
  await expect(page.locator(".design-docs-link")).toHaveCount(50);
  await expect(page.locator(".deep-dive-label")).toHaveCount(3);
  await expect(page.locator("[data-results-count]")).toHaveText("50 of 50 projects");
  for (const track of ["foundation", "dotnet-azure-modernization", "ai-application-engineering"]) {
    await page.getByLabel("Engineering track", { exact: true }).selectOption(track);
    await expect(page.locator("[data-project-number]:visible")).toHaveCount(projects.filter((project) => project.track === track).length);
    await expect(page).toHaveURL(new RegExp(`track=${track}`));
  }
});

test("search, language filters, shared URLs, clear actions and empty states stay consistent", async ({ page }) => {
  await page.goto("projects/");
  const search = page.getByLabel("Search projects", { exact: true });
  await search.fill("Crown Jewels");
  await expect(page.locator("[data-project-number]:visible")).toHaveCount(1);
  await expect(page.locator('[data-project-number="35"]')).toBeVisible();
  await page.reload();
  await expect(search).toHaveValue("Crown Jewels");
  await expect(page.locator("[data-project-number]:visible")).toHaveCount(1);
  await page.getByRole("button", { name: "Clear filters", exact: true }).click();
  await page.getByLabel("Language", { exact: true }).selectOption("C#");
  await expect(page.locator("[data-project-number]:visible")).toHaveCount(projects.filter((project) => project.languages.includes("C#")).length);
  await expect(page).toHaveURL(/language=C%23/);
  await page.getByRole("button", { name: "Clear filters", exact: true }).click();
  await search.fill("<script>nonexistent-query</script>");
  await expect(page.locator("[data-empty-state]")).toBeVisible();
  await expect(page.locator("[data-results-count]")).toHaveText("0 of 50 projects");
  await page.getByRole("button", { name: "Show all projects" }).click();
  await expect(search).toBeFocused();
  await expect(page.locator("[data-project-number]:visible")).toHaveCount(50);
  await expect(page).not.toHaveURL(/[?&](q|track|language)=/);
});

test("an invalid shared-link filter is explained, not silently applied", async ({ page }) => {
  await page.goto("projects/?track=not-a-track&language=C%23");
  await expect(page.locator("[data-filter-warning]")).toContainText("unrecognized track");
  await expect(page.getByLabel("Language", { exact: true })).toHaveValue("C#");
  await expect(page.locator("[data-project-number]:visible")).toHaveCount(projects.filter((project) => project.languages.includes("C#")).length);
  await expect(page).not.toHaveURL(/track=not-a-track/);
});

test("the mobile menu has real keyboard and dismissal behavior", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("./");
  const toggle = page.locator("[data-nav-toggle]");
  const navigation = page.getByRole("navigation", { name: "Primary navigation" });
  await expect(toggle).toHaveAccessibleName("Menu");
  await expect(navigation).not.toBeVisible();
  await toggle.click();
  await expect(toggle).toHaveAttribute("aria-expanded", "true");
  await expect(navigation).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(navigation).not.toBeVisible();
  await expect(toggle).toBeFocused();
  await toggle.click();
  await navigation.getByRole("link", { name: "Projects", exact: true }).click();
  await expect(page).toHaveURL(/\/projects\/$/);
  await expect(page.locator("[data-nav-toggle]")).toHaveAttribute("aria-expanded", "false");
});

test("the skip link reaches the main content", async ({ page }) => {
  await page.goto("./");
  await page.keyboard.press("Tab");
  await expect(page.getByRole("link", { name: "Skip to content" })).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(page.locator("#main")).toBeFocused();
});

test("tabbing out of the mobile menu leaves the focused content visible", async ({ page }) => {
  for (const viewport of [{ width: 390, height: 600 }, { width: 667, height: 375 }]) {
    await page.setViewportSize(viewport);
    await page.goto("./");
    const toggle = page.locator("[data-nav-toggle]");
    await toggle.focus();
    await page.keyboard.press("Enter");
    await expect(toggle).toHaveAttribute("aria-expanded", "true");
    for (let index = 0; index < 7; index++) await page.keyboard.press("Tab");
    const contentLink = page.getByRole("link", { name: "Explore my work", exact: true });
    await expect(contentLink).toBeFocused();
    await expect(toggle).toHaveAttribute("aria-expanded", "false");
    const unobscured = await contentLink.evaluate((element) => {
      const bounds = element.getBoundingClientRect();
      const top = document.elementFromPoint(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
      return top === element || (top !== null && element.contains(top));
    });
    expect(unobscured, `Focused content is obscured at ${viewport.width}x${viewport.height}`).toBe(true);
  }
});

test("the complete library and mobile navigation remain available without JavaScript", async ({ browser, baseURL }) => {
  const context = await browser.newContext({ javaScriptEnabled: false, viewport: { width: 390, height: 844 } });
  const page = await context.newPage();
  try {
    await page.goto(new URL("projects/", baseURL).href);
    await expect(page.locator("[data-project-number]:visible")).toHaveCount(50);
    await expect(page.getByRole("navigation", { name: "Primary navigation" })).toBeVisible();
    await expect(page.locator("[data-project-filters]")).not.toBeVisible();
    await expect(page.locator("noscript .callout")).toBeVisible();
    await expect(page.locator("noscript .callout")).toContainText("All 50 projects are available below without JavaScript.");
    await page.goto(new URL(projectRoute(35), baseURL).href);
    await page.locator(".readme-disclosure > summary").click();
    await expect(page.locator(".readme-body")).toBeVisible();
  } finally {
    await context.close();
  }
});

test("deep links open the full README within an editorial case study", async ({ page }) => {
  const project = projects.find((item) => item.number === 35);
  if (!project?.headings[0]) throw new Error("Project 35 has no documented headings.");
  await page.goto(`${projectRoute(35)}#${encodeURIComponent(project.headings[0].id)}`);
  await expect(page.locator(".readme-disclosure")).toHaveAttribute("open", "");
  await expect(page.locator(`[id="${project.headings[0].id}"]`)).toBeVisible();
});

test("every public page has usable content, metadata, real local links and unique IDs", async ({ page, baseURL }) => {
  test.setTimeout(180_000);
  const routes = ["", "projects/", "experience/", "leadership/", "notes/", "contact/", "resume/", "privacy/", ...notes.map((note) => `notes/${note.slug}/`), ...projects.map((project) => `projects/${project.slug}/`)];
  const dist = path.resolve(fileURLToPath(new URL(`../../${testOutputDirectory()}/`, import.meta.url)));
  const documents = new Map<string, Set<string>>();
  const fragments: { from: string; to: string; fragment: string }[] = [];
  const browserErrors: string[] = [];
  const outsideRequests: string[] = [];
  const origin = new URL(baseURL!).origin;
  page.on("pageerror", (error) => browserErrors.push(error.message));
  page.on("request", (request) => {
    if (new URL(request.url()).origin !== origin) outsideRequests.push(request.url());
  });

  for (const route of routes) {
    const response = await page.goto(route || "./");
    expect(response?.status(), route).toBe(200);
    await expect(page.locator("main h1"), route).toHaveCount(1);
    await expect(page.locator("main"), route).toHaveCount(1);
    await expect(page.locator("html")).toHaveAttribute("lang", "en");
    const metadata = await page.evaluate(() => ({
      title: document.title,
      canonical: document.querySelector<HTMLLinkElement>('link[rel="canonical"]')?.href,
      description: document.querySelector<HTMLMetaElement>('meta[name="description"]')?.content,
      ids: Array.from(document.querySelectorAll("[id]"), (element) => element.id),
      hrefs: Array.from(document.querySelectorAll<HTMLAnchorElement>("a[href]"), (anchor) => anchor.href),
      assets: Array.from(document.querySelectorAll<HTMLImageElement>("img"), (image) => ({ src: image.currentSrc || image.src, alt: image.getAttribute("alt") })),
      jsonLd: Array.from(document.querySelectorAll('script[type="application/ld+json"]'), (script) => JSON.parse(script.textContent ?? "")),
    }));
    expect(metadata.title.length, route).toBeGreaterThan(15);
    expect(metadata.description?.length, route).toBeGreaterThan(30);
    expect(metadata.canonical, route).toBe(new URL(route, deployment.home).href);
    expect(metadata.jsonLd.length, route).toBeGreaterThan(0);
    expect(new Set(metadata.ids).size, `Duplicate IDs on ${route}`).toBe(metadata.ids.length);
    const current = new URL(page.url());
    documents.set(current.pathname, new Set(metadata.ids));
    for (const asset of metadata.assets) expect(asset.alt, `Unlabelled image on ${route}`).not.toBeNull();
    for (const href of [...metadata.hrefs, ...metadata.assets.map((asset) => asset.src)]) {
      const target = new URL(href);
      expect(["javascript:", "vbscript:", "data:"], `${route}: ${href}`).not.toContain(target.protocol);
      if (target.origin !== origin) continue;
      expect(target.pathname.startsWith(publicBase), `Escaped deployment base: ${href}`).toBe(true);
      const relative = decodeURIComponent(target.pathname.slice(publicBase.length));
      const file = path.resolve(dist, ...relative.split("/"));
      expect(file === dist || file.startsWith(`${dist}${path.sep}`), `Escaped build directory: ${href}`).toBe(true);
      const candidates = [file, path.join(file, "index.html")];
      expect(candidates.some((candidate) => existsSync(candidate) && statSync(candidate).isFile()), `Missing internal link from ${route}: ${href}`).toBe(true);
      if (target.hash) fragments.push({ from: route, to: target.pathname, fragment: decodeURIComponent(target.hash.slice(1)) });
    }
  }
  for (const link of fragments) {
    const ids = documents.get(link.to);
    if (ids) expect(ids.has(link.fragment), `Missing #${link.fragment} on ${link.to}, linked from ${link.from}`).toBe(true);
  }
  expect(browserErrors).toEqual([]);
  expect(outsideRequests).toEqual([]);
});

test("core reading surfaces fit phone, tablet and desktop widths", async ({ page }) => {
  test.setTimeout(180_000);
  const routes = ["", "projects/", "experience/", "leadership/", "resume/", "contact/", "notes/", `notes/${notes[0].slug}/`, projectRoute(1), projectRoute(35), projectRoute(43), projectRoute(50)];
  for (const width of [360, 390, 768, 1024, 1440]) {
    await page.setViewportSize({ width, height: 950 });
    for (const route of routes) {
      await page.goto(route || "./");
      const dimensions = await page.evaluate(() => ({ document: document.documentElement.scrollWidth, viewport: document.documentElement.clientWidth }));
      expect(dimensions.document, `${route || "homepage"} overflows at ${width}px`).toBeLessThanOrEqual(dimensions.viewport + 1);
    }
  }
});

test("resume download, feeds, sitemap and the 404 route are wired to the actual deployment base", async ({ page, request }) => {
  await page.goto("resume/");
  const download = page.locator('a[href$="/resume/Paul-Rukwaro-Resume.pdf"]').first();
  await expect(download).toBeVisible();
  const pdf = await request.get((await download.getAttribute("href"))!);
  expect(pdf.status()).toBe(200);
  expect((await pdf.body()).subarray(0, 5).toString()).toBe("%PDF-");
  const rss = await request.get("rss.xml");
  expect(rss.status()).toBe(200);
  expect(await rss.text()).toContain(`${deployment.home}notes/`);
  const robots = await request.get("robots.txt");
  expect(await robots.text()).toContain(`${deployment.home}sitemap-index.xml`);
  const sitemap = await request.get("sitemap-0.xml");
  expect(sitemap.status()).toBe(200);
  const xml = await sitemap.text();
  for (const project of projects) expect(xml).toContain(`${deployment.home}projects/${project.slug}/`);
  const response = await page.goto("this-route-does-not-exist/");
  expect(response?.status()).toBe(404);
  await expect(page.locator('meta[name="robots"]')).toHaveAttribute("content", "noindex,follow");
  await expect(page.getByRole("link", { name: "Return home" })).toHaveAttribute("href", publicBase);
});

test("reduced-motion preferences are respected", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.goto("./");
  const behavior = await page.evaluate(() => ({
    scroll: getComputedStyle(document.documentElement).scrollBehavior,
    transition: getComputedStyle(document.querySelector(".hero-visual img")!).transitionDuration,
  }));
  expect(behavior.scroll).toBe("auto");
  expect(behavior.transition).toBe("0s");
});
