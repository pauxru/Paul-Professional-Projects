import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { InMemoryJournal, type Json } from '../src/journal.ts';
import { RefundGateway, type EffectHandlers } from '../src/ledger.ts';
import {
  BudgetExceededError,
  DurableRuntime,
  NondeterminismError,
  type RuntimeOptions,
  type Workflow,
  type WorkflowContext,
} from '../src/runtime.ts';

const noHandlers: EffectHandlers = {};

function rt(opts: Partial<RuntimeOptions> = {}): DurableRuntime {
  return new DurableRuntime({
    runId: 'r1',
    journal: new InMemoryJournal(),
    handlers: noHandlers,
    ...opts,
  });
}

/** Runs `wf` to completion, crashing at nothing, and returns the outcome and journal. */
async function run<O extends Json>(wf: Workflow<Json, Json>, opts: Partial<RuntimeOptions> = {}) {
  const journal = (opts.journal as InMemoryJournal | undefined) ?? new InMemoryJournal();
  const runtime = rt({ ...opts, journal });
  const outcome = await runtime.run(wf, {});
  return { outcome: outcome as { status: string; output?: O }, journal };
}

describe('steps', () => {
  it('completes a workflow with no steps', async () => {
    const { outcome } = await run(async () => ({ ok: true }));
    assert.equal(outcome.status, 'completed');
    assert.deepEqual(outcome.output, { ok: true });
  });

  it('returns the step body result', async () => {
    const { outcome } = await run(async (ctx) => ({ v: await ctx.step('a', () => 7) }));
    assert.deepEqual(outcome.output, { v: 7 });
  });

  it('journals one event per step plus start and completion', async () => {
    const { journal } = await run(async (ctx) => {
      await ctx.step('a', () => 1);
      await ctx.step('b', () => 2);
      return {};
    });
    assert.equal(journal.length, 4);
    assert.deepEqual(
      journal.events().map((e) => e.type),
      ['run.started', 'step.completed', 'step.completed', 'run.completed'],
    );
  });

  it('awaits an async step body', async () => {
    const { outcome } = await run(async (ctx) => ({
      v: await ctx.step('a', async () => {
        await Promise.resolve();
        return 9;
      }),
    }));
    assert.deepEqual(outcome.output, { v: 9 });
  });

  it('records the step name in the journal', async () => {
    const { journal } = await run(async (ctx) => {
      await ctx.step('charge-card', () => 1);
      return {};
    });
    const e = journal.events()[1] as { name?: string };
    assert.equal(e.name, 'charge-card');
  });

  it('a failing step fails the run rather than throwing out of run()', async () => {
    const { outcome } = await run(async (ctx) => {
      await ctx.step('a', () => {
        throw new Error('boom');
      });
      return {};
    });
    assert.equal(outcome.status, 'failed');
  });

  it('isReplaying is false on the first attempt', async () => {
    let seen: boolean | undefined;
    await run(async (ctx) => {
      await ctx.step('a', () => 1);
      seen = ctx.isReplaying;
      return {};
    });
    assert.equal(seen, false);
  });
});

describe('replay', () => {
  it('a step body does not execute on replay', async () => {
    let calls = 0;
    const wf: Workflow<Json, Json> = async (ctx) => ({ v: await ctx.step('a', () => ++calls) });

    const journal = new InMemoryJournal();
    await rt({ journal }).run(wf, {});
    assert.equal(calls, 1);

    const resumed = InMemoryJournal.parse(journal.serialise());
    await rt({ journal: resumed }).run(wf, {});
    assert.equal(calls, 1, 'the journalled result must be returned without calling the body');
  });

  it('replay returns the journalled value, not a recomputed one', async () => {
    let n = 100;
    const wf: Workflow<Json, Json> = async (ctx) => ({ v: await ctx.step('a', () => n++) });

    const journal = new InMemoryJournal();
    await rt({ journal }).run(wf, {});
    const second = await rt({ journal: InMemoryJournal.parse(journal.serialise()) }).run(wf, {});
    assert.deepEqual((second as { output: Json }).output, { v: 100 });
  });

  it('a journal ending in run.completed returns the output without executing anything', async () => {
    let calls = 0;
    const wf: Workflow<Json, Json> = async (ctx) => ({ v: await ctx.step('a', () => ++calls) });
    const journal = new InMemoryJournal();
    await rt({ journal }).run(wf, {});
    const before = calls;
    const again = await rt({ journal }).run(wf, {});
    assert.equal(calls, before);
    assert.equal(again.status, 'completed');
  });

  it('resuming from a truncated journal re-executes only the missing steps', async () => {
    const executed: string[] = [];
    const wf: Workflow<Json, Json> = async (ctx) => {
      for (const name of ['a', 'b', 'c', 'd']) {
        await ctx.step(name, () => {
          executed.push(name);
          return name;
        });
      }
      return {};
    };
    const journal = new InMemoryJournal();
    await rt({ journal }).run(wf, {});
    assert.deepEqual(executed, ['a', 'b', 'c', 'd']);

    executed.length = 0;
    const lines = journal.serialise().split('\n').slice(0, 3).join('\n');
    await rt({ journal: InMemoryJournal.parse(lines) }).run(wf, {});
    assert.deepEqual(executed, ['c', 'd']);
  });

  it('isReplaying is true while the journal still has events to consume', async () => {
    const flags: boolean[] = [];
    const wf: Workflow<Json, Json> = async (ctx) => {
      for (const name of ['a', 'b', 'c']) {
        // Read *before* the step: this is where a workflow would decide whether to emit a
        // log line or a metric. Read after the last replayed step the flag is already
        // false, because the cursor has reached the end of the journal.
        flags.push(ctx.isReplaying);
        await ctx.step(name, () => name);
      }
      return {};
    };
    const journal = new InMemoryJournal();
    await rt({ journal }).run(wf, {});

    flags.length = 0;
    const lines = journal.serialise().split('\n').slice(0, 3).join('\n');
    await rt({ journal: InMemoryJournal.parse(lines) }).run(wf, {});
    assert.deepEqual(flags, [true, true, false]);
  });

  it('the journal after a resumed run is identical to the crash-free one', async () => {
    const wf: Workflow<Json, Json> = async (ctx) => {
      for (const name of ['a', 'b', 'c']) await ctx.step(name, () => name);
      return {};
    };
    const clean = new InMemoryJournal();
    await rt({ journal: clean }).run(wf, {});

    const partial = InMemoryJournal.parse(clean.serialise().split('\n').slice(0, 2).join('\n'));
    await rt({ journal: partial }).run(wf, {});

    const strip = (j: InMemoryJournal) =>
      j.events().map((e) => ({ ...e, at: 0 }) as Record<string, unknown>);
    assert.deepEqual(strip(partial), strip(clean));
  });
});

describe('nondeterminism guard', () => {
  it('a renamed step is caught', async () => {
    const journal = new InMemoryJournal();
    await rt({ journal }).run(async (ctx) => {
      await ctx.step('a', () => 1);
      return {};
    }, {});

    const resumed = InMemoryJournal.parse(journal.serialise().split('\n').slice(0, 2).join('\n'));
    const outcome = await rt({ journal: resumed }).run(async (ctx) => {
      await ctx.step('RENAMED', () => 1);
      return {};
    }, {});
    assert.equal(outcome.status, 'failed');
    assert.ok((outcome as { error: Error }).error instanceof NondeterminismError);
  });

  it('a step where the journal expects an effect is caught', async () => {
    const gateway = new RefundGateway(true);
    const handlers: EffectHandlers = { refund: (r) => gateway.refund(r) };
    const journal = new InMemoryJournal();
    await rt({ journal, handlers }).run(async (ctx) => {
      await ctx.effect('refund', { cents: 1 });
      return {};
    }, {});

    const resumed = InMemoryJournal.parse(journal.serialise().split('\n').slice(0, 2).join('\n'));
    const outcome = await rt({ journal: resumed, handlers }).run(async (ctx) => {
      await ctx.step('refund', () => 1);
      return {};
    }, {});
    assert.equal(outcome.status, 'failed');
    assert.ok((outcome as { error: Error }).error instanceof NondeterminismError);
  });

  it('the error names the expected and actual step', async () => {
    const journal = new InMemoryJournal();
    await rt({ journal }).run(async (ctx) => {
      await ctx.step('expected-name', () => 1);
      return {};
    }, {});
    const resumed = InMemoryJournal.parse(journal.serialise().split('\n').slice(0, 2).join('\n'));
    const outcome = await rt({ journal: resumed }).run(async (ctx) => {
      await ctx.step('actual-name', () => 1);
      return {};
    }, {});
    const msg = (outcome as { error: Error }).error.message;
    assert.match(msg, /expected-name/);
    assert.match(msg, /actual-name/);
  });

  it('extra steps appended after the journal are allowed', async () => {
    const wf1: Workflow<Json, Json> = async (ctx) => {
      await ctx.step('a', () => 1);
      return {};
    };
    const wf2: Workflow<Json, Json> = async (ctx) => {
      await ctx.step('a', () => 1);
      await ctx.step('b', () => 2);
      return {};
    };
    const journal = new InMemoryJournal();
    await rt({ journal }).run(wf1, {});
    const resumed = InMemoryJournal.parse(journal.serialise().split('\n').slice(0, 2).join('\n'));
    assert.equal((await rt({ journal: resumed }).run(wf2, {})).status, 'completed');
  });

  it('an identical replay is not flagged', async () => {
    const wf: Workflow<Json, Json> = async (ctx) => {
      for (const n of ['a', 'b', 'c']) await ctx.step(n, () => n);
      return {};
    };
    const journal = new InMemoryJournal();
    await rt({ journal }).run(wf, {});
    const resumed = InMemoryJournal.parse(journal.serialise().split('\n').slice(0, 3).join('\n'));
    assert.equal((await rt({ journal: resumed }).run(wf, {})).status, 'completed');
  });
});

describe('now and random', () => {
  it('ctx.now is journalled and replays to the same value', async () => {
    const wf: Workflow<Json, Json> = async (ctx) => ({ t: ctx.now() });
    const journal = new InMemoryJournal();
    const first = await rt({ journal, clock: () => 1000 }).run(wf, {});
    const resumed = InMemoryJournal.parse(journal.serialise().split('\n').slice(0, 2).join('\n'));
    const second = await rt({ journal: resumed, clock: () => 999999 }).run(wf, {});
    assert.deepEqual((second as { output: Json }).output, (first as { output: Json }).output);
  });

  it('ctx.random is journalled and replays to the same value', async () => {
    const wf: Workflow<Json, Json> = async (ctx) => ({ r: ctx.random() });
    const journal = new InMemoryJournal();
    const first = await rt({ journal, random: () => 0.25 }).run(wf, {});
    const resumed = InMemoryJournal.parse(journal.serialise().split('\n').slice(0, 2).join('\n'));
    const second = await rt({ journal: resumed, random: () => 0.75 }).run(wf, {});
    assert.deepEqual((second as { output: Json }).output, (first as { output: Json }).output);
  });

  it('two calls to ctx.now in one run may differ but both replay exactly', async () => {
    let t = 0;
    const wf: Workflow<Json, Json> = async (ctx) => ({ a: ctx.now(), b: ctx.now() });
    const journal = new InMemoryJournal();
    const first = await rt({ journal, clock: () => (t += 5) }).run(wf, {});
    const resumed = InMemoryJournal.parse(journal.serialise().split('\n').slice(0, 3).join('\n'));
    const second = await rt({ journal: resumed, clock: () => 10_000 }).run(wf, {});
    assert.deepEqual((second as { output: Json }).output, (first as { output: Json }).output);
    assert.deepEqual((first as { output: Json }).output, { a: 5, b: 10 });
  });
});

describe('budget', () => {
  it('a run under the limit completes', async () => {
    const wf: Workflow<Json, Json> = async (ctx) => {
      await ctx.step('a', () => 1, 40);
      return {};
    };
    assert.equal((await rt({ budgetCents: 100 }).run(wf, {})).status, 'completed');
  });

  it('a run over the limit fails with BudgetExceededError', async () => {
    const wf: Workflow<Json, Json> = async (ctx) => {
      await ctx.step('a', () => 1, 60);
      await ctx.step('b', () => 1, 60);
      return {};
    };
    const outcome = await rt({ budgetCents: 100 }).run(wf, {});
    assert.equal(outcome.status, 'failed');
    assert.ok((outcome as { error: Error }).error instanceof BudgetExceededError);
  });

  it('spend is reported on the outcome', async () => {
    const wf: Workflow<Json, Json> = async (ctx) => {
      await ctx.step('a', () => 1, 30);
      await ctx.step('b', () => 1, 20);
      return {};
    };
    assert.equal((await rt().run(wf, {})).spentCents, 50);
  });

  it('a resumed run does not re-charge journalled steps', async () => {
    const wf: Workflow<Json, Json> = async (ctx) => {
      for (const n of ['a', 'b', 'c']) await ctx.step(n, () => n, 50);
      return {};
    };
    const journal = new InMemoryJournal();
    const clean = await rt({ journal }).run(wf, {});

    const partial = InMemoryJournal.parse(journal.serialise().split('\n').slice(0, 3).join('\n'));
    const resumed = await rt({ journal: partial }).run(wf, {});
    assert.equal(resumed.spentCents, clean.spentCents);
  });

  it('the budget is re-accumulated from the journal, not reset to zero', async () => {
    const wf: Workflow<Json, Json> = async (ctx) => {
      for (const n of ['a', 'b', 'c', 'd']) await ctx.step(n, () => n, 30);
      return {};
    };
    const journal = new InMemoryJournal();
    await rt({ journal, budgetCents: 1000 }).run(wf, {});
    const partial = InMemoryJournal.parse(journal.serialise().split('\n').slice(0, 3).join('\n'));

    // 4 x 30 = 120. Resuming with a 100 limit must still trip, because 90 was already spent.
    const outcome = await rt({ journal: partial, budgetCents: 100 }).run(wf, {});
    assert.equal(outcome.status, 'failed');
    assert.ok((outcome as { error: Error }).error instanceof BudgetExceededError);
  });

  it('ctx.spentCents is visible to the workflow', async () => {
    let seen = -1;
    await rt().run(async (ctx: WorkflowContext) => {
      await ctx.step('a', () => 1, 25);
      seen = ctx.spentCents;
      return {};
    }, {});
    assert.equal(seen, 25);
  });

  it('steps default to zero cost', async () => {
    const outcome = await rt().run(async (ctx) => {
      await ctx.step('a', () => 1);
      return {};
    }, {});
    assert.equal(outcome.spentCents, 0);
  });
});
