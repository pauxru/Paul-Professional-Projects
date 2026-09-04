#!/usr/bin/env pwsh
# The two-minute version: show that the failure is invisible to APM, then show what does
# see it and what it cost.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$py = 'C:\Users\rukwaropaul\AppData\Local\Programs\Python\Python312\python.exe'
if (-not (Test-Path $py)) { $py = 'python' }

Write-Host '== 50-silent-failure-observability :: demo ==' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Ninety days of a support assistant. On day 45 the provider quietly routes half' -ForegroundColor Gray
Write-Host 'the traffic to a cheaper model. Nothing throws. Here is what the dashboards say.' -ForegroundColor Gray
Write-Host ''

& $py -c @'
from src import detectors, evaluate, stream

turns = list(stream.generate("model-swap"))
material = stream.first_materially_degraded_day(turns)
quality = stream.true_quality_series(turns)

print(f"  requests        : {len(turns):,}")
print(f"  non-200 responses: {sum(1 for t in turns if t.http_status != 200)}")
print(f"  p95 latency, healthy window : {sorted(t.latency_ms for t in turns if t.day < 30)[int(0.95 * sum(1 for t in turns if t.day < 30))]:.0f} ms")
print(f"  p95 latency, degraded window: {sorted(t.latency_ms for t in turns if t.day >= 80)[int(0.95 * sum(1 for t in turns if t.day >= 80))]:.0f} ms")
print()
print(f"  true answer quality, day 30 : {quality[30]:.2f}")
print(f"  true answer quality, day 85 : {quality[85]:.2f}")
print(f"  quality became material on day {material}")
print()
print("  Every one of those requests returned 200 OK at a normal latency.")
print("  An APM tool has nothing to alert on, and it does not:")
apm = detectors.apm_baseline(turns, stream.DAYS)
print(f"    APM alert day: {detectors.first_alert(apm.scores, apm.threshold)}  (-1 means never)")
'@
if ($LASTEXITCODE -ne 0) { throw 'demo failed' }

Write-Host ''
Write-Host 'Now the full panel: eight detectors, six scenarios, all thresholds calibrated' -ForegroundColor Gray
Write-Host 'identically. This takes about thirty seconds.' -ForegroundColor Gray
Write-Host ''

& $py -c @'
from src import evaluate

panel = evaluate.evaluate()
width = max(len(d) for d in panel.detector_names) + 2

print("  " + "detector".ljust(width) + "".join(k[:13].ljust(15) for k in panel.scenario_keys))
for d in panel.detector_names:
    row = "  " + d.ljust(width)
    for k in panel.scenario_keys:
        c = evaluate.cell(panel, k, d)
        if not c.detected:
            row += "--".ljust(15)
        elif c.delay_days is None:
            row += "FALSE POS".ljust(15)
        else:
            row += ("d%d (%+d)" % (c.alert_day, c.delay_days)).ljust(15)
    print(row)

print()
print(f"  false alarms on the healthy control : {sum(panel.false_alarm_days.values())} days")
print(f"  alerted on `input-shift`, which is not a regression:")
for name, fp in panel.input_shift_false_positive.items():
    if fp:
        print(f"    - {name}")
print()
chosen = evaluate.minimum_covering_set(panel)
print("  Cheapest set of detectors covering every regression:")
for d in chosen:
    print(f"    - {d}")
print(f"  Cost: {sum(evaluate.operating_cost(panel, d)[0] for d in chosen)} model calls per day.")
print(f"  The 24-call/day golden-set canary is not in it.")
'@
if ($LASTEXITCODE -ne 0) { throw 'demo failed' }

Write-Host ''
Write-Host 'Full write-up : docs/results.md' -ForegroundColor Green
Write-Host 'Dashboard     : docs/dashboard.html  (open it in a browser)' -ForegroundColor Green
