/**
 * The build step. Loads every module in dependency order and reports which ones parsed.
 *
 * This is what "compiling" means for this project: Node's strip-only TypeScript refuses
 * any construct that would require emitting code -- enums, parameter properties,
 * namespaces -- so a module that loads is a module whose types were erasable and whose
 * syntax was valid. See docs/adr/003-no-build-step.md.
 */
const modules = [
  './journal.ts',
  './ledger.ts',
  './runtime.ts',
  './workflows.ts',
  './experiments.ts',
  './predictions.ts',
  './report.ts',
];

let loaded = 0;
for (const m of modules) {
  await import(m);
  loaded++;
  console.log(`  loaded src/${m.slice(2)}`);
}
console.log(`${loaded} modules loaded`);
