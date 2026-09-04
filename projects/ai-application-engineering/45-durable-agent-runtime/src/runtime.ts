/**
 * The durable executor.
 *
 * A workflow is an ordinary async function. It calls `ctx.step`, `ctx.effect`,
 * `ctx.now`, `ctx.random` and `ctx.awaitSignal`, and it must not do anything else that
 * observes the outside world. Each of those calls consumes the next journal event if one
 * exists (replay) or performs the operation and appends an event (live).
 *
 * The whole design is one invariant:
 *
 *   The Nth call to a runtime operation in a replay must be the same operation, with the
 *   same name, as the Nth call in the original run.
 *
 * Everything else -- resumption, exactly-once, budgets, approval gates -- is a
 * consequence. When the invariant breaks, the run has *silently diverged*, and the only
 * safe response is to stop. That is why divergence is checked on every operation rather
 * than at the end: by the time the outputs disagree, the duplicate refund has been sent.
 */

import {
  type EffectState,
  type Journal,
  type JournalEvent,
  type Json,
} from './journal.ts';
import type { EffectHandlers, EffectRequest, UnknownPolicy } from './ledger.ts';

export class NondeterminismError extends Error {
  readonly expected: string;
  readonly actual: string;
  readonly seq: number;

  constructor(expected: string, actual: string, seq: number) {
    super(`nondeterministic replay at seq ${seq}: journal has ${expected}, workflow asked for ${actual}`);
    this.name = 'NondeterminismError';
    this.expected = expected;
    this.actual = actual;
    this.seq = seq;
  }
}

export class BudgetExceededError extends Error {
  readonly spentCents: number;
  readonly limitCents: number;

  constructor(spentCents: number, limitCents: number) {
    super(`budget exceeded: ${spentCents} > ${limitCents} cents`);
    this.name = 'BudgetExceededError';
    this.spentCents = spentCents;
    this.limitCents = limitCents;
  }
}

/** Thrown to unwind the stack when a run suspends waiting for an external signal. */
export class SuspendSignal extends Error {
  readonly waitingFor: string;

  constructor(waitingFor: string) {
    super(`suspended awaiting ${waitingFor}`);
    this.name = 'SuspendSignal';
    this.waitingFor = waitingFor;
  }
}

export class UnknownEffectError extends Error {
  readonly effectName: string;
  readonly idempotencyKey: string;

  constructor(effectName: string, idempotencyKey: string) {
    super(`effect ${effectName} (${idempotencyKey}) has an intent but no outcome; escalating`);
    this.name = 'UnknownEffectError';
    this.effectName = effectName;
    this.idempotencyKey = idempotencyKey;
  }
}

export interface RuntimeOptions {
  readonly runId: string;
  readonly journal: Journal;
  readonly handlers: EffectHandlers;
  /** Signals already delivered. A signal is a fact about the world, not a promise. */
  readonly signals?: Readonly<Record<string, Json>>;
  readonly budgetCents?: number;
  readonly unknownPolicy?: UnknownPolicy;
  /** Wall clock for live execution. Never consulted during replay. */
  readonly clock?: () => number;
  readonly random?: () => number;
}

export interface WorkflowContext {
  /** A deterministic unit of work. Its result is journalled and replayed verbatim. */
  step<T extends Json>(name: string, body: () => T | Promise<T>, costCents?: number): Promise<T>;
  /** A side effect on a remote system. Journalled as intent, then outcome. */
  effect(name: string, payload: Json): Promise<Json>;
  /** Suspends the run until the signal has been delivered. Survives process restarts. */
  awaitSignal(name: string): Promise<Json>;
  now(): number;
  random(): number;
  readonly runId: string;
  readonly isReplaying: boolean;
  readonly spentCents: number;
}

export type Workflow<I extends Json, O extends Json> = (
  ctx: WorkflowContext,
  input: I,
) => Promise<O>;

export type RunOutcome<O> =
  | { readonly status: 'completed'; readonly output: O; readonly spentCents: number; readonly journalLength: number }
  | { readonly status: 'suspended'; readonly waitingFor: string; readonly spentCents: number; readonly journalLength: number }
  | { readonly status: 'failed'; readonly error: Error; readonly spentCents: number; readonly journalLength: number };

/**
 * Executes a workflow against a journal, replaying whatever the journal already contains.
 */
export class DurableRuntime {
  private cursor = 0;
  private seq = 0;
  private spent = 0;
  private effectCounter = 0;
  private readonly journal: Journal;
  private readonly opts: RuntimeOptions;

  constructor(options: RuntimeOptions) {
    this.opts = options;
    this.journal = options.journal;
  }

  async run<I extends Json, O extends Json>(workflow: Workflow<I, O>, input: I): Promise<RunOutcome<O>> {
    this.cursor = 0;
    this.seq = this.journal.length;
    this.spent = 0;
    this.effectCounter = 0;

    // Re-accumulate spend from the journal before running. A resumed run that starts at
    // zero spend will happily exceed the budget it was already at the edge of, and a
    // resumed run that re-charges the budget looks like it doubled its cost. The journal
    // is the only source that gets this right, because it is the only thing that knows
    // what the previous attempt did.
    for (const event of this.journal.events()) {
      if (event.type === 'step.completed') this.spent += event.costCents;
    }

    const existing = this.journal.events();
    if (existing.length === 0) {
      this.appendEvent({ type: 'run.started', seq: this.seq, runId: this.opts.runId, input });
    } else {
      // A journal that already ends in a terminal event is a finished run. Executing the
      // workflow again would append a second `run.completed` and, worse, would re-run any
      // trailing operation. Starting a completed run is a normal thing for a recovery
      // loop to do -- a supervisor that cannot tell "done" from "crashed" will do it on
      // every sweep -- so it has to be free.
      const last = existing[existing.length - 1]!;
      if (last.type === 'run.completed') {
        return {
          status: 'completed',
          output: last.output as O,
          spentCents: this.spent,
          journalLength: this.journal.length,
        };
      }
      this.cursor = 1; // consume the recorded run.started
    }

    const ctx = this.makeContext();

    try {
      const output = await workflow(ctx, input);
      this.appendEvent({ type: 'run.completed', seq: this.seq, output });
      return { status: 'completed', output, spentCents: this.spent, journalLength: this.journal.length };
    } catch (error) {
      if (error instanceof SuspendSignal) {
        // Suspension is deliberately NOT journalled. It is a status, not a decision: the
        // workflow made no choice by being suspended, and appending an event for it
        // would grow the journal on every restart and put a record in the replay stream
        // that the workflow never asked for. Suspension is derivable -- the journal ends
        // with a `signal.awaited` that has no matching `signal.received`.
        return {
          status: 'suspended',
          waitingFor: error.waitingFor,
          spentCents: this.spent,
          journalLength: this.journal.length,
        };
      }
      if (error instanceof NondeterminismError) {
        this.appendEvent({
          type: 'nondeterminism.detected',
          seq: this.seq,
          expected: error.expected,
          actual: error.actual,
        });
      }
      return { status: 'failed', error: error as Error, spentCents: this.spent, journalLength: this.journal.length };
    }
  }

  private appendEvent(event: JournalEvent): void {
    this.journal.append(event);
    this.seq = this.journal.length;
    this.cursor = this.journal.length;
  }

  /** The next journalled event, if we are still replaying history. */
  private peek(): JournalEvent | undefined {
    return this.cursor < this.journal.events().length ? this.journal.events()[this.cursor] : undefined;
  }

  private makeContext(): WorkflowContext {
    const self = this;

    return {
      get runId() {
        return self.opts.runId;
      },
      get isReplaying() {
        return self.cursor < self.journal.events().length;
      },
      get spentCents() {
        return self.spent;
      },

      async step<T extends Json>(name: string, body: () => T | Promise<T>, costCents = 0): Promise<T> {
        const recorded = self.peek();
        if (recorded) {
          if (recorded.type !== 'step.completed' || recorded.name !== name) {
            throw new NondeterminismError(describe(recorded), `step:${name}`, self.cursor);
          }
          self.cursor++;
          // Cost is NOT re-charged. It is already in `spent` from the journal scan, and
          // charging it again is how a resumed run invents a budget overrun that never
          // happened.
          return recorded.result as T;
        }

        // Budget is checked before the work, not after, because the point of a budget is
        // to prevent spending rather than to report it.
        if (self.opts.budgetCents !== undefined && self.spent + costCents > self.opts.budgetCents) {
          throw new BudgetExceededError(self.spent + costCents, self.opts.budgetCents);
        }

        const result = await body();
        self.spent += costCents;
        self.appendEvent({ type: 'step.completed', seq: self.seq, name, result, costCents });
        return result;
      },

      async effect(name: string, payload: Json): Promise<Json> {
        // The idempotency key is derived from the run and the effect's *ordinal position*,
        // not generated. A generated key differs on every attempt, so the retry after a
        // crash presents a new key and the remote system does the work again. This single
        // line is the difference between one refund and two.
        const ordinal = self.effectCounter++;
        const idempotencyKey = `${self.opts.runId}:${name}:${ordinal}`;

        const intent = self.peek();
        if (intent) {
          if (intent.type !== 'effect.intent' || intent.name !== name) {
            throw new NondeterminismError(describe(intent), `effect:${name}`, self.cursor);
          }
          self.cursor++;

          const outcome = self.peek();
          if (outcome && outcome.type === 'effect.outcome' && outcome.name === name) {
            self.cursor++;
            if (outcome.state === 'succeeded') return outcome.response;
            throw new Error(`effect ${name} failed: ${JSON.stringify(outcome.response)}`);
          }

          // Intent with no outcome. The window. We do not know whether the refund was
          // issued, and no amount of local reasoning will tell us.
          return self.resolveUnknown(name, intent.idempotencyKey, payload);
        }

        return self.performEffect(name, idempotencyKey, payload);
      },

      async awaitSignal(name: string): Promise<Json> {
        const recorded = self.peek();
        if (recorded) {
          if (recorded.type !== 'signal.awaited' || recorded.name !== name) {
            throw new NondeterminismError(describe(recorded), `signal:${name}`, self.cursor);
          }
          self.cursor++;
          const received = self.peek();
          if (received && received.type === 'signal.received' && received.name === name) {
            self.cursor++;
            return received.payload;
          }

          // Awaited on a previous attempt and still at the end of the journal. If the
          // signal has arrived since, record it now and carry on -- this is the resume
          // path, and getting it wrong means an approved refund suspends forever.
          const late = self.opts.signals?.[name];
          if (late !== undefined) {
            self.appendEvent({ type: 'signal.received', seq: self.seq, name, payload: late });
            return late;
          }
          throw new SuspendSignal(name);
        }

        self.appendEvent({ type: 'signal.awaited', seq: self.seq, name });
        const delivered = self.opts.signals?.[name];
        if (delivered === undefined) {
          // Suspension is an unwind, not a blocked promise. A promise that never settles
          // holds the approval gate in memory, and memory does not survive a deploy --
          // which is precisely when a two-day approval wait gets lost.
          throw new SuspendSignal(name);
        }
        self.appendEvent({ type: 'signal.received', seq: self.seq, name, payload: delivered });
        return delivered;
      },

      now(): number {
        const recorded = self.peek();
        if (recorded) {
          if (recorded.type !== 'clock.read') {
            throw new NondeterminismError(describe(recorded), 'clock', self.cursor);
          }
          self.cursor++;
          return recorded.millis;
        }
        const millis = (self.opts.clock ?? Date.now)();
        self.appendEvent({ type: 'clock.read', seq: self.seq, millis });
        return millis;
      },

      random(): number {
        const recorded = self.peek();
        if (recorded) {
          if (recorded.type !== 'random.read') {
            throw new NondeterminismError(describe(recorded), 'random', self.cursor);
          }
          self.cursor++;
          return recorded.value;
        }
        const value = (self.opts.random ?? Math.random)();
        self.appendEvent({ type: 'random.read', seq: self.seq, value });
        return value;
      },
    };
  }

  private performEffect(name: string, idempotencyKey: string, payload: Json): Json {
    const handler = this.opts.handlers[name];
    if (!handler) throw new Error(`no handler registered for effect '${name}'`);

    const request: EffectRequest = { name, idempotencyKey, payload };

    // Intent first. If the process dies between this line and the outcome append, the
    // journal says "we were about to do this and we do not know if it happened" -- which
    // is strictly more useful than the alternative ordering, where it says nothing and
    // the retry is unconditional.
    this.appendEvent({ type: 'effect.intent', seq: this.seq, name, idempotencyKey, request: payload });

    let state: EffectState;
    let response: Json;
    try {
      const result = handler(request);
      state = result.ok ? 'succeeded' : 'failed';
      response = result.response;
    } catch (error) {
      state = 'failed';
      response = { error: String(error) };
    }

    this.appendEvent({ type: 'effect.outcome', seq: this.seq, name, idempotencyKey, state, response });
    if (state === 'failed') throw new Error(`effect ${name} failed: ${JSON.stringify(response)}`);
    return response;
  }

  private resolveUnknown(name: string, recordedKey: string, payload: Json): Json {
    const policy = this.opts.unknownPolicy ?? 'retry-same-key';

    switch (policy) {
      case 'escalate':
        throw new UnknownEffectError(name, recordedKey);

      case 'retry-new-key': {
        // Deliberately wrong, kept because measuring it is the argument. A fresh key
        // guarantees the remote system treats this as a new request.
        const freshKey = `${recordedKey}:retry-${this.journal.length}`;
        return this.performEffect(name, freshKey, payload);
      }

      case 'retry-same-key':
      default:
        // Correct iff the remote system deduplicates on the key. That is a property of
        // *their* implementation, not ours, and it is the assumption most durable
        // execution documentation leaves unstated.
        return this.performEffect(name, recordedKey, payload);
    }
  }
}

function describe(event: JournalEvent): string {
  switch (event.type) {
    case 'step.completed':
      return `step:${event.name}`;
    case 'effect.intent':
    case 'effect.outcome':
      return `effect:${event.name}`;
    case 'signal.awaited':
    case 'signal.received':
      return `signal:${event.name}`;
    case 'clock.read':
      return 'clock';
    case 'random.read':
      return 'random';
    default:
      return event.type;
  }
}
