/**
 * The workflows under test.
 *
 * `refundWorkflow` is the one from the story: a 40-step agent run that decides whether to
 * refund a customer, asks a human, and then moves money. It is written the way an agent
 * workflow is actually written -- a retrieval phase, a reasoning loop, a tool call, an
 * approval gate, a side effect -- because a durable runtime that only works on workflows
 * written to suit it has not solved the problem.
 *
 * `NONDETERMINISM_CORPUS` is the more uncomfortable half. Each entry is a workflow that
 * looks completely ordinary and breaks replay. They are not contrived; every one of them
 * is a pattern I have written on purpose in non-durable code.
 */

import type { Json } from './journal.ts';
import type { Workflow, WorkflowContext } from './runtime.ts';

export interface RefundInput extends Record<string, Json> {
  readonly ticketId: string;
  readonly amountCents: number;
  readonly documents: string[];
}

export interface RefundOutput extends Record<string, Json> {
  readonly decision: string;
  readonly confirmation: Json;
  readonly evidenceCount: number;
}

/**
 * Forty steps: 1 classify, 12 retrieve, 12 score, 8 reason, 1 policy check, 1 approval,
 * 1 effect, and the bookkeeping around them. Costs are in cents and are deliberately
 * uneven -- the reasoning steps are where the money goes, which is why a naive re-run
 * from step 0 is expensive as well as dangerous.
 */
export const refundWorkflow: Workflow<RefundInput, RefundOutput> = async (ctx, input) => {
  const classified = await ctx.step('classify-intent', () => ({ intent: 'refund', confidence: 0.94 }), 2);

  const evidence: string[] = [];
  for (let i = 0; i < 12; i++) {
    const doc = await ctx.step(`retrieve-${i}`, () => input.documents[i % input.documents.length] ?? `doc-${i}`, 1);
    evidence.push(doc);
  }

  const scores: number[] = [];
  for (let i = 0; i < 12; i++) {
    const score = await ctx.step(`score-${i}`, () => Number(((i * 7919) % 100) / 100), 1);
    scores.push(score);
  }

  // The reasoning loop. Each iteration is an LLM call in the real thing, hence the cost.
  let rationale = '';
  for (let i = 0; i < 8; i++) {
    rationale = await ctx.step(
      `reason-${i}`,
      () => `${rationale}|hop${i}:${scores[i]?.toFixed(2) ?? '0.00'}`,
      25,
    );
  }

  const decidedAt = ctx.now();
  const eligible = await ctx.step(
    'policy-check',
    () => input.amountCents <= 50_00 && (classified as { confidence: number }).confidence > 0.8,
    3,
  );

  if (!eligible) {
    return { decision: 'declined', confirmation: null, evidenceCount: evidence.length };
  }

  // The approval gate. On a fresh run with no signal delivered this suspends the run --
  // it does not block a promise -- so a deploy during the two days a human takes to
  // answer does not lose the run.
  const approval = await ctx.awaitSignal('human-approval');

  const confirmation = await ctx.effect('issue-refund', {
    ticketId: input.ticketId,
    amountCents: input.amountCents,
    approvedBy: (approval as { by?: string }).by ?? 'unknown',
    decidedAt,
  });

  await ctx.step('notify-customer', () => ({ sent: true }), 1);

  return {
    decision: 'refunded',
    confirmation,
    evidenceCount: evidence.length,
  };
};

/** Total steps the refund workflow performs, excluding the effect and the signal. */
export const REFUND_STEP_COUNT = 1 + 12 + 12 + 8 + 1 + 1;

// ---------------------------------------------------------------------------
// The nondeterminism corpus.

/**
 * What should happen when this entry is replayed.
 *
 * - `detected`   the guard fires immediately; the loud, safe failure.
 * - `latent`     genuinely nondeterministic, but invisible to an in-process replay --
 *                it only appears once the wall clock or the RNG has moved on, which is
 *                to say, only in production.
 * - `silent`     replay completes with a different answer. No guard can see this,
 *                because the sequence of operations is identical.
 * - `prevented`  the runtime's API makes the mistake impossible to express.
 * - `clean`      the control group.
 */
export type ExpectedOutcome = 'detected' | 'latent' | 'silent' | 'prevented' | 'clean';

export interface CorpusEntry {
  readonly name: string;
  /** Why an experienced engineer would write this without noticing. */
  readonly why: string;
  readonly expected: ExpectedOutcome;
  readonly workflow: Workflow<Json, Json>;
}

/** A hidden mutable used by the "module-level cache" entry. Reset before each run. */
let moduleCache: string[] = [];
export function resetCorpusState(): void {
  moduleCache = [];
}

export const NONDETERMINISM_CORPUS: readonly CorpusEntry[] = [
  {
    name: 'branch-on-wall-clock',
    why: 'A timeout check. `Date.now()` outside ctx.now() reads a different value on replay, so the branch flips and the workflow calls a different step.',
    expected: 'detected',
    workflow: async (ctx) => {
      await ctx.step('start', () => 1);
      // Nondeterministic: the real clock, not the journalled one.
      const slow = Date.now() % 2 === 0;
      if (slow) await ctx.step('slow-path', () => 'slow');
      else await ctx.step('fast-path', () => 'fast');
      return { done: true };
    },
  },
  {
    name: 'branch-on-math-random',
    why: 'Sampling, A/B routing, jitter. Any of them changes the step sequence between attempts.',
    expected: 'detected',
    workflow: async (ctx) => {
      await ctx.step('start', () => 1);
      if (Math.random() < 0.5) await ctx.step('variant-a', () => 'a');
      else await ctx.step('variant-b', () => 'b');
      return { done: true };
    },
  },
  {
    name: 'set-iteration-order',
    why: 'Deduplicating tool names with a Set and iterating it. Insertion order is stable in JS, but the *insertion* here depends on a Map built from an object whose key order came from JSON parsing of a response that was re-fetched.',
    expected: 'detected',
    workflow: async (ctx) => {
      const names = new Set<string>();
      names.add(Math.random() < 0.5 ? 'alpha' : 'beta');
      names.add('gamma');
      for (const n of names) await ctx.step(`tool-${n}`, () => n);
      return { done: true };
    },
  },
  {
    name: 'module-level-cache',
    why: 'A memoisation cache outside the workflow. On the first attempt it is cold and the workflow does the work; on replay in the same process it is warm and the workflow skips a step.',
    expected: 'detected',
    workflow: async (ctx) => {
      if (moduleCache.length === 0) {
        const v = await ctx.step('populate-cache', () => 'value');
        moduleCache.push(v as string);
      }
      await ctx.step('use-cache', () => moduleCache[0]!);
      return { done: true };
    },
  },
  {
    name: 'uuid-as-idempotency-key',
    why: 'The single most common way to get two refunds: generate the key inside the step, so every attempt presents a different one. The runtime derives keys positionally, so this is caught -- but only because the runtime refuses to let the workflow supply the key at all.',
    expected: 'detected',
    workflow: async (ctx) => {
      // The workflow *wants* to do this, and the API does not let it.
      const key = await ctx.step('make-key', () => `${Math.random()}`);
      await ctx.step(`use-${(key as string).slice(0, 4)}`, () => 'used');
      return { done: true };
    },
  },
  {
    name: 'unordered-parallel-completion',
    why: 'Promise.race or an unordered Promise.all consumer: whichever settles first drives the next step, and that ordering is not stable.',
    expected: 'detected',
    workflow: async (ctx) => {
      const first = await Promise.race([
        Promise.resolve('a'),
        new Promise<string>((r) => setImmediate(() => r('b'))),
      ]);
      await ctx.step(`raced-${first}`, () => first);
      return { done: true };
    },
  },
  {
    name: 'exception-message-in-step-name',
    why: 'Naming a step after an error string. The error text includes a socket number, a hostname or a timestamp, so the name differs on replay.',
    expected: 'detected',
    workflow: async (ctx) => {
      const detail = `socket-${Math.floor(Math.random() * 1000)}`;
      await ctx.step(`retry-after-${detail}`, () => detail);
      return { done: true };
    },
  },
  {
    name: 'clock-read-not-branched-on',
    why: 'Reads the wall clock and puts it in the *output* without branching on it. The step sequence is identical, so no guard fires -- and the run completes with a different answer than the original. This one is silent.',
    expected: 'silent',
    workflow: async (ctx) => {
      const v = await ctx.step('work', () => 'constant');
      return { done: true, at: Date.now(), v: v as Json };
    },
  },
  {
    name: 'float-accumulation-order',
    why: 'Summing scores in an order derived from a Map. The sequence of steps is identical and only the *value* differs, in the last few bits. Nothing catches it.',
    expected: 'silent',
    workflow: async (ctx) => {
      const parts = Math.random() < 0.5 ? [0.1, 0.2, 0.3] : [0.3, 0.2, 0.1];
      const total = await ctx.step('sum', () => parts.reduce((a, b) => a + b, 0));
      return { total: total as Json };
    },
  },
  {
    name: 'deterministic-control',
    why: 'The control group. If this one is ever reported as nondeterministic, the detector is broken and every other number here is noise.',
    expected: 'clean',
    workflow: async (ctx) => {
      for (let i = 0; i < 4; i++) await ctx.step(`s${i}`, () => i);
      return { done: true };
    },
  },
];

/** A workflow that spends more than any sensible budget, for the budget experiments. */
export const expensiveWorkflow: Workflow<Json, Json> = async (ctx: WorkflowContext) => {
  for (let i = 0; i < 20; i++) {
    await ctx.step(`burn-${i}`, () => i, 10);
  }
  return { done: true };
};
