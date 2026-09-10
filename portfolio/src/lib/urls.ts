import { withBase } from "../../config/site.mjs";

export function pathTo(path: string): string {
  return withBase(path, import.meta.env.BASE_URL);
}

export function absoluteUrl(path: string, site: URL): string {
  return new URL(pathTo(path), site).href;
}
