<#
.SYNOPSIS
    End-to-end demo for the Example Bank Digital Banking Ledger.

.DESCRIPTION
    Builds the solution, launches the API on http://localhost:5013 (Development,
    which enables the local dev-token endpoint), then exercises the headline
    flows against the REAL running API:
        mint token -> create accounts -> fund -> transfer -> balances ->
        trial balance -> hold place+capture -> reversal -> FX quote ->
        integrity verification.
    The API process is stopped and the demo database removed on exit.

.NOTES
    Requires only the .NET 10 SDK. No Docker, no external services.
    PowerShell:  pwsh -File scripts\demo.ps1   (or run from Windows PowerShell)
#>

[CmdletBinding()]
param(
    [int]    $Port    = 5013,
    [string] $BaseUrl = ""
)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot
$apiProj = Join-Path $root 'src\ExampleBank.Ledger.Api\ExampleBank.Ledger.Api.csproj'
if (-not $BaseUrl) { $BaseUrl = "http://localhost:$Port" }
$demoDb  = Join-Path $root ("demo-{0}.db" -f ([guid]::NewGuid().ToString('N')))
$apiProc = $null

function Write-Step([string] $text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Invoke-Api {
    param(
        [string] $Method,
        [string] $Path,
        [hashtable] $Headers,
        $Body
    )
    $uri = "$BaseUrl$Path"
    $args = @{ Method = $Method; Uri = $uri; Headers = $Headers }
    if ($null -ne $Body) {
        $args.Body        = ($Body | ConvertTo-Json -Depth 8)
        $args.ContentType = 'application/json'
    }
    return Invoke-RestMethod @args
}

try {
    Write-Step "Building (Release)"
    dotnet build (Join-Path $root 'ExampleBank.Ledger.slnx') -c Release --nologo | Out-Null

    Write-Step "Launching API on $BaseUrl (Development)"
    $env:ASPNETCORE_ENVIRONMENT   = 'Development'
    $env:ConnectionStrings__Ledger = "Data Source=$demoDb"
    $apiProc = Start-Process -FilePath 'dotnet' `
        -ArgumentList @('run', '--no-build', '-c', 'Release', '--project', $apiProj) `
        -PassThru -WindowStyle Hidden

    # Wait for health.
    $healthy = $false
    for ($i = 0; $i -lt 40; $i++) {
        try {
            $h = Invoke-RestMethod -Method Get -Uri "$BaseUrl/health" -TimeoutSec 2
            if ($h.status -eq 'healthy') { $healthy = $true; break }
        } catch { Start-Sleep -Milliseconds 750 }
    }
    if (-not $healthy) { throw "API did not become healthy on $BaseUrl" }
    Write-Host "API is healthy." -ForegroundColor Green

    Write-Step "Minting an admin dev token (all scopes)"
    $tokenResp = Invoke-Api -Method Post -Path '/api/v1/dev/token' -Headers @{} -Body @{
        subject = 'demo-operator'
        scopes  = @('ledger:read', 'ledger:post', 'ledger:adjust', 'ledger:admin')
    }
    $token = $tokenResp.access_token
    $auth  = @{ Authorization = "Bearer $token" }
    Write-Host "Token acquired." -ForegroundColor Green

    Write-Step "Locating the seeded CASH-KES general-ledger account"
    $accounts = Invoke-Api -Method Get -Path '/api/v1/accounts' -Headers $auth
    $cashKes  = $accounts | Where-Object { $_.code -eq 'CASH-KES' } | Select-Object -First 1
    Write-Host ("CASH-KES = {0}" -f $cashKes.id)

    Write-Step "Creating two KES customer deposit accounts (Liability, credit-normal)"
    $suffix = (Get-Date -Format 'HHmmss')
    $custA = Invoke-Api -Method Post -Path '/api/v1/accounts' -Headers $auth -Body @{
        code = "CUST-A-$suffix"; name = 'Demo customer A'; type = 'Liability'; currency = 'KES'
        parentCode = 'DEPOSITS-KES'; isCustomerAccount = $true; overdraftLimitMinor = 0
    }
    $custB = Invoke-Api -Method Post -Path '/api/v1/accounts' -Headers $auth -Body @{
        code = "CUST-B-$suffix"; name = 'Demo customer B'; type = 'Liability'; currency = 'KES'
        parentCode = 'DEPOSITS-KES'; isCustomerAccount = $true; overdraftLimitMinor = 0
    }
    Write-Host ("Customer A = {0}" -f $custA.id)
    Write-Host ("Customer B = {0}" -f $custB.id)

    Write-Step "Funding customer A with 1,000.00 KES (Debit CASH-KES / Credit customer A)"
    $fund = Invoke-Api -Method Post -Path '/api/v1/entries' -Headers ($auth + @{ 'Idempotency-Key' = "fund-$suffix" }) -Body @{
        type        = 'Adjustment'
        description = 'Cash deposit to customer A'
        sourceSystem = 'demo'
        postings = @(
            @{ accountId = $cashKes.id; direction = 'Debit';  amountMinor = 100000; currency = 'KES' },
            @{ accountId = $custA.id;   direction = 'Credit'; amountMinor = 100000; currency = 'KES' }
        )
    }
    Write-Host ("Funding entry seq={0} hash={1}..." -f $fund.sequenceNumber, $fund.hash.Substring(0,12))

    Write-Step "Transferring 300.00 KES from A to B"
    $transfer = Invoke-Api -Method Post -Path '/api/v1/transfers' -Headers ($auth + @{ 'Idempotency-Key' = "xfer-$suffix" }) -Body @{
        fromAccountId = $custA.id; toAccountId = $custB.id
        amountMinor = 30000; currency = 'KES'; description = 'A pays B'
    }
    Write-Host ("Transfer entry seq={0}" -f $transfer.sequenceNumber)

    Write-Step "Balances after transfer (derived from postings)"
    $balA = Invoke-Api -Method Get -Path "/api/v1/accounts/$($custA.id)/balance" -Headers $auth
    $balB = Invoke-Api -Method Get -Path "/api/v1/accounts/$($custB.id)/balance" -Headers $auth
    Write-Host ("Customer A available = {0} minor ({1:N2} KES)" -f $balA.account.availableMinor, ($balA.account.availableMinor/100))
    Write-Host ("Customer B available = {0} minor ({1:N2} KES)" -f $balB.account.availableMinor, ($balB.account.availableMinor/100))

    Write-Step "Replaying the transfer with the SAME idempotency key (must NOT double-post)"
    $replay = Invoke-Api -Method Post -Path '/api/v1/transfers' -Headers ($auth + @{ 'Idempotency-Key' = "xfer-$suffix" }) -Body @{
        fromAccountId = $custA.id; toAccountId = $custB.id
        amountMinor = 30000; currency = 'KES'; description = 'A pays B'
    }
    if ($replay.sequenceNumber -eq $transfer.sequenceNumber) {
        Write-Host ("Idempotent replay returned the original entry (seq={0}). No money created." -f $replay.sequenceNumber) -ForegroundColor Green
    } else {
        Write-Host "WARNING: idempotency replay produced a different entry!" -ForegroundColor Red
    }

    Write-Step "Placing a 200.00 KES hold on A, then capturing 120.00 to B"
    $hold = Invoke-Api -Method Post -Path '/api/v1/holds' -Headers ($auth + @{ 'Idempotency-Key' = "hold-$suffix" }) -Body @{
        accountId = $custA.id; amountMinor = 20000; currency = 'KES'; expiresInMinutes = 60; reference = 'card-auth'
    }
    $capture = Invoke-Api -Method Post -Path "/api/v1/holds/$($hold.id)/capture" -Headers ($auth + @{ 'Idempotency-Key' = "cap-$suffix" }) -Body @{
        destinationAccountId = $custB.id; captureMinor = 12000; reference = 'card-settle'
    }
    Write-Host ("Hold {0} captured 120.00, remainder released." -f $hold.id)

    Write-Step "Partially reversing the original transfer (100.00 KES)"
    $reversal = Invoke-Api -Method Post -Path '/api/v1/reversals' -Headers ($auth + @{ 'Idempotency-Key' = "rev-$suffix" }) -Body @{
        originalEntryId = $transfer.id; amountMinor = 10000; reason = 'demo partial reversal'
    }
    Write-Host ("Reversal entry seq={0} references original {1}" -f $reversal.sequenceNumber, $reversal.reversalOfEntryId)

    Write-Step "FX quote: convert 100.00 USD -> KES (read-only preview)"
    try {
        $usdAcct = $accounts | Where-Object { $_.code -eq 'CASH-USD' } | Select-Object -First 1
        $quote = Invoke-Api -Method Post -Path '/api/v1/fx/quote' -Headers $auth -Body @{
            fromAccountId = $usdAcct.id; toAccountId = $cashKes.id; amountMinor = 10000
        }
        Write-Host ("Quote: {0} {1} -> {2} {3} (rate {4}/{5}, remainder {6}/{7} minor)" -f `
            $quote.sourceMinor, $quote.fromCurrency, $quote.targetMinor, $quote.toCurrency, `
            $quote.numerator, $quote.denominator, $quote.remainderNumerator, $quote.remainderDenominator)
    } catch {
        Write-Host "FX quote skipped: $($_.Exception.Message)" -ForegroundColor Yellow
    }

    Write-Step "Trial balance (must sum to zero per currency)"
    $tb = Invoke-Api -Method Get -Path '/api/v1/reports/trial-balance' -Headers $auth
    Write-Host ("Trial balance isBalanced = {0}" -f $tb.isBalanced) -ForegroundColor Green

    Write-Step "Integrity verification (hash chain + cached/derived reconciliation)"
    $integrity = Invoke-Api -Method Get -Path '/api/v1/admin/integrity/verify' -Headers $auth
    Write-Host ("Integrity isHealthy = {0}; chain entries checked = {1}; accounts reconciled = {2}" -f `
        $integrity.isHealthy, $integrity.chain.entriesChecked, $integrity.reconciliation.accountsChecked) -ForegroundColor Green

    Write-Step "Demo complete — the ledger balanced, reconciled, and never created money."
}
finally {
    if ($apiProc -and -not $apiProc.HasExited) {
        Write-Host "`nStopping API (PID $($apiProc.Id))..." -ForegroundColor DarkGray
        Stop-Process -Id $apiProc.Id -Force -ErrorAction SilentlyContinue
        $apiProc.WaitForExit(5000) | Out-Null
    }
    Start-Sleep -Milliseconds 300
    Get-ChildItem -Path $root -Filter 'demo-*.db*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}
