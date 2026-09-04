import assert from 'node:assert/strict';
import { before, describe, it } from 'node:test';
import {
  approvalGateExperiment,
  baselineRun,
  budgetExperiment,
  crashSweep,
  nondeterminismSweep,
  replayCost,
  type CrashSweepResult,
  type NondeterminismResult,
} from '../src/experiments.ts';
import { PREDICTIONS } from '../src/predictions.ts';
import { buildReport } from '../src/report.ts';
import { NONDETERMINISM_CORPUS, REFUND_STEP_COUNT } from '../src/workflows.ts';

describe('baseline', () => {
  it('the crash-free run completes', async () => {
    assert.equal((await baselineRun()).outcome.status, 'completed');
  });

  it('the crash-free run moves money exactly once', async () => {
    assert.equal((await baselineRun()).effects, 1);
  });

  it('the workflow is long enough for the crash sweep to be interesting', async () => {
    assert.ok(REFUND_STEP_COUNT >= 30, `only ${REFUND_STEP_COUNT} steps`);
  });

  it('the journal has one event per step plus start, effect pair and completion', async () => {
    assert.equal((await baselineRun()).journalLength, 42);
  });

  it('the run costs what the workflow declares', async () => {
    assert.equal((await baselineRun()).spentCents, 230);
  });
});

describe('crash sweep', () => {
  let safe: CrashSweepResult;
  let noDedup: CrashSweepResult;
  let newKey: CrashSweepResult;
  let escalate: CrashSweepResult;

  before(async () => {
    safe = await crashSweep('retry-same-key', true);
    noDedup = await crashSweep('retry-same-key', false);
    newKey = await crashSweep('retry-new-key', true);
    escalate = await crashSweep('escalate', true);
  });

  it('crashes at every journal write of the complete run', () => {
    assert.equal(safe.crashPoints, 42);
  });

  it('recovers from every crash point under the safe policy', () => {
    assert.equal(safe.recovered, safe.crashPoints);
  });

  it('issues no duplicate refund under the safe policy', () => {
    assert.equal(safe.duplicateEffects, 0);
  });

  it('loses no refund under the safe policy', () => {
    assert.equal(safe.lostEffects, 0);
  });

  it('escalates nothing when the gateway deduplicates and the key is reused', () => {
    assert.equal(safe.escalated, 0);
  });

  it('a non-deduplicating gateway produces a duplicate refund', () => {
    assert.equal(noDedup.duplicateEffects, 1);
  });

  it('a fresh key on retry produces a duplicate refund', () => {
    assert.equal(newKey.duplicateEffects, 1);
  });

  it('escalate stops exactly one run for a human', () => {
    assert.equal(escalate.escalated, 1);
    assert.equal(escalate.recovered, escalate.crashPoints - 1);
  });

  it('escalate never issues a duplicate', () => {
    assert.equal(escalate.duplicateEffects, 0);
  });

  it('the unknown window is exactly one crash point', () => {
    assert.equal(safe.unknownWindowPoints, 1);
  });

  it('every policy sees the same unknown window; only the response differs', () => {
    for (const s of [safe, noDedup, newKey, escalate]) {
      assert.equal(s.unknownWindowPoints, 1, s.policy);
    }
  });

  it('the number of dangerous crash points equals the number of effects', async () => {
    assert.equal(safe.unknownWindowPoints, (await baselineRun()).effects);
  });

  it('the policies differ only at the unknown window', () => {
    const risky = [noDedup.duplicateEffects, newKey.duplicateEffects, escalate.escalated];
    assert.ok(
      risky.every((n) => n <= safe.unknownWindowPoints),
      'no policy can go wrong more often than the window occurs',
    );
  });
});

describe('nondeterminism corpus', () => {
  let results: readonly NondeterminismResult[];

  before(async () => {
    results = await nondeterminismSweep();
  });

  it('probes every corpus entry', () => {
    assert.equal(results.length, NONDETERMINISM_CORPUS.length);
  });

  it('has ten entries', () => {
    assert.equal(NONDETERMINISM_CORPUS.length, 10);
  });

  it('every entry explains why an engineer would write it', () => {
    for (const e of NONDETERMINISM_CORPUS) assert.ok(e.why.length > 40, e.name);
  });

  it('the deterministic control is never flagged', () => {
    const c = results.find((r) => r.name === 'deterministic-control')!;
    assert.equal(c.detectedInProcess, false);
    assert.equal(c.detectedAfterWorldMoved, false);
    assert.equal(c.silentlyDiverged, false);
  });

  it('branching on Math.random is caught immediately', () => {
    assert.equal(results.find((r) => r.name === 'branch-on-math-random')!.detectedInProcess, true);
  });

  it('a renamed step from an exception message is caught immediately', () => {
    assert.equal(
      results.find((r) => r.name === 'exception-message-in-step-name')!.detectedInProcess,
      true,
    );
  });

  it('a module-level cache is caught immediately', () => {
    assert.equal(results.find((r) => r.name === 'module-level-cache')!.detectedInProcess, true);
  });

  it('branching on the wall clock is invisible to an in-process replay', () => {
    const r = results.find((r) => r.name === 'branch-on-wall-clock')!;
    assert.equal(r.detectedInProcess, false, 'a same-millisecond replay reads the same clock');
    assert.equal(r.detectedAfterWorldMoved, true, 'it only appears once time has passed');
  });

  it('exactly two patterns are latent', () => {
    const latent = results.filter((r) => r.latent && !r.detectedInProcess);
    assert.equal(latent.length, 2);
  });

  it('a clock read that is not branched on diverges silently', () => {
    const r = results.find((r) => r.name === 'clock-read-not-branched-on')!;
    assert.equal(r.silentlyDiverged, true);
    assert.equal(r.detectedInProcess, false, 'no guard can see an identical operation sequence');
  });

  it('nondeterminism inside a step body is harmless', () => {
    for (const name of ['uuid-as-idempotency-key', 'float-accumulation-order']) {
      const r = results.find((x) => x.name === name)!;
      assert.equal(r.detectedInProcess, false, name);
      assert.equal(r.detectedAfterWorldMoved, false, name);
      assert.equal(r.silentlyDiverged, false, name);
    }
  });

  it('four patterns are caught by an in-process replay', () => {
    assert.equal(results.filter((r) => r.detectedInProcess).length, 4);
  });

  it('every pattern caught in-process is also caught after the world moves', () => {
    for (const r of results) {
      if (r.detectedInProcess) assert.equal(r.detectedAfterWorldMoved, true, r.name);
    }
  });

  it('carries the prediction forward so the scoreboard can score it', () => {
    for (const r of results) assert.ok(r.expected.length > 0, r.name);
  });
});

describe('budget', () => {
  it('the naive counter over-reports a resumed run', async () => {
    const b = await budgetExperiment();
    assert.ok(b.naiveResumedSpend > b.singleRunSpend);
  });

  it('the naive counter trips a limit the work never exceeded', async () => {
    const b = await budgetExperiment();
    assert.equal(b.naiveWouldTrip, true);
    assert.ok(b.singleRunSpend < b.limitCents);
  });

  it('the journal-aware counter reports the true spend', async () => {
    const b = await budgetExperiment();
    assert.equal(b.journalAwareResumedSpend, b.singleRunSpend);
  });

  it('the journal-aware counter does not trip', async () => {
    assert.equal((await budgetExperiment()).journalAwareTrips, false);
  });

  it('the naive figure is 310 against a 250 limit for 200 cents of work', async () => {
    const b = await budgetExperiment();
    assert.equal(b.naiveResumedSpend, 310);
    assert.equal(b.limitCents, 250);
    assert.equal(b.singleRunSpend, 200);
  });

  it('a higher limit removes the false trip without fixing the counter', async () => {
    const b = await budgetExperiment(1000);
    assert.equal(b.naiveWouldTrip, false);
    assert.ok(b.naiveResumedSpend > b.singleRunSpend, 'still over-reporting, just not fatally');
  });
});

describe('replay cost', () => {
  it('no step body re-executes on resume', async () => {
    assert.equal((await replayCost()).workUnitsReExecuted, 0);
  });

  it('the journal is small relative to the work it replaces', async () => {
    const c = await replayCost();
    assert.ok(c.bytesPerStep < 200, `${c.bytesPerStep} bytes/step`);
  });

  it('resumption avoids re-doing the work already recorded', async () => {
    assert.ok((await replayCost()).costCentsAvoided > 0);
  });

  it('the step count matches the workflow', async () => {
    assert.equal((await replayCost()).stepsReplayed, REFUND_STEP_COUNT);
  });
});

describe('approval gate experiment', () => {
  it('suspends cleanly', async () => {
    assert.equal((await approvalGateExperiment()).suspendedCleanly, true);
  });

  it('survives every restart', async () => {
    assert.equal((await approvalGateExperiment(5)).survivedRestarts, 5);
  });

  it('survives twenty restarts just as well', async () => {
    assert.equal((await approvalGateExperiment(20)).survivedRestarts, 20);
  });

  it('re-executes nothing across restarts', async () => {
    assert.equal((await approvalGateExperiment()).stepsReExecutedAcrossRestarts, 0);
  });

  it('resumes once the signal arrives', async () => {
    assert.equal((await approvalGateExperiment()).resumedAfterSignal, true);
  });
});

describe('report', () => {
  it('produces the same bytes twice', async () => {
    assert.equal(await buildReport(true), await buildReport(true));
  });

  it('the stable file omits the byte measurement', async () => {
    assert.ok(!(await buildReport(true)).includes('bytes/step'));
    assert.ok((await buildReport(false)).includes('bytes/step'));
  });

  it('scores every prediction', async () => {
    const report = await buildReport(true);
    for (const p of PREDICTIONS) {
      assert.ok(report.includes(p.slice(0, 60)), p.slice(0, 40));
    }
  });

  it('states the number of predictions it actually scored', async () => {
    const report = await buildReport(true);
    const rows = report.split('\n').filter((l) => /^\| \d+ \| /.test(l)).length;
    assert.equal(rows, PREDICTIONS.length);
    assert.ok(report.includes(`${PREDICTIONS.length} predictions were written`));
  });

  it('reports a non-trivial number of contradicted predictions', async () => {
    const report = await buildReport(true);
    const contradicted = report.split('\n').filter((l) => l.includes('| contradicted |')).length;
    assert.ok(contradicted >= 5, `only ${contradicted} contradicted`);
    assert.ok(report.includes(`**${contradicted} were contradicted.**`));
  });

  it('has all eight sections', async () => {
    const report = await buildReport(true);
    for (let i = 1; i <= 8; i++) assert.match(report, new RegExp(`^## ${i}\\. `, 'm'));
  });

  it('says what it does not measure', async () => {
    assert.match(await buildReport(true), /## 8\. What this does not measure/);
  });

  it('contains no placeholder text', async () => {
    assert.ok(!/TODO|FIXME|XXX|TBD/.test(await buildReport(false)));
  });
});
