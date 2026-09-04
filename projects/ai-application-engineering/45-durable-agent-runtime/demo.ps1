#!/usr/bin/env pwsh
# A five-minute tour. Each section runs a real experiment and prints its real numbers;
# nothing here is a recording.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Section([string]$title) {
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor DarkGray
    Write-Host "  $title" -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor DarkGray
}

Write-Host '45-durable-agent-runtime -- what survives a crash, and what does not' -ForegroundColor White

Section '1. One clean run of the refund workflow'
node --input-type=module -e @"
import { baselineRun } from './src/experiments.ts';
const b = await baselineRun();
console.log(``  status          `${b.outcome.status}``);
console.log(``  journal events  `${b.journalLength}``);
console.log(``  money moved     `${b.effects} time(s)``);
console.log(``  cost            `${b.spentCents} cents``);
"@

Section '2. Crash before every single journal write, four times over'
node --input-type=module -e @"
import { crashSweep } from './src/experiments.ts';
const rows = [
  ['retry-same-key', true], ['retry-same-key', false],
  ['retry-new-key', true], ['escalate', true],
];
console.log('  policy           dedup  recovered  duplicates  escalated');
for (const [p, d] of rows) {
  const s = await crashSweep(p, d);
  console.log(``  `${p.padEnd(16)} `${(d?'yes':'no').padEnd(6)} `${String(s.recovered+'/'+s.crashPoints).padEnd(10)} `${String(s.duplicateEffects).padEnd(11)} `${s.escalated}``);
}
"@
Write-Host '  -> the four rows differ at exactly one crash point out of 42.' -ForegroundColor Yellow

Section '3. Ten ways to break replay, and which ones you would ever notice'
node --input-type=module -e @"
import { nondeterminismSweep } from './src/experiments.ts';
for (const r of await nondeterminismSweep()) {
  const verdict = r.detectedInProcess ? 'caught by a test'
    : r.detectedAfterWorldMoved ? 'LATENT -- only fails in production'
    : r.silentlyDiverged ? 'SILENT -- no guard can see it'
    : r.expected === 'clean' ? 'deterministic by construction (the control)'
    : 'inert -- the nondeterminism never reached the replay stream';
  console.log(``  `${r.name.padEnd(32)} `${verdict}``);
}
"@

Section '4. The budget a resumed run invents'
node --input-type=module -e @"
import { budgetExperiment } from './src/experiments.ts';
const b = await budgetExperiment();
console.log(``  the work costs                    `${b.singleRunSpend} cents (limit `${b.limitCents})``);
console.log(``  counter that resets on resume     `${b.naiveResumedSpend} cents -> `${b.naiveWouldTrip ? 'ABORTS THE RUN' : 'ok'}``);
console.log(``  counter re-derived from journal   `${b.journalAwareResumedSpend} cents -> `${b.journalAwareTrips ? 'aborts' : 'ok'}``);
"@

Section '5. A human approval that survives a deploy'
node --input-type=module -e @"
import { approvalGateExperiment } from './src/experiments.ts';
const g = await approvalGateExperiment(5);
console.log(``  suspended cleanly                 `${g.suspendedCleanly}``);
console.log(``  restarts survived                 `${g.survivedRestarts}/5``);
console.log(``  steps re-executed meanwhile       `${g.stepsReExecutedAcrossRestarts}``);
console.log(``  resumed when the signal arrived   `${g.resumedAfterSignal}``);
"@

Section 'Full write-up'
Write-Host '  docs/results.md      -- every number above, with the argument around it'
Write-Host '  docs/adr/            -- the six decisions that produced them'
Write-Host '  docs/known-limitations.md -- what this does not show'
Write-Host ''
