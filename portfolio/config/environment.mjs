import { existsSync } from "node:fs";
import { loadEnvFile } from "node:process";
import { fileURLToPath } from "node:url";

export function loadLocalEnvironment() {
  const path = fileURLToPath(new URL("../.env", import.meta.url));
  if (existsSync(path)) loadEnvFile(path);
}
