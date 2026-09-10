export function normalizeBase(value = "/Paul-Professional-Projects") {
  if (!/^\/[a-zA-Z0-9/_-]*$/.test(value) || value.includes("//")) {
    throw new Error("SITE_BASE_PATH must be an absolute path containing only letters, numbers, /, _ or -.");
  }
  return value === "/" ? "/" : value.replace(/\/+$/, "");
}

export function resolveSite(environment = process.env) {
  const url = new URL(environment.SITE_URL || "https://pauxru.github.io");
  if (url.protocol !== "https:" || url.username || url.password || url.search || url.hash || url.pathname !== "/") {
    throw new Error("SITE_URL must be an HTTPS origin, without a path, credentials, query or fragment.");
  }
  const base = normalizeBase(environment.SITE_BASE_PATH ?? "/Paul-Professional-Projects");
  return { site: url.origin, base, home: new URL(`${base === "/" ? "" : base}/`, url.origin).href };
}

export function withBase(path, base) {
  if (typeof path !== "string" || !path.startsWith("/") || path.startsWith("//") || path.includes("\\")) {
    throw new Error("Internal paths must start with a single forward slash.");
  }
  return `${normalizeBase(base) === "/" ? "" : normalizeBase(base)}${path}`;
}
