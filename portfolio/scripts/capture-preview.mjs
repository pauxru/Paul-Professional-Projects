import { chromium } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { resolveSite, withBase } from "../config/site.mjs";
import { loadLocalEnvironment } from "../config/environment.mjs";

loadLocalEnvironment();
const { base } = resolveSite();
const origin = process.env.PORTFOLIO_PREVIEW_ORIGIN || "http://127.0.0.1:4321";
const url = new URL(withBase("/", base), origin).href;
const output = new URL("../preview/", import.meta.url);
await mkdir(output, { recursive: true });
const browser = await chromium.launch({
  channel: process.env.PLAYWRIGHT_CHANNEL || (process.platform === "win32" ? "msedge" : undefined),
});
try {
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  for (const [name, width, height] of [["home-desktop", 1440, 1050], ["home-mobile", 390, 1150]]) {
    await page.setViewportSize({ width, height });
    const response = await page.goto(url);
    if (!response?.ok()) throw new Error(`The portfolio preview is unavailable at ${url}. Start npm run dev first.`);
    await page.evaluate(() => {
      for (const image of document.images) image.loading = "eager";
    });
    await page.waitForFunction(() => Array.from(document.images).every((image) => image.complete && image.naturalWidth > 0));
    await page.screenshot({ path: fileURLToPath(new URL(`${name}.png`, output)), fullPage: false });
  }
  console.log("Saved desktop and mobile homepage previews in portfolio/preview/.");
} finally {
  await browser.close();
}
