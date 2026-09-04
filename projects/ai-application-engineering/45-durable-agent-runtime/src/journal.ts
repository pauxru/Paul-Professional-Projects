/**
 * The journal: an append-only log of everything that happened, and the only thing that
 * survives a crash.
 *
 * The central design claim of durable execution is that a workflow's state does not need
 * to be checkpointed, because it can be *reconstructed* by re-running the workflow
 * function against a log of the decisions it previously made. That works exactly as far
 * as the workflow is deterministic given the log, and not one step further -- which is
 * why `NondeterminismDetected` is an event type here rather than an exception somewhere
 * else. A replay that silently diverges is much worse than one that fails, and the only
 * way to tell them apart is to record what the original run decided and compare.
 */

/** A step's result, as recorded. Values must survive JSON. */
export type Json =
  | null
  | boolean
  | number
  | string
  | Json[]
  | { [key: string]: Json };

/**
 * Effects move through three states, and the middle one is the entire problem.
 *
 * `intent` is written before the side effect is attempted; `outcome` after it returns.
 * A crash between them leaves an intent with no outcome, and on recovery the runtime
 * genuinely does not know whether the refund was issued. There is no protocol that
 * removes this window -- the journal write and the remote call cannot be made atomic --
 * so the honest engineering is to make the window small, make it visible, and give it a
 * defined resolution policy rather than pretending it does not exist.
 */
export type EffectState = 'intent' | 'succeeded' | 'failed';

export type JournalEvent =
  | { readonly type: 'run.started'; readonly seq: number; readonly runId: string; readonly input: Json }
  | { readonly type: 'step.completed'; readonly seq: number; readonly name: string; readonly result: Json; readonly costCents: number }
  | { readonly type: 'effect.intent'; readonly seq: number; readonly name: string; readonly idempotencyKey: string; readonly request: Json }
  | { readonly type: 'effect.outcome'; readonly seq: number; readonly name: string; readonly idempotencyKey: string; readonly state: EffectState; readonly response: Json }
  | { readonly type: 'clock.read'; readonly seq: number; readonly millis: number }
  | { readonly type: 'random.read'; readonly seq: number; readonly value: number }
  | { readonly type: 'signal.awaited'; readonly seq: number; readonly name: string }
  | { readonly type: 'signal.received'; readonly seq: number; readonly name: string; readonly payload: Json }
  | { readonly type: 'run.completed'; readonly seq: number; readonly output: Json }
  | { readonly type: 'run.failed'; readonly seq: number; readonly error: string }
  | { readonly type: 'nondeterminism.detected'; readonly seq: number; readonly expected: string; readonly actual: string };

/**
 * Storage for one run's events.
 *
 * `append` is where a real implementation would fsync. The `CrashError` thrown by
 * {@link CrashingJournal} models the two distinct failure points around that fsync,
 * because "crashed before the write landed" and "crashed after the write landed" resume
 * into different states and only one of them is interesting.
 */
export interface Journal {
  append(event: JournalEvent): void;
  events(): readonly JournalEvent[];
  readonly length: number;
}

export class InMemoryJournal implements Journal {
  private readonly log: JournalEvent[] = [];

  append(event: JournalEvent): void {
    if (event.seq !== this.log.length) {
      // A sequence gap means two writers, or a lost write. Either way the log can no
      // longer be replayed, and continuing would produce a run whose history is fiction.
      throw new Error(`journal sequence gap: expected ${this.log.length}, got ${event.seq}`);
    }
    this.log.push(event);
  }

  events(): readonly JournalEvent[] {
    return this.log;
  }

  get length(): number {
    return this.log.length;
  }

  /** Serialise for durability. One JSON document per line, so a torn tail is detectable. */
  serialise(): string {
    return this.log.map((e) => JSON.stringify(e)).join('\n');
  }

  /**
   * Read a journal back, discarding a torn final line.
   *
   * Discarding rather than failing is deliberate: a partial write is exactly what a crash
   * during append looks like, and a runtime that refuses to start after a crash has
   * achieved nothing. The discarded event is one the workflow will simply redo.
   */
  static parse(text: string): InMemoryJournal {
    const journal = new InMemoryJournal();
    for (const line of text.split('\n')) {
      if (line.trim() === '') continue;
      try {
        journal.log.push(JSON.parse(line) as JournalEvent);
      } catch {
        break;
      }
    }
    return journal;
  }
}

/**
 * Thrown by the crash injector. Distinguished from workflow errors so tests cannot
 * confuse them.
 *
 * Written with an explicit field rather than a constructor parameter property, because
 * this project runs under Node's strip-only TypeScript: parameter properties emit code,
 * and stripping cannot emit. See docs/adr/003-no-build-step.md.
 */
export class CrashError extends Error {
  readonly atSequence: number;

  constructor(atSequence: number) {
    super(`crash injected before journal write ${atSequence}`);
    this.name = 'CrashError';
    this.atSequence = atSequence;
  }
}

/**
 * A journal that fails on the Nth append, used to enumerate every crash point in a run.
 *
 * The crash happens *before* the event is recorded, which models a process dying between
 * doing something and writing it down -- the ordering that produces duplicate side
 * effects. Crashing after the write is the easy case and is covered by simply resuming
 * from a complete journal.
 */
export class CrashingJournal implements Journal {
  private readonly inner: InMemoryJournal;
  private readonly crashAt: number;

  constructor(inner: InMemoryJournal, crashAt: number) {
    this.inner = inner;
    this.crashAt = crashAt;
  }

  append(event: JournalEvent): void {
    if (this.inner.length === this.crashAt) {
      throw new CrashError(this.crashAt);
    }
    this.inner.append(event);
  }

  events(): readonly JournalEvent[] {
    return this.inner.events();
  }

  get length(): number {
    return this.inner.length;
  }
}
