<#
.SYNOPSIS
    Print one section of the report, or the headline findings.

.EXAMPLE
    ./demo.ps1               # the findings that matter most
    ./demo.ps1 -Section 7    # "A judge with 92% agreement can be worthless"
    ./demo.ps1 -Full         # regenerate and print the whole report
#>
[CmdletBinding()]
param(
    [int]$Section = 0,
    [switch]$Full,
    [switch]$Quick
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$py = 'C:\Users\rukwaropaul\AppData\Local\Programs\Python\Python312\python.exe'
if (-not (Test-Path $py)) { $py = 'python' }

Push-Location $root
try {
    if ($Section -gt 0) {
        $args = @('run_eval.py', '--section', $Section, '--stdout')
        if ($Quick) { $args += '--quick' }
        & $py @args
        return
    }

    if ($Full) {
        & $py run_eval.py --out docs\results.md --stdout
        return
    }

    $results = Join-Path $root 'docs\results.md'
    if (-not (Test-Path $results)) {
        Write-Host "docs/results.md not found; generating (this takes a few minutes)..."
        & $py run_eval.py --out $results
    }

    Write-Host ""
    Write-Host "  LLM EVALUATION HARNESS -- headline findings" -ForegroundColor Cyan
    Write-Host "  (full report: docs/results.md; one section: ./demo.ps1 -Section N)"
    Write-Host ""

    $findings = @(
        @{ S = 1;  T = 'A 50-item eval set has 50.9% power to see a +0.05 improvement.' },
        @{ S = 1;  T = 'Holding everything else fixed, power moves 28.5% -> 79.0% purely'
           T2 = 'with how much the two systems have in common. Adequacy is not about 50.' },
        @{ S = 2;  T = 'Significant results at that power are inflated 1.39x.' },
        @{ S = 3;  T = 'Detecting +0.01 instead of +0.05 costs 25x the items (93 -> 2,309).' },
        @{ S = 5;  T = '20 uncorrected slice tests: 41.8% chance of a false positive.' },
        @{ S = 6;  T = "107.1% of a sweep winner's gain evaporates on held-out data." },
        @{ S = 7;  T = 'A judge at 93.0% agreement / AC1 0.885 beats a hard-coded string'
           T2 = 'by +0.0040 [-0.0073, +0.0145] -- indistinguishable from zero on 4,000 items.' },
        @{ S = 9;  T = 'A length-biased judge reports +0.0711 significant for a true +0.0021.'
           T2 = 'Growing the eval set 25 -> 800 narrows the interval 5.1x and buys only'
           T3 = 'confidence in a false conclusion.' },
        @{ S = 11; T = '8.5% of individual 50-item runs call a real +0.04 gain a decline.' },
        @{ S = 12; T = 'The gate catches 97% of real regressions and never calls one an'
           T2 = 'improvement.' },
        @{ S = 13; T = 'BCa interval coverage at n=20: 91.0% vs percentile 86.2%.' }
    )

    foreach ($f in $findings) {
        Write-Host ("  [{0,2}] " -f $f.S) -NoNewline -ForegroundColor DarkGray
        Write-Host $f.T -ForegroundColor White
        foreach ($k in 'T2', 'T3') {
            if ($f.$k) { Write-Host "       $($f.$k)" -ForegroundColor Gray }
        }
    }

    Write-Host ""
    $text = Get-Content $results -Raw
    $preds = ([regex]::Matches($text, '\*\*Predicted\.\*\*')).Count
    $wrong = ([regex]::Matches($text, 'prediction wrong')).Count
    Write-Host "  $preds predictions written before the measurement. $wrong were wrong," -ForegroundColor Yellow
    Write-Host "  and each is marked in place -- see sections 1, 5 and 11." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  No LLM is called anywhere. docs/adr/001 argues why that is the" -ForegroundColor DarkGray
    Write-Host "  stronger choice; docs/known-limitations.md says what it costs." -ForegroundColor DarkGray
    Write-Host ""
}
finally { Pop-Location }
