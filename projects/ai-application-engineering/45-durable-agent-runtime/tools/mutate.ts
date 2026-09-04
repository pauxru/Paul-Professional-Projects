/**
 * Mutation testing without a mutation framework.
 *
 * Each mutant is a single textual edit to a source file that changes a load-bearing
 * decision into a plausible alternative -- the kind of thing a reviewer would wave
 * through. The suite must fail for every one. A mutant that survives means the tests
 * describe the code's shape rather than its behaviour, and the number of tests is
 * decoration.
 *
 * The edits are applied to a copy of the tree, not to the working tree, so an
 * interrupted run cannot leave mutated source behind.
 */
import { spawnSync } from 'node:child_process';
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

interface Mutant {
  readonly name: string;
  readonly file: string;
  readonly from: string;
  readonly to: string;
  /** The behaviour that should notice. Printed when a mutant survives. */
  readonly guards: string;
}

const MUTANTS: readonly Mutant[] = [
  {
    name: 'journal accepts a sequence gap',
    file: 'src/journal.ts',
    from: 'if (event.seq !== this.log.length) {',
    to: 'if (false) {',
    guards: 'two writers, or a lost write, must not be silently accepted',
  },
  {
    name: 'parse skips a torn line instead of stopping',
    file: 'src/journal.ts',
    from: '      } catch {\n        break;\n      }',
    to: '      } catch {\n        continue;\n      }',
    guards: 'resuming past a hole replays a history that never happened',
  },
  {
    name: 'idempotency key is generated fresh each attempt',
    file: 'src/runtime.ts',
    from: 'const idempotencyKey = `${self.opts.runId}:${name}:${ordinal}`;',
    to: 'const idempotencyKey = `${self.opts.runId}:${name}:${ordinal}:${Math.random()}`;',
    guards: 'a positional key is the only thing that makes a retry the same request',
  },
  {
    name: 'idempotency key drops the ordinal',
    file: 'src/runtime.ts',
    from: 'const idempotencyKey = `${self.opts.runId}:${name}:${ordinal}`;',
    to: 'const idempotencyKey = `${self.opts.runId}:${name}`;',
    guards: 'two deliberate refunds in one run must not collapse into one',
  },
  {
    name: 'budget is not re-accumulated from the journal',
    file: 'src/runtime.ts',
    from: "if (event.type === 'step.completed') this.spent += event.costCents;",
    to: "if (event.type === 'step.completed') this.spent += 0;",
    guards: 'a resumed run must not restart its budget at zero',
  },
  {
    name: 'the step-name guard is removed',
    file: 'src/runtime.ts',
    from: 'throw new NondeterminismError(describe(recorded), `step:${name}`, self.cursor);',
    to: 'void 0;',
    guards: 'a workflow whose steps moved must not replay against the old journal',
  },
  {
    name: 'a late signal is ignored on resume',
    file: 'src/runtime.ts',
    from: 'const late = self.opts.signals?.[name];',
    to: 'const late = undefined;',
    guards: 'an approved run must not suspend forever',
  },
  {
    name: 'suspension is journalled as an event',
    file: 'src/runtime.ts',
    from: "self.appendEvent({ type: 'signal.awaited', seq: self.seq, name });",
    to: "self.appendEvent({ type: 'signal.awaited', seq: self.seq, name });\n        self.appendEvent({ type: 'signal.awaited', seq: self.seq, name });",
    guards: 'a restart loop must not grow the journal',
  },
];

const root = process.cwd();
const results: { name: string; killed: boolean; guards: string }[] = [];

for (const m of MUTANTS) {
  const dir = mkdtempSync(join(tmpdir(), 'mut-45-'));
  try {
    cpSync(join(root, 'src'), join(dir, 'src'), { recursive: true });
    cpSync(join(root, 'tests'), join(dir, 'tests'), { recursive: true });

    const path = join(dir, m.file);
    // Normalise line endings before matching: the anchors are written with \n and the
    // working tree on Windows is CRLF, which would silently make every multi-line mutant
    // "not applicable" -- a mutation harness that quietly stops mutating is worse than
    // no mutation harness.
    const original = readFileSync(path, 'utf8').replace(/\r\n/g, '\n');
    if (!original.includes(m.from)) {
      console.error(`  MUTANT NOT APPLICABLE: ${m.name} -- anchor text not found in ${m.file}`);
      results.push({ name: m.name, killed: false, guards: 'anchor text not found' });
      continue;
    }
    writeFileSync(path, original.replace(m.from, m.to), 'utf8');

    const run = spawnSync(process.execPath, ['--test', 'tests/*.test.ts'], {
      cwd: dir,
      encoding: 'utf8',
      timeout: 180_000,
    });
    // A mutant is only killed if the suite *ran* and failed. Node exits non-zero for a
    // module-resolution error too, and an earlier version of this harness reported 8/8
    // killed while every single run had died before executing a test.
    const ran = /^(ℹ|#) tests \d+/m.test(run.stdout ?? '');
    if (!ran) {
      console.error(`  HARNESS ERROR: ${m.name} -- the suite did not run`);
      console.error((run.stdout ?? '').split('\n').slice(0, 12).join('\n'));
      results.push({ name: m.name, killed: false, guards: 'the suite did not run at all' });
      continue;
    }
    const killed = run.status !== 0;
    results.push({ name: m.name, killed, guards: m.guards });
    console.log(`  ${killed ? 'killed  ' : 'SURVIVED'}  ${m.name}`);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

const survivors = results.filter((r) => !r.killed);
console.log('');
console.log(`${results.length - survivors.length}/${results.length} mutants killed`);
for (const s of survivors) {
  console.error(`  SURVIVED: ${s.name}`);
  console.error(`            nothing checks that ${s.guards}`);
}
process.exit(survivors.length === 0 ? 0 : 1);
