/**
 * The side-effect ledger and the world the effects act on.
 *
 * `RefundGateway` counts calls per idempotency key, which is the only measurement that
 * matters in this project: "exactly once" is a claim about how many times money moved,
 * and the way to check it is to count.
 */

import type { Json } from './journal.ts';

export interface EffectRequest {
  readonly name: string;
  readonly idempotencyKey: string;
  readonly payload: Json;
}

export interface EffectResult {
  readonly ok: boolean;
  readonly response: Json;
}

/**
 * A remote system that charges money.
 *
 * `attempts` counts every call that reached it, including duplicates. `distinctKeys`
 * counts how many times it actually acted. The gap between them is what an idempotency
 * key buys, and it is only a gap if the *server* deduplicates -- a client-side key that
 * the server ignores is decoration.
 */
export class RefundGateway {
  readonly attempts: EffectRequest[] = [];
  private readonly applied = new Map<string, Json>();

  /** Simulated failures, keyed by idempotency key, consumed on first use. */
  private readonly failOnce = new Set<string>();

  private readonly honoursIdempotency: boolean;

  /**
   * If `honoursIdempotency` is false the gateway ignores idempotency keys, which is the
   * behaviour of most internal services and of every API written before someone got
   * paged about it.
   */
  constructor(honoursIdempotency: boolean = true) {
    this.honoursIdempotency = honoursIdempotency;
  }

  failNext(idempotencyKey: string): void {
    this.failOnce.add(idempotencyKey);
  }

  call(request: EffectRequest): EffectResult {
    this.attempts.push(request);

    if (this.failOnce.has(request.idempotencyKey)) {
      this.failOnce.delete(request.idempotencyKey);
      return { ok: false, response: { error: 'gateway unavailable' } };
    }

    if (this.honoursIdempotency && this.applied.has(request.idempotencyKey)) {
      // The server replays its stored response. It does *not* move money again.
      return { ok: true, response: this.applied.get(request.idempotencyKey)! };
    }

    const response: Json = {
      confirmation: `cnf-${this.applied.size + 1}`,
      key: request.idempotencyKey,
    };
    this.applied.set(request.idempotencyKey, response);
    return { ok: true, response };
  }

  /** How many times money actually moved. This is the number under test. */
  get distinctEffects(): number {
    return this.applied.size;
  }

  /** How many times an effect *acted*, counting a non-deduplicating server honestly. */
  get materialEffects(): number {
    return this.honoursIdempotency ? this.applied.size : this.attempts.length;
  }

  reset(): void {
    this.attempts.length = 0;
    this.applied.clear();
    this.failOnce.clear();
  }
}

/** Anything the runtime is allowed to call. Keyed by effect name. */
export type EffectHandlers = Readonly<Record<string, (request: EffectRequest) => EffectResult>>;

/**
 * How to resolve an effect that has an intent but no outcome.
 *
 * These are the only three options and each one is wrong in a different way, which is
 * the point of measuring them rather than arguing about them.
 */
export type UnknownPolicy =
  /** Call again with the same key. Correct iff the remote system deduplicates. */
  | 'retry-same-key'
  /** Call again with a fresh key. Always safe to execute, always risks a double effect. */
  | 'retry-new-key'
  /** Refuse to proceed; escalate to a human. Correct, and stops the run. */
  | 'escalate';
