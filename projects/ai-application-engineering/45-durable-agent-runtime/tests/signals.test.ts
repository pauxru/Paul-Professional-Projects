import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { InMemoryJournal, type Json } from '../src/journal.ts';
import type { EffectHandlers } from '../src/ledger.ts';
import {
  DurableRuntime,
  NondeterminismError,
  type RuntimeOptions,
  type Workflow,
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

const gated: Workflow<Json, Json> = async (ctx) => {
  await ctx.step('prepare', () => 'ready');
  const decision = await ctx.awaitSignal('approval');
  await ctx.step('act', () => 'done');
  return { decision };
};

describe('approval gate', () => {
  it('suspends when the signal has not arrived', async () => {
    assert.equal((await rt().run(gated, {})).status, 'suspended');
  });

  it('names what it is waiting for', async () => {
    const outcome = await rt().run(gated, {});
    assert.equal((outcome as { waitingFor: string }).waitingFor, 'approval');
  });

  it('completes when the signal is already present', async () => {
    const outcome = await rt({ signals: { approval: { ok: true } } }).run(gated, {});
    assert.equal(outcome.status, 'completed');
    assert.deepEqual((outcome as { output: Json }).output, { decision: { ok: true } });
  });

  it('does not run the steps after the gate while suspended', async () => {
    let acted = 0;
    const wf: Workflow<Json, Json> = async (ctx) => {
      await ctx.awaitSignal('approval');
      await ctx.step('act', () => ++acted);
      return {};
    };
    await rt().run(wf, {});
    assert.equal(acted, 0);
  });

  it('journals signal.awaited but not a suspension event', async () => {
    const journal = new InMemoryJournal();
    await rt({ journal }).run(gated, {});
    const types = journal.events().map((e) => e.type);
    assert.ok(types.includes('signal.awaited'));
    assert.equal(types.at(-1), 'signal.awaited', 'suspension is a status, not a decision');
  });

  it('a restart with no signal suspends again without growing the journal', async () => {
    const journal = new InMemoryJournal();
    await rt({ journal }).run(gated, {});
    const size = journal.length;
    const resumed = InMemoryJournal.parse(journal.serialise());
    const outcome = await rt({ journal: resumed }).run(gated, {});
    assert.equal(outcome.status, 'suspended');
    assert.equal(resumed.length, size, 'a restart loop must not grow the journal');
  });

  it('survives ten restarts without growth or progress', async () => {
    let journal = new InMemoryJournal();
    await rt({ journal }).run(gated, {});
    const size = journal.length;
    for (let i = 0; i < 10; i++) {
      journal = InMemoryJournal.parse(journal.serialise());
      const outcome = await rt({ journal }).run(gated, {});
      assert.equal(outcome.status, 'suspended');
      assert.equal(journal.length, size);
    }
  });

  it('does not re-execute steps before the gate on restart', async () => {
    let prepared = 0;
    const wf: Workflow<Json, Json> = async (ctx) => {
      await ctx.step('prepare', () => ++prepared);
      await ctx.awaitSignal('approval');
      return {};
    };
    const journal = new InMemoryJournal();
    await rt({ journal }).run(wf, {});
    assert.equal(prepared, 1);
    for (let i = 0; i < 3; i++) {
      await rt({ journal: InMemoryJournal.parse(journal.serialise()) }).run(wf, {});
    }
    assert.equal(prepared, 1);
  });

  it('resumes and completes once the signal is delivered to a suspended journal', async () => {
    const journal = new InMemoryJournal();
    await rt({ journal }).run(gated, {});
    const resumed = InMemoryJournal.parse(journal.serialise());
    const outcome = await rt({ journal: resumed, signals: { approval: 'yes' } }).run(gated, {});
    assert.equal(outcome.status, 'completed', 'an approved run must not suspend forever');
    assert.deepEqual((outcome as { output: Json }).output, { decision: 'yes' });
  });

  it('the delivered signal is journalled so a later replay does not need it again', async () => {
    const journal = new InMemoryJournal();
    await rt({ journal }).run(gated, {});
    const resumed = InMemoryJournal.parse(journal.serialise());
    await rt({ journal: resumed, signals: { approval: 'yes' } }).run(gated, {});

    const final = InMemoryJournal.parse(resumed.serialise());
    const outcome = await rt({ journal: final }).run(gated, {});
    assert.equal(outcome.status, 'completed', 'the decision is recorded, not re-requested');
  });

  it('a denial is a signal like any other and lets the workflow branch', async () => {
    const wf: Workflow<Json, Json> = async (ctx) => {
      const d = (await ctx.awaitSignal('approval')) as { approved: boolean };
      return { took: d.approved ? 'refund' : 'reject' };
    };
    const outcome = await rt({ signals: { approval: { approved: false } } }).run(wf, {});
    assert.deepEqual((outcome as { output: Json }).output, { took: 'reject' });
  });

  it('waiting for a different signal on replay is caught', async () => {
    const journal = new InMemoryJournal();
    await rt({ journal }).run(gated, {});
    const resumed = InMemoryJournal.parse(journal.serialise());
    const changed: Workflow<Json, Json> = async (ctx) => {
      await ctx.step('prepare', () => 'ready');
      await ctx.awaitSignal('a-different-signal');
      return {};
    };
    const outcome = await rt({ journal: resumed, signals: { 'a-different-signal': 1 } }).run(
      changed,
      {},
    );
    assert.equal(outcome.status, 'failed');
    assert.ok((outcome as { error: Error }).error instanceof NondeterminismError);
  });

  it('two gates suspend at the first and then at the second', async () => {
    const wf: Workflow<Json, Json> = async (ctx) => {
      await ctx.awaitSignal('one');
      await ctx.awaitSignal('two');
      return {};
    };
    const journal = new InMemoryJournal();
    assert.equal((await rt({ journal }).run(wf, {})).status, 'suspended');

    const afterOne = InMemoryJournal.parse(journal.serialise());
    const second = await rt({ journal: afterOne, signals: { one: 1 } }).run(wf, {});
    assert.equal(second.status, 'suspended');
    assert.equal((second as { waitingFor: string }).waitingFor, 'two');

    const afterTwo = InMemoryJournal.parse(afterOne.serialise());
    const third = await rt({ journal: afterTwo, signals: { one: 1, two: 2 } }).run(wf, {});
    assert.equal(third.status, 'completed');
  });

  it('a signal arriving before the workflow asks for it is still consumed', async () => {
    const outcome = await rt({ signals: { approval: 'early' } }).run(gated, {});
    assert.equal(outcome.status, 'completed');
  });

  it('a suspended run reports the spend accumulated so far', async () => {
    const wf: Workflow<Json, Json> = async (ctx) => {
      await ctx.step('a', () => 1, 42);
      await ctx.awaitSignal('approval');
      return {};
    };
    assert.equal((await rt().run(wf, {})).spentCents, 42);
  });
});
