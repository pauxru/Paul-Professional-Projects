/**
 * The measurements.
 *
 * Every function here answers exactly one question and returns data, never prose, so the
 * same call produces the number in the report and the number the tests assert.
 *
 * The headline experiment is {@link crashSweep}: run the workflow, crash before *every*
 * journal write in turn, recover, and count how many times money moved. It is exhaustive
 * rather than sampled, which matters -- the interesting crash points are a small fraction
 * of the total and a random sample finds them late or not at all.
 */

import { CrashError, InMemoryJournal, type Journal, type Json } from './journal.ts';
import { RefundGateway, type EffectHandlers, type UnknownPolicy } from './ledger.ts';
import {
  BudgetExceededError,
  DurableRuntime,
  NondeterminismError,
  UnknownEffectError,
  type RunOutcome,
  type Workflow,
} from './runtime.ts';
import {
  NONDETERMINISM_CORPUS,
  REFUND_STEP_COUNT,
  expensiveWorkflow,
  refundWorkflow,
  resetCorpusState,
  type RefundInput,
} from './workflows.ts';

const INPUT: RefundInput = {
  ticketId: 'TCK-40219',
  amountCents: 3400,
  documents: ['policy.md', 'invoice.pdf', 'chat-log.txt'],
};

const APPROVAL = { by: 'a.mensah', at: '2026-04-02T09:14:00Z' } as const;

const FIXED_CLOCK = () => 1_775_000_000_000;
const FIXED_RANDOM = () => 0.42;

function handlersFor(gateway: RefundGateway): EffectHandlers {
  return { 'issue-refund': (request) => gateway.call(request) };
}

/**
 * Run to completion with no crashes. The control: every other number is a comparison
 * against this.
 */
export async function baselineRun(): Promise<{
  outcome: RunOutcome<Json>;
  journalLength: number;
  effects: number;
  spentCents: number;
}> {
  const gateway = new RefundGateway(true);
  const journal = new InMemoryJournal();

  // First attempt suspends at the approval gate.
  await new DurableRuntime({
    runId: 'baseline',
    journal,
    handlers: handlersFor(gateway),
    clock: FIXED_CLOCK,
    random: FIXED_RANDOM,
  }).run(refundWorkflow, INPUT);

  // The human answers; the run resumes from the journal.
  const outcome = await new DurableRuntime({
    runId: 'baseline',
    journal,
    handlers: handlersFor(gateway),
    signals: { 'human-approval': APPROVAL },
    clock: FIXED_CLOCK,
    random: FIXED_RANDOM,
  }).run(refundWorkflow, INPUT);

  return {
    outcome: outcome as RunOutcome<Json>,
    journalLength: journal.length,
    effects: gateway.materialEffects,
    spentCents: outcome.spentCents,
  };
}

export interface CrashPointResult {
  readonly crashAt: number;
  readonly recovered: boolean;
  readonly materialEffects: number;
  readonly gatewayAttempts: number;
  readonly finalSpentCents: number;
  /** True if the crash landed between an effect intent and its outcome. */
  readonly inUnknownWindow: boolean;
}

export interface CrashSweepResult {
  readonly policy: UnknownPolicy;
  readonly serverDeduplicates: boolean;
  readonly crashPoints: number;
  readonly recovered: number;
  readonly duplicateEffects: number;
  readonly lostEffects: number;
  readonly escalated: number;
  readonly unknownWindowPoints: number;
  readonly results: readonly CrashPointResult[];
}

/**
 * Crash before every journal write in turn; recover; count the refunds.
 *
 * The recovery loop is the interesting part. A crashed run is resumed by constructing a
 * fresh runtime over the surviving journal -- there is no in-memory state carried across,
 * because in a real deployment the process is gone. Resumption repeats until the run
 * completes, fails, or stops making progress.
 */
export async function crashSweep(
  policy: UnknownPolicy = 'retry-same-key',
  serverDeduplicates = true,
): Promise<CrashSweepResult> {
  const baseline = await baselineRun();
  const total = baseline.journalLength;
  const results: CrashPointResult[] = [];

  for (let crashAt = 0; crashAt < total; crashAt++) {
    results.push(await runWithCrashAt(crashAt, policy, serverDeduplicates));
  }

  return {
    policy,
    serverDeduplicates,
    crashPoints: total,
    recovered: results.filter((r) => r.recovered).length,
    duplicateEffects: results.filter((r) => r.materialEffects > 1).length,
    lostEffects: results.filter((r) => r.recovered && r.materialEffects === 0).length,
    escalated: results.filter((r) => !r.recovered && r.inUnknownWindow).length,
    unknownWindowPoints: results.filter((r) => r.inUnknownWindow).length,
    results,
  };
}

async function runWithCrashAt(
  crashAt: number,
  policy: UnknownPolicy,
  serverDeduplicates: boolean,
): Promise<CrashPointResult> {
  const gateway = new RefundGateway(serverDeduplicates);
  const journal = new InMemoryJournal();
  let inUnknownWindow = false;

  // The approval is delivered from the start. The crash sweep is measuring what happens
  // around the *side effect*, and a run that suspends at the gate never reaches it --
  // which is how an earlier version of this experiment reported a zero-width unknown
  // window and made the whole design look easier than it is. The gate's own behaviour is
  // measured separately by approvalGateExperiment.
  await attempt(journal, gateway, crashAt, policy, true);

  // Did the crash land in the window? Check the surviving journal, not a flag: this is
  // the same evidence a recovering process would have.
  inUnknownWindow = hasDanglingIntent(journal);

  // Recovery attempts. Bounded so a workflow that cannot make progress fails the sweep
  // rather than hanging it.
  let recovered = false;
  let lastLength = -1;
  for (let i = 0; i < 6 && journal.length !== lastLength; i++) {
    lastLength = journal.length;
    const outcome = await attempt(journal, gateway, -1, policy, true);
    if (outcome?.status === 'completed') {
      recovered = true;
      break;
    }
    if (outcome?.status === 'failed' && outcome.error instanceof UnknownEffectError) break;
  }

  return {
    crashAt,
    recovered,
    materialEffects: gateway.materialEffects,
    gatewayAttempts: gateway.attempts.length,
    finalSpentCents: totalSpend(journal),
    inUnknownWindow,
  };
}

async function attempt(
  journal: InMemoryJournal,
  gateway: RefundGateway,
  crashAt: number,
  policy: UnknownPolicy,
  withApproval: boolean,
): Promise<RunOutcome<Json> | undefined> {
  const target: Journal =
    crashAt >= 0
      ? {
          append: (e) => {
            if (journal.length === crashAt) throw new CrashError(crashAt);
            journal.append(e);
          },
          events: () => journal.events(),
          get length() {
            return journal.length;
          },
        }
      : journal;

  const runtime = new DurableRuntime({
    runId: 'sweep',
    journal: target,
    handlers: handlersFor(gateway),
    signals: withApproval ? { 'human-approval': APPROVAL } : undefined,
    unknownPolicy: policy,
    clock: FIXED_CLOCK,
    random: FIXED_RANDOM,
  });

  try {
    return (await runtime.run(refundWorkflow, INPUT)) as RunOutcome<Json>;
  } catch (error) {
    if (error instanceof CrashError) return undefined;
    throw error;
  }
}

function hasDanglingIntent(journal: InMemoryJournal): boolean {
  const events = journal.events();
  for (let i = 0; i < events.length; i++) {
    const e = events[i]!;
    if (e.type !== 'effect.intent') continue;
    const next = events[i + 1];
    if (!next || next.type !== 'effect.outcome' || next.idempotencyKey !== e.idempotencyKey) {
      return true;
    }
  }
  return false;
}

function totalSpend(journal: InMemoryJournal): number {
  return journal
    .events()
    .reduce((sum, e) => (e.type === 'step.completed' ? sum + e.costCents : sum), 0);
}

// ---------------------------------------------------------------------------

export interface NondeterminismResult {
  readonly name: string;
  readonly why: string;
  readonly expected: string;
  /** The guard fired when replayed immediately, in the same process. */
  readonly detectedInProcess: boolean;
  /** The guard fired when replayed after the world moved on. */
  readonly detectedAfterWorldMoved: boolean;
  /** Replay completed but produced a different answer. No guard can see this. */
  readonly silentlyDiverged: boolean;
  /**
   * True when the entry is genuinely nondeterministic but invisible to an immediate
   * in-process replay. This is the dangerous class: a test suite reports it as clean.
   */
  readonly latent: boolean;
}

/**
 * For each corpus entry, replay it twice: once immediately, and once after moving the
 * world on.
 *
 * The two modes are the finding. A test suite replays microseconds after the original
 * run, in the same process, with the same wall clock reading and the same RNG state --
 * so a workflow that branches on `Date.now()` takes the same branch both times and the
 * suite reports it clean. Production replays after a crash, a restart and possibly an
 * hour of queueing. The "world moved" mode patches `Date.now` and `Math.random` between
 * the two runs to model that, which is not a trick: it is the only way to observe in a
 * test the condition that recovery actually happens under.
 */
export async function nondeterminismSweep(): Promise<readonly NondeterminismResult[]> {
  const out: NondeterminismResult[] = [];

  for (const entry of NONDETERMINISM_CORPUS) {
    const immediate = await probe(entry, false);
    const moved = await probe(entry, true);

    out.push({
      name: entry.name,
      why: entry.why,
      expected: entry.expected,
      detectedInProcess: immediate.detected,
      detectedAfterWorldMoved: moved.detected,
      silentlyDiverged: immediate.diverged || moved.diverged,
      latent: !immediate.detected && !immediate.diverged && (moved.detected || moved.diverged),
    });
  }

  return out;
}

async function probe(
  entry: CorpusEntry,
  moveTheWorld: boolean,
): Promise<{ detected: boolean; diverged: boolean }> {
  let detected = false;
  let diverged = false;

  const realNow = Date.now;
  const realRandom = Math.random;

  // Repeat: several entries are only wrong when the coin lands differently, and one
  // trial would report them clean about half the time.
  for (let trial = 0; trial < 24 && !detected && !diverged; trial++) {
    resetCorpusState();
    const journal = new InMemoryJournal();

    try {
      if (moveTheWorld) {
        // The original run: clock at T, RNG returns 0.1.
        Date.now = () => 1_775_000_000_000;
        Math.random = () => 0.1;
      } else {
        // An immediate in-process replay: same millisecond, both times. Freezing the
        // clock is not a convenience here, it is the whole point of the mode -- a test
        // suite that runs a workflow and replays it on the next line genuinely does read
        // the same millisecond, and that is why it misses this class of bug. Leaving the
        // real clock in place would make the result depend on how busy the machine is,
        // which is the flakiness this experiment exists to explain.
        Date.now = () => 1_775_000_000_000;
      }

      const first = await new DurableRuntime({
        runId: `nd-${entry.name}`,
        journal,
        handlers: {},
        clock: FIXED_CLOCK,
      }).run(entry.workflow as Workflow<Json, Json>, {} as Json);

      if (first.status !== 'completed') continue;

      if (moveTheWorld) {
        // Recovery, one hour later, in a new process. Both sources have moved.
        Date.now = () => 1_775_000_000_000 + 3_600_001;
        Math.random = () => 0.9;
      }

      // Replay against the journal with its terminal event removed -- which is exactly
      // what a crash immediately before the completion write leaves behind, and the only
      // state in which replay is asked to re-execute anything. A completed journal is
      // returned verbatim by the runtime and would test nothing.
      const truncated = InMemoryJournal.parse(
        journal
          .events()
          .slice(0, -1)
          .map((e) => JSON.stringify(e))
          .join('\n'),
      );

      const replay = await new DurableRuntime({
        runId: `nd-${entry.name}`,
        journal: truncated,
        handlers: {},
        clock: FIXED_CLOCK,
      }).run(entry.workflow as Workflow<Json, Json>, {} as Json);

      if (replay.status === 'failed' && replay.error instanceof NondeterminismError) {
        detected = true;
      } else if (replay.status === 'completed') {
        if (JSON.stringify(replay.output) !== JSON.stringify(first.output)) diverged = true;
      }
    } finally {
      Date.now = realNow;
      Math.random = realRandom;
    }
  }

  return { detected, diverged };
}

// ---------------------------------------------------------------------------

export interface BudgetResult {
  readonly naiveResumedSpend: number;
  readonly journalAwareResumedSpend: number;
  readonly singleRunSpend: number;
  readonly limitCents: number;
  readonly naiveWouldTrip: boolean;
  readonly journalAwareTrips: boolean;
}

/**
 * What a resumed run costs, and what a runtime that re-charges journalled steps
 * *thinks* it costs.
 *
 * The naive figure is computed by summing the costs the workflow declares over both
 * attempts -- which is what a budget counter reset to zero on resume would observe. It
 * is not a hypothetical: it is the default behaviour of every budget implementation that
 * lives outside the journal.
 */
export async function budgetExperiment(limitCents = 250): Promise<BudgetResult> {
  const journal = new InMemoryJournal();
  const runtime = () =>
    new DurableRuntime({
      runId: 'budget',
      journal,
      handlers: {},
      budgetCents: limitCents,
      clock: FIXED_CLOCK,
    });

  // Crash halfway.
  const partial = new InMemoryJournal();
  const crashing: Journal = {
    append: (e) => {
      if (partial.length === 12) throw new CrashError(12);
      partial.append(e);
    },
    events: () => partial.events(),
    get length() {
      return partial.length;
    },
  };
  try {
    await new DurableRuntime({
      runId: 'budget',
      journal: crashing,
      handlers: {},
      budgetCents: limitCents,
      clock: FIXED_CLOCK,
    }).run(expensiveWorkflow, {} as Json);
  } catch (error) {
    if (!(error instanceof CrashError)) throw error;
  }

  const spentBeforeCrash = partial
    .events()
    .reduce((s, e) => (e.type === 'step.completed' ? s + e.costCents : s), 0);

  const resumed = await new DurableRuntime({
    runId: 'budget',
    journal: partial,
    handlers: {},
    budgetCents: limitCents,
    clock: FIXED_CLOCK,
  }).run(expensiveWorkflow, {} as Json);

  // A clean single run, for the true cost.
  const clean = await runtime().run(expensiveWorkflow, {} as Json);

  return {
    naiveResumedSpend: spentBeforeCrash + 20 * 10,
    journalAwareResumedSpend: resumed.spentCents,
    singleRunSpend: clean.status === 'completed' ? clean.spentCents : clean.spentCents,
    limitCents,
    naiveWouldTrip: spentBeforeCrash + 20 * 10 > limitCents,
    journalAwareTrips: resumed.status === 'failed' && resumed.error instanceof BudgetExceededError,
  };
}

// ---------------------------------------------------------------------------

export interface ReplayCostResult {
  readonly journalEvents: number;
  readonly journalBytes: number;
  readonly bytesPerStep: number;
  readonly stepsReplayed: number;
  /** Work units re-executed on resume. Zero is the claim durable execution makes. */
  readonly workUnitsReExecuted: number;
  readonly costCentsAvoided: number;
}

/**
 * What resumption actually saves, and what the journal costs to buy it.
 *
 * `workUnitsReExecuted` is counted by instrumenting the step bodies: a replayed step
 * returns the journalled result *without calling the body*, so a body that runs during
 * replay is a bug and this counts it.
 */
export async function replayCost(): Promise<ReplayCostResult> {
  let bodyCalls = 0;
  const counting: Workflow<Json, Json> = async (ctx) => {
    for (let i = 0; i < REFUND_STEP_COUNT; i++) {
      await ctx.step(`s-${i}`, () => {
        bodyCalls++;
        return i;
      }, 5);
    }
    return { done: true };
  };

  const journal = new InMemoryJournal();
  await new DurableRuntime({ runId: 'cost', journal, handlers: {}, clock: FIXED_CLOCK }).run(
    counting,
    {} as Json,
  );
  const firstPass = bodyCalls;

  bodyCalls = 0;
  await new DurableRuntime({ runId: 'cost', journal, handlers: {}, clock: FIXED_CLOCK }).run(
    counting,
    {} as Json,
  );

  const bytes = journal.serialise().length;
  return {
    journalEvents: journal.length,
    journalBytes: bytes,
    bytesPerStep: Math.round(bytes / firstPass),
    stepsReplayed: firstPass,
    workUnitsReExecuted: bodyCalls,
    costCentsAvoided: firstPass * 5,
  };
}

// ---------------------------------------------------------------------------

export interface ApprovalGateResult {
  readonly suspendedCleanly: boolean;
  readonly journalLengthWhileWaiting: number;
  readonly survivedRestarts: number;
  readonly resumedAfterSignal: boolean;
  readonly stepsReExecutedAcrossRestarts: number;
}

/**
 * The approval gate, restarted repeatedly while the human thinks.
 *
 * The failure this is written against: an approval implemented as an unresolved promise
 * looks fine in every test, because the tests do not restart the process. A deploy during
 * the two days a human takes to answer loses the run, and the loss is silent.
 */
export async function approvalGateExperiment(restarts = 5): Promise<ApprovalGateResult> {
  const gateway = new RefundGateway(true);
  const journal = new InMemoryJournal();
  let bodyCallsAfterFirst = 0;

  const first = await new DurableRuntime({
    runId: 'approval',
    journal,
    handlers: handlersFor(gateway),
    clock: FIXED_CLOCK,
  }).run(refundWorkflow, INPUT);

  const lengthWhileWaiting = journal.length;
  let survived = 0;

  for (let i = 0; i < restarts; i++) {
    const before = journal.length;
    const outcome = await new DurableRuntime({
      runId: 'approval',
      journal,
      handlers: handlersFor(gateway),
      clock: FIXED_CLOCK,
    }).run(refundWorkflow, INPUT);
    // Each restart must suspend again and must not add work.
    if (outcome.status === 'suspended' && journal.length === before) survived++;
    bodyCallsAfterFirst += gateway.attempts.length;
  }

  const resumed = await new DurableRuntime({
    runId: 'approval',
    journal,
    handlers: handlersFor(gateway),
    signals: { 'human-approval': APPROVAL },
    clock: FIXED_CLOCK,
  }).run(refundWorkflow, INPUT);

  return {
    suspendedCleanly: first.status === 'suspended',
    journalLengthWhileWaiting: lengthWhileWaiting,
    survivedRestarts: survived,
    resumedAfterSignal: resumed.status === 'completed',
    stepsReExecutedAcrossRestarts: bodyCallsAfterFirst,
  };
}
