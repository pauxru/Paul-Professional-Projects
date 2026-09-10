import { defineConfig, devices } from "@playwright/test";
import { loadLocalEnvironment } from "./config/environment.mjs";
import { resolveSite } from "./config/site.mjs";
import { testOutputDirectory } from "./config/testing.mjs";

loadLocalEnvironment();
const { base } = resolveSite();
const outputDirectory = testOutputDirectory();
const port = Number(process.env.PORTFOLIO_TEST_PORT ?? "4377");
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error("PORTFOLIO_TEST_PORT must be an integer from 1024 to 65535.");
const baseURL = `http://127.0.0.1:${port}${base === "/" ? "" : base}/`;

export default defineConfig({
  testDir: "./tests/e2e",
  timeout: 90_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  reporter: process.env.CI ? [["line"], ["github"], ["html", { open: "never" }]] : "list",
  use: {
    ...devices["Desktop Chrome"],
    baseURL,
    channel: process.env.PLAYWRIGHT_CHANNEL || (process.platform === "win32" ? "msedge" : undefined),
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  webServer: {
    command: `npm run preview -- --host 127.0.0.1 --port ${port} --outDir ${outputDirectory} --ignore-lock`,
    url: baseURL,
    reuseExistingServer: false,
    timeout: 60_000,
    env: { ASTRO_TELEMETRY_DISABLED: "1" },
  },
});
