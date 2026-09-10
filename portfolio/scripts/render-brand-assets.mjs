import { chromium } from "@playwright/test";
import { fileURLToPath } from "node:url";

const publicRoot = new URL("../public/", import.meta.url);
const browser = await chromium.launch({
  channel: process.env.PLAYWRIGHT_CHANNEL || (process.platform === "win32" ? "msedge" : undefined),
});

try {
  const page = await browser.newPage({ viewport: { width: 1200, height: 630 }, deviceScaleFactor: 1 });
  await page.goto(new URL("images/social-card.svg", publicRoot).href);
  await page.locator("svg").screenshot({ path: fileURLToPath(new URL("images/social-card.png", publicRoot)) });
  for (const [size, filename] of [[32, "favicon-32.png"], [180, "apple-touch-icon.png"]]) {
    await page.goto(new URL("favicon.svg", publicRoot).href);
    await page.locator("svg").evaluate((element, dimension) => {
      element.setAttribute("width", String(dimension));
      element.setAttribute("height", String(dimension));
    }, size);
    await page.locator("svg").screenshot({ path: fileURLToPath(new URL(filename, publicRoot)), omitBackground: true });
  }
  console.log("Rendered the social sharing card, favicon and touch icon.");
} finally {
  await browser.close();
}
