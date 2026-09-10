import { defineConfig } from "astro/config";
import sitemap from "@astrojs/sitemap";
import { resolveSite } from "./config/site.mjs";
import { loadLocalEnvironment } from "./config/environment.mjs";

loadLocalEnvironment();
const deployment = resolveSite();

export default defineConfig({
  site: deployment.site,
  base: deployment.base,
  trailingSlash: "always",
  output: "static",
  integrations: [sitemap()],
  build: { inlineStylesheets: "never" },
  server: { host: "127.0.0.1", port: 4321 },
  vite: {
    server: { strictPort: true },
    preview: { strictPort: true },
  },
});
