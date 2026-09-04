import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { InMemoryJournal, type Json } from '../src/journal.ts';
import { RefundGateway, type EffectHandlers } from '../src/ledger.ts';
import {
  DurableRuntime,
  UnknownEffectError,
  type RuntimeOptions,
  type Workflow,
} from '../src/runtime.ts';

function withGateway(honours = true) {
  const gateway = new RefundGateway(honours);
  const handlers: EffectHandlers = { refund: (r) => gateway.call(r) };
  return { gateway, handlers };
}

function rt(opts: Partial<RuntimeOptions> & { handlers: EffectHandlers }): DurableRuntime {
  return new DurableRuntime({
    runId: 'r1',
    journal: new InMemoryJournal(),
    ...opts,
  });
}

const oneRefund: Workflow<Json, Json> = async (ctx) => ({
  receipt: await ctx.effect('refund', { cents: 500 }),
});

describe('effects', () => {
  it('an effect calls the handler once', async () => {
    const { gateway, handlers } = withGateway();
    await rt({ handlers }).run(oneRefund, {});
    assert.equal(gateway.attempts.length, 1);
  });

  it('an effect moves money once', async () => {
    const { gateway, handlers } = withGateway();
    await rt({ handlers }).run(oneRefund, {});
    assert.equal(gateway.materialEffects, 1);
  });

  it('the effect result is returned to the workflow', async () => {
    const { handlers } = withGateway();
    const outcome = await rt({ handlers }).run(oneRefund, {});
    const receipt = (outcome as { output: { receipt: { confirmation: string } } }).output.receipt;
    assert.equal(receipt.confirmation, 'cnf-1');
  });

  it('an effect journals intent then outcome', async () => {
    const { handlers } = withGateway();
    const journal = new InMemoryJournal();
    await rt({ handlers, journal }).run(oneRefund, {});
    assert.deepEqual(
      journal.events().map((e) => e.type),
      ['run.started', 'effect.intent', 'effect.outcome', 'run.completed'],
    );
  });

  it('an unregistered effect fails the run', async () => {
    const outcome = await rt({ handlers: {} }).run(oneRefund, {});
    assert.equal(outcome.status, 'failed');
  });

  it('replaying a journalled effect does not call the handler again', async () => {
    const { gateway, handlers } = withGateway();
    const journal = new InMemoryJournal();
    await rt({ handlers, journal }).run(oneRefund, {});
    await rt({ handlers, journal: InMemoryJournal.parse(journal.serialise()) }).run(oneRefund, {});
    assert.equal(gateway.attempts.length, 1);
  });
});

describe('idempotency keys', () => {
  it('the key is derived from the run, the effect name and its position', async () => {
    const { gateway, handlers } = withGateway();
    await rt({ handlers, runId: 'run-abc' }).run(oneRefund, {});
    assert.equal(gateway.attempts[0]!.idempotencyKey, 'run-abc:refund:0');
  });

  it('the same crash point produces the same key on retry', async () => {
    const { gateway, handlers } = withGateway();
    const journal = new InMemoryJournal();
    await rt({ handlers, journal }).run(oneRefund, {});
    const intentOnly = InMemoryJournal.parse(journal.serialise().split('\n').slice(0, 2).join('\n'));
    await rt({ handlers, journal: intentOnly }).run(oneRefund, {});
    assert.equal(gateway.attempts.length, 2);
    assert.equal(gateway.attempts[0]!.idempotencyKey, gateway.attempts[1]!.idempotencyKey);
  });

  it('two calls in the same run get different keys', async () => {
    const { gateway, handlers } = withGateway();
    await rt({ handlers }).run(async (ctx) => {
      await ctx.effect('refund', { cents: 1 });
      await ctx.effect('refund', { cents: 2 });
      return {};
    }, {});
    assert.notEqual(gateway.attempts[0]!.idempotencyKey, gateway.attempts[1]!.idempotencyKey);
    assert.equal(gateway.materialEffects, 2, 'two deliberate refunds are two refunds');
  });

  it('different runs get different keys', async () => {
    const { gateway, handlers } = withGateway();
    await rt({ handlers, runId: 'a' }).run(oneRefund, {});
    await rt({ handlers, runId: 'b' }).run(oneRefund, {});
    assert.equal(gateway.materialEffects, 2);
  });

  it('a deduplicating gateway collapses a retried key to one movement', async () => {
    const { gateway, handlers } = withGateway(true);
    const journal = new InMemoryJournal();
    await rt({ handlers, journal }).run(oneRefund, {});
    const intentOnly = InMemoryJournal.parse(journal.serialise().split('\n').slice(0, 2).join('\n'));
    await rt({ handlers, journal: intentOnly }).run(oneRefund, {});
    assert.equal(gateway.attempts.length, 2, 'the runtime did retry');
    assert.equal(gateway.materialEffects, 1, 'the server did not move money twice');
  });

  it('a gateway that ignores keys issues a second refund for the same retry', async () => {
    const { gateway, handlers } = withGateway(false);
    const journal = new InMemoryJournal();
    await rt({ handlers, journal }).run(oneRefund, {});
    const intentOnly = InMemoryJournal.parse(journal.serialise().split('\n').slice(0, 2).join('\n'));
    await rt({ handlers, journal: intentOnly }).run(oneRefund, {});
    assert.equal(
      gateway.materialEffects,
      2,
      'exactly-once is a property of the pair, not of the runtime',
    );
  });

  it('the workflow cannot supply its own key', async () => {
    const { gateway, handlers } = withGateway();
    await rt({ handlers }).run(async (ctx) => {
      await ctx.effect('refund', { cents: 1, idempotencyKey: 'attacker-chosen' });
      return {};
    }, {});
    assert.equal(gateway.attempts[0]!.idempotencyKey, 'r1:refund:0');
  });
});

describe('the unknown window', () => {
  /** Journal truncated between intent and outcome: the runtime does not know what happened. */
  async function torn() {
    const { gateway, handlers } = withGateway();
    const journal = new InMemoryJournal();
    await rt({ handlers, journal }).run(oneRefund, {});
    const before = gateway.materialEffects;
    const partial = InMemoryJournal.parse(journal.serialise().split('\n').slice(0, 2).join('\n'));
    return { gateway, handlers, partial, before };
  }

  it('retry-same-key completes the run', async () => {
    const { handlers, partial } = await torn();
    const outcome = await rt({ handlers, journal: partial, unknownPolicy: 'retry-same-key' }).run(
      oneRefund,
      {},
    );
    assert.equal(outcome.status, 'completed');
  });

  it('retry-same-key does not double-refund against a deduplicating gateway', async () => {
    const { gateway, handlers, partial } = await torn();
    await rt({ handlers, journal: partial, unknownPolicy: 'retry-same-key' }).run(oneRefund, {});
    assert.equal(gateway.materialEffects, 1);
  });

  it('retry-new-key double-refunds even against a deduplicating gateway', async () => {
    const { gateway, handlers, partial } = await torn();
    await rt({ handlers, journal: partial, unknownPolicy: 'retry-new-key' }).run(oneRefund, {});
    assert.equal(gateway.materialEffects, 2);
  });

  it('escalate refuses to guess and stops the run', async () => {
    const { handlers, partial } = await torn();
    const outcome = await rt({ handlers, journal: partial, unknownPolicy: 'escalate' }).run(
      oneRefund,
      {},
    );
    assert.equal(outcome.status, 'failed');
    assert.ok((outcome as { error: Error }).error instanceof UnknownEffectError);
  });

  it('escalate moves no money', async () => {
    const { gateway, handlers, partial } = await torn();
    await rt({ handlers, journal: partial, unknownPolicy: 'escalate' }).run(oneRefund, {});
    assert.equal(gateway.materialEffects, 1, 'the first attempt still happened; the retry did not');
  });

  it('the escalation names the effect a human has to check', async () => {
    const { handlers, partial } = await torn();
    const outcome = await rt({ handlers, journal: partial, unknownPolicy: 'escalate' }).run(
      oneRefund,
      {},
    );
    assert.match((outcome as { error: Error }).error.message, /refund/);
  });

  it('a crash after the outcome is written is not an unknown window at all', async () => {
    const { gateway, handlers } = withGateway();
    const journal = new InMemoryJournal();
    await rt({ handlers, journal }).run(oneRefund, {});
    const withOutcome = InMemoryJournal.parse(
      journal.serialise().split('\n').slice(0, 3).join('\n'),
    );
    const outcome = await rt({ handlers, journal: withOutcome, unknownPolicy: 'escalate' }).run(
      oneRefund,
      {},
    );
    assert.equal(outcome.status, 'completed', 'escalate must not fire when the answer is recorded');
    assert.equal(gateway.attempts.length, 1);
  });

  it('a crash before the intent is written is not an unknown window either', async () => {
    const { gateway, handlers } = withGateway();
    const journal = new InMemoryJournal();
    await rt({ handlers, journal }).run(oneRefund, {});
    const startOnly = InMemoryJournal.parse(journal.serialise().split('\n').slice(0, 1).join('\n'));
    const outcome = await rt({ handlers, journal: startOnly, unknownPolicy: 'escalate' }).run(
      oneRefund,
      {},
    );
    assert.equal(outcome.status, 'completed');
    assert.equal(gateway.materialEffects, 1, 'the effect had not started, so starting it is safe');
  });
});

describe('gateway failures', () => {
  it('a gateway error fails the run rather than silently succeeding', async () => {
    const { gateway, handlers } = withGateway();
    gateway.failNext('r1:refund:0');
    const outcome = await rt({ handlers }).run(oneRefund, {});
    assert.equal(outcome.status, 'failed');
  });

  it('the failure is journalled, so a resume can see it happened', async () => {
    const { gateway, handlers } = withGateway();
    gateway.failNext('r1:refund:0');
    const journal = new InMemoryJournal();
    await rt({ handlers, journal }).run(oneRefund, {});
    assert.ok(journal.events().some((e) => e.type === 'effect.outcome'));
  });

  it('resuming after a recorded failure does not re-call the gateway', async () => {
    const { gateway, handlers } = withGateway();
    gateway.failNext('r1:refund:0');
    const journal = new InMemoryJournal();
    await rt({ handlers, journal }).run(oneRefund, {});
    const attempts = gateway.attempts.length;
    await rt({ handlers, journal: InMemoryJournal.parse(journal.serialise()) }).run(oneRefund, {});
    assert.equal(gateway.attempts.length, attempts, 'a recorded failure is a recorded decision');
  });
});
