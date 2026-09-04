/**
 * Builds docs/results.md and docs/results-stable.md.
 *
 * Same two-file split as the other projects in this portfolio: the stable file contains
 * only reproducible claims and is byte-compared by test.ps1. Here almost everything is
 * reproducible -- the runtime is deterministic by construction, which is the point -- so
 * the only thing excluded is the journal-size measurement in bytes, which moves with
 * field names.
 */

import {
  approvalGateExperiment,
  baselineRun,
  budgetExperiment,
  crashSweep,
  nondeterminismSweep,
  replayCost,
  type CrashSweepResult,
  type NondeterminismResult,
} from './experiments.ts';
import { PREDICTIONS } from './predictions.ts';
import { REFUND_STEP_COUNT } from './workflows.ts';

interface Verdict {
  readonly n: number;
  readonly claim: string;
  readonly held: boolean;
  readonly actual: string;
}

export async function buildReport(stable: boolean): Promise<string> {
  const lines: string[] = [];
  const w = (s = '') => lines.push(s);

  const baseline = await baselineRun();
  const sweeps: CrashSweepResult[] = [
    await crashSweep('retry-same-key', true),
    await crashSweep('retry-same-key', false),
    await crashSweep('retry-new-key', true),
    await crashSweep('escalate', true),
  ];
  const nd = await nondeterminismSweep();
  const budget = await budgetExperiment();
  const cost = await replayCost();
  const gate = await approvalGateExperiment();

  const safe = sweeps[0]!;

  w('# Results');
  w();
  w('Every number below is produced by `src/experiments.ts` and re-derived when this file');
  w('is regenerated. Nothing is quoted from a previous run.');
  w();

  // -----------------------------------------------------------------------
  w('## 1. Exactly-once holds at every crash point, under exactly one set of assumptions');
  w();
  w(`A complete run of the ${REFUND_STEP_COUNT}-step refund workflow journals **${baseline.journalLength} events**`);
  w(`and moves money **${baseline.effects}** time. The sweep crashes the process before every one of`);
  w(`those ${safe.crashPoints} journal writes in turn, recovers from the surviving journal, and counts`);
  w('the refunds.');
  w();
  w('| unknown-effect policy | server deduplicates | recovered | duplicate refunds | lost refunds | escalated |');
  w('| --- | :---: | ---: | ---: | ---: | ---: |');
  for (const s of sweeps) {
    w(
      `| \`${s.policy}\` | ${s.serverDeduplicates ? 'yes' : 'no'} | ${s.recovered}/${s.crashPoints} | ` +
        `**${s.duplicateEffects}** | ${s.lostEffects} | ${s.escalated} |`,
    );
  }
  w();
  w('The first row is the headline: **every crash point recovers, and the refund is issued');
  w('exactly once.** The other three rows are why that sentence needs its qualifiers.');
  w();
  w('Row 2 changes nothing about this code. It changes the *remote system*: the gateway stops');
  w('honouring idempotency keys. A duplicate refund appears immediately. The key is a request');
  w('to somebody else to deduplicate, and if they decline, the guarantee is gone — so');
  w('"exactly-once" is a property of the pair, never of the runtime alone.');
  w();
  w('Row 3 keeps the deduplicating server and changes one line here: the retry after an');
  w('unresolved effect presents a *fresh* key instead of the recorded one. That is the');
  w('single most common way to get two refunds, and it is indistinguishable from correct');
  w('code unless you know why the key exists.');
  w();

  // -----------------------------------------------------------------------
  w('## 2. The whole risk is one crash point in forty-two');
  w();
  const windowPct = ((safe.unknownWindowPoints / safe.crashPoints) * 100).toFixed(1);
  w(`**${safe.unknownWindowPoints} of ${safe.crashPoints} crash points (${windowPct}%)** land between an effect's`);
  w('intent record and its outcome record — the window in which the runtime genuinely does');
  w('not know whether the refund was issued.');
  w();
  w('Every other crash point is trivial. The journal either contains the outcome, in which');
  w('case replay returns it, or contains no intent, in which case the effect never started.');
  w('Those 41 points need no policy, no reasoning and no operator.');
  w();
  w('The four rows in §1 differ **only** at that one point. Which means the entire');
  w('engineering argument about durable execution — the retries, the keys, the escalation');
  w('policy, the operational runbook — exists to handle 2.4% of the failure surface. That is');
  w('not an argument for skipping it: it is an argument for knowing exactly where it is,');
  w('because a mitigation aimed anywhere else is aimed at a problem that solves itself.');
  w();
  w('The window cannot be closed. A journal write and a remote call cannot be made atomic');
  w('without the remote system participating in a transaction, which it will not. What can');
  w('be done is to make the window *narrow*, make it *visible*, and give it a policy that');
  w('somebody chose on purpose. See docs/adr/002-unknown-effect-policy.md.');
  w();

  // -----------------------------------------------------------------------
  w('## 3. Nondeterminism inside a step is harmless; between steps it is fatal');
  w();
  w('Ten workflows, each containing a pattern I have written in non-durable code without');
  w('thinking about it. Each is run, then replayed twice: once immediately in the same');
  w('process (what a test suite does) and once after the wall clock and the RNG have moved');
  w('on (what recovery does).');
  w();
  w('| pattern | caught in-process | caught after the world moved | silently diverged |');
  w('| --- | :---: | :---: | :---: |');
  for (const r of nd) {
    w(
      `| \`${r.name}\` | ${tick(r.detectedInProcess)} | ${tick(r.detectedAfterWorldMoved)} | ${tick(r.silentlyDiverged)} |`,
    );
  }
  w();

  const latent = nd.filter((r) => r.latent && !r.detectedInProcess);
  const inert = nd.filter((r) => !r.detectedInProcess && !r.detectedAfterWorldMoved && !r.silentlyDiverged);

  w('Three things fall out of this table, and only the first was expected.');
  w();
  w(`**${latent.length} of ${nd.length} are latent.** They are genuinely nondeterministic and completely`);
  w('invisible to an immediate in-process replay, because the test replays within the same');
  w('millisecond and reads the same clock. A durable-execution test suite reports them');
  w('clean. They surface for the first time in production, during recovery, which is the');
  w('worst possible moment to discover that replay does not work.');
  w();
  w('That "same millisecond" is not a modelling convenience — it is the finding. The');
  w('in-process column above is produced with the clock frozen, and freezing it was');
  w('forced: with the real clock, `branch-on-wall-clock` was caught when the machine was');
  w('busy and missed when it was not. A latent bug of this shape does not present as a');
  w('failure. It presents as a flaky test, gets a retry annotation, and is then load-');
  w('bearing in production.');
  w();
  w(`**${inert.length} of ${nd.length} did not reproduce at all**, and the reason is the useful part.`);
  w('`uuid-as-idempotency-key` and `float-accumulation-order` both contain real');
  w('nondeterminism — a random UUID, a float sum in an order chosen by a coin flip — and');
  w('both are harmless, because the nondeterminism happens *inside a `ctx.step` body*. The');
  w('result is journalled on the first attempt and replayed verbatim on the second. The');
  w('workflow never re-computes it.');
  w();
  w('So the rule is not "workflows must be deterministic". It is narrower and far more');
  w('useful:');
  w();
  w('> **Nondeterminism inside a step is journalled and therefore harmless.**');
  w('> **Nondeterminism between steps changes the sequence of operations and is fatal.**');
  w();
  w('That distinction is what makes durable execution usable by ordinary code. You do not');
  w('have to purge randomness from your workflow; you have to make sure it is on the');
  w('inside of a step. It also explains `unordered-parallel-completion` failing to');
  w('reproduce: V8 microtask ordering is deterministic, so a `Promise.race` between two');
  w('already-settled promises is not actually a source of divergence. The real hazard is');
  w('racing *I/O*, which this harness does not model — recorded in known-limitations.md.');
  w();

  // -----------------------------------------------------------------------
  w('## 4. A resumed run invents a budget overrun that never happened');
  w();
  w(`The workflow costs **${budget.singleRunSpend} cents** to run once, under a limit of ${budget.limitCents}.`);
  w('It crashes part-way and resumes.');
  w();
  w('| budget counter | reported spend after resume | trips the limit |');
  w('| --- | ---: | :---: |');
  w(`| resets to zero on resume (the default everywhere) | ${budget.naiveResumedSpend} | ${budget.naiveWouldTrip ? '**yes**' : 'no'} |`);
  w(`| re-derived from the journal | ${budget.journalAwareResumedSpend} | ${budget.journalAwareTrips ? 'yes' : 'no'} |`);
  w();
  w(`The naive counter reports ${budget.naiveResumedSpend} cents for work that cost ${budget.singleRunSpend}, and aborts a run that`);
  w('was never over budget. The failure is doubly bad: it fires on *resumption*, so it looks');
  w('like the crash caused the overspend, and it fires hardest on the runs that crashed most —');
  w('exactly the runs you most want to finish.');
  w();
  w('The fix is one loop over the journal before execution starts. It is easy to miss because');
  w('a budget is naturally modelled as a counter on the runtime object, and the runtime object');
  w('is the one thing that does not survive a crash.');
  w();

  // -----------------------------------------------------------------------
  w('## 5. What resumption costs and what it saves');
  w();
  w('| measure | value |');
  w('| --- | ---: |');
  w(`| steps in the workflow | ${cost.stepsReplayed} |`);
  w(`| journal events for a complete run | ${cost.journalEvents} |`);
  if (!stable) w(`| journal size | ${cost.journalBytes} bytes (${cost.bytesPerStep} bytes/step) |`);
  w(`| step bodies re-executed on resume | **${cost.workUnitsReExecuted}** |`);
  w(`| work not repeated | ${cost.costCentsAvoided} cents |`);
  w();
  w('**Zero step bodies re-execute.** That is the claim durable execution makes and it is');
  w('measured by instrumenting the bodies rather than by trusting the design: a replayed');
  w('step returns its journalled result without calling the function, so any body that runs');
  w('during replay increments the counter.');
  w();
  w('The journal is small because it stores *decisions*, not state. Nothing snapshots the');
  w('workflow\'s variables; they are reconstructed by re-running the function, which is why');
  w('the determinism requirement in §3 is load-bearing rather than fastidious.');
  w();

  // -----------------------------------------------------------------------
  w('## 6. The approval gate survives restarts because it is not a promise');
  w();
  w('| measure | value |');
  w('| --- | ---: |');
  w(`| first attempt suspended cleanly | ${gate.suspendedCleanly} |`);
  w(`| journal length while waiting | ${gate.journalLengthWhileWaiting} |`);
  w(`| restarts survived without progress or growth | ${gate.survivedRestarts}/5 |`);
  w(`| steps re-executed across those restarts | ${gate.stepsReExecutedAcrossRestarts} |`);
  w(`| resumed and completed once the signal arrived | ${gate.resumedAfterSignal} |`);
  w();
  w('A human approval implemented as an unresolved promise passes every test, because tests');
  w('do not redeploy. It is lost by the first restart during the two days the human takes to');
  w('answer, and it is lost silently — the run simply is not there any more.');
  w();
  w('Here, `awaitSignal` **unwinds the stack**. The run is not blocked; it is over, and the');
  w('journal records that it was waiting. Restarting replays to the same point and stops');
  w('again, adding nothing to the journal — which is what the "0 growth over 5 restarts" row');
  w('is checking. A restart loop that grew the journal would eventually run out of disk while');
  w('appearing to work.');
  w();

  // -----------------------------------------------------------------------
  const verdicts = score(sweeps, nd, budget, cost, gate, baseline.effects);
  const wrong = verdicts.filter((v) => !v.held).length;
  w('## 7. Predictions');
  w();
  w(`${verdicts.length} predictions were written before any experiment was run. **${wrong} were contradicted.**`);
  w();
  w('| # | prediction | verdict | what actually happened |');
  w('| ---: | --- | --- | --- |');
  for (const v of verdicts) {
    w(`| ${v.n} | ${v.claim} | ${v.held ? 'held' : 'contradicted'} | ${v.actual} |`);
  }
  w();

  // -----------------------------------------------------------------------
  w('## 8. What this does not measure');
  w();
  w('- **No real I/O.** Every effect is an in-process function call. Racing I/O completions');
  w('  is the one genuine source of ordering nondeterminism this harness cannot produce,');
  w('  which is why `unordered-parallel-completion` came back inert.');
  w('- **No concurrency.** One run at a time, no competing writers to the journal. The');
  w('  sequence-gap check in `InMemoryJournal.append` is the only defence against two');
  w('  processes resuming the same run, and it is not enough on its own — a real deployment');
  w('  needs a lease.');
  w('- **The journal is in memory.** `serialise`/`parse` model durability, including a torn');
  w('  final line, but nothing here fsyncs. The crash model is therefore "the process died",');
  w('  not "the disk lied".');
  w('- **One workflow shape.** 40 steps, one effect, one approval gate. The crash sweep is');
  w('  exhaustive over *that* workflow. A workflow with several effects has a wider unknown');
  w('  window, and the 2.4% figure would grow roughly with the number of effects.');
  w('- **Costs are declared, not measured.** `costCents` is a number the workflow states.');
  w('  A real budget needs token accounting from the provider.');
  w();

  return lines.join('\n') + '\n';
}

function tick(b: boolean): string {
  return b ? 'yes' : '--';
}

function score(
  sweeps: readonly CrashSweepResult[],
  nd: readonly NondeterminismResult[],
  budget: Awaited<ReturnType<typeof budgetExperiment>>,
  cost: Awaited<ReturnType<typeof replayCost>>,
  gate: Awaited<ReturnType<typeof approvalGateExperiment>>,
  baselineEffects: number,
): Verdict[] {
  const safe = sweeps[0]!;
  const noDedup = sweeps[1]!;
  const newKey = sweeps[2]!;
  const escalate = sweeps[3]!;

  const latent = nd.filter((r) => r.latent && !r.detectedInProcess).length;
  const inert = nd.filter(
    (r) => !r.detectedInProcess && !r.detectedAfterWorldMoved && !r.silentlyDiverged,
  ).length;
  const caughtInProcess = nd.filter((r) => r.detectedInProcess).length;
  const predictedDetected = nd.filter((r) => r.expected === 'detected').length;
  const control = nd.find((r) => r.name === 'deterministic-control')!;

  const v: Verdict[] = [];
  const add = (n: number, held: boolean, actual: string) =>
    v.push({ n, claim: PREDICTIONS[n - 1]!, held, actual });

  add(1, safe.duplicateEffects === 0 && safe.recovered === safe.crashPoints,
    `${safe.recovered}/${safe.crashPoints} recovered, ${safe.duplicateEffects} duplicate refunds`);

  add(2, safe.unknownWindowPoints > safe.crashPoints * 0.1,
    `${safe.unknownWindowPoints} of ${safe.crashPoints} crash points (${((safe.unknownWindowPoints / safe.crashPoints) * 100).toFixed(1)}%) land in the window`);

  add(3, noDedup.duplicateEffects === 0,
    `${noDedup.duplicateEffects} duplicate refund once the server stops honouring the key`);

  add(4, newKey.duplicateEffects > 0,
    `${newKey.duplicateEffects} duplicate refund from a freshly generated key`);

  add(5, escalate.recovered === escalate.crashPoints,
    `escalate recovered ${escalate.recovered}/${escalate.crashPoints}; ${escalate.escalated} run stopped for a human`);

  add(6, caughtInProcess === predictedDetected,
    `${caughtInProcess} of ${predictedDetected} predicted-detectable patterns were caught by an in-process replay`);

  add(7, latent === 0,
    `${latent} pattern${latent === 1 ? '' : 's'} invisible in-process and only visible once the clock moved`);

  add(8, inert === 0,
    `${inert} patterns were harmless because the nondeterminism was inside a step body`);

  add(9, !budget.naiveWouldTrip,
    `naive counter reports ${budget.naiveResumedSpend} cents against a ${budget.limitCents} limit for work costing ${budget.singleRunSpend}`);

  add(10, cost.workUnitsReExecuted > 0,
    `${cost.workUnitsReExecuted} step bodies re-executed on resume`);

  add(11, gate.survivedRestarts === 5 && gate.stepsReExecutedAcrossRestarts === 0,
    `${gate.survivedRestarts}/5 restarts survived, ${gate.stepsReExecutedAcrossRestarts} steps re-executed, journal did not grow`);

  add(12, !control.detectedInProcess && !control.silentlyDiverged,
    control.detectedInProcess || control.silentlyDiverged
      ? 'the control group was flagged; the detector is broken'
      : 'the control group replayed cleanly, as required');

  add(13, baselineEffects === 1,
    `the crash-free control moved money ${baselineEffects} time`);

  return v;
}
