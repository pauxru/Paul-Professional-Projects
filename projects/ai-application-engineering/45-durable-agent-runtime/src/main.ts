import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildReport } from './report.ts';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const docs = join(root, 'docs');

await mkdir(docs, { recursive: true });

const started = Date.now();
const full = await buildReport(false);
const stable = await buildReport(true);

await writeFile(join(docs, 'results.md'), full, 'utf8');
await writeFile(join(docs, 'results-stable.md'), stable, 'utf8');

console.log(`wrote docs/results.md (${full.length} bytes)`);
console.log(`wrote docs/results-stable.md (${stable.length} bytes)`);
console.log(`elapsed ${((Date.now() - started) / 1000).toFixed(1)}s`);
