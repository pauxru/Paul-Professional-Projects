import type { APIRoute } from "astro";
import { absoluteUrl } from "../lib/urls";

export const GET: APIRoute = ({ site }) => {
  if (!site) throw new Error("A configured site URL is required for robots.txt.");
  return new Response(`User-agent: *\nAllow: /\n\nSitemap: ${absoluteUrl("/sitemap-index.xml", site)}\n`, {
    headers: { "Content-Type": "text/plain; charset=utf-8" },
  });
};
