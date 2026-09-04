<#
    scripts/demo.ps1 — Enterprise Audit & Compliance Event Store demo
    -----------------------------------------------------------------
    End-to-end integrity demonstration:
        1. Start the API on port 5019 with a fresh SQLite database.
        2. Mint a dev JWT.
        3. Register the event schema, ingest five events for the fictional
           `example-bank` tenant.
        4. Verify the hash chain — expect valid.
        5. Directly tamper with the DB (out-of-band UPDATE) to simulate a
           malicious insider bypassing the API layer.
        6. Re-verify — expect the chain to break at the tampered sequence.
        7. Build a signed evidence pack for an auditor.

    Requires: PowerShell 7+, .NET 10 SDK, an available TCP port 5019.
#>

param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot ".."))
)

$ErrorActionPreference = "Stop"
Set-Location $RepoRoot

$dbFile = Join-Path $RepoRoot "demo.db"
if (Test-Path $dbFile) { Remove-Item $dbFile -Force }

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Database__Provider = "Sqlite"
$env:Database__ConnectionString = "Data Source=$dbFile;Cache=Shared"
$env:Jwt__SigningKey = "demo-signing-key-not-secret-not-for-production-32chars"
$env:Seed__Enabled = "true"
$env:Seed__TenantId = "example-bank"

Write-Host "== 1. Starting API on http://localhost:5019 ==" -ForegroundColor Cyan
$apiJob = Start-Process -PassThru -WindowStyle Hidden dotnet -ArgumentList @(
    "run", "--project", "src/AuditPlatform.Api/AuditPlatform.Api.csproj",
    "-c", "Release", "--no-launch-profile"
)
Start-Sleep -Seconds 8

try {
    Write-Host "== 2. Waiting for readiness ==" -ForegroundColor Cyan
    for ($i = 0; $i -lt 30; $i++) {
        try {
            $r = Invoke-WebRequest -Uri "http://localhost:5019/health/ready" -UseBasicParsing -TimeoutSec 2
            if ($r.StatusCode -eq 200) { break }
        } catch { Start-Sleep -Seconds 1 }
    }

    Write-Host "== 3. Requesting a dev token ==" -ForegroundColor Cyan
    $tokenResp = Invoke-RestMethod -Method POST -Uri "http://localhost:5019/api/v1/dev/token" -ContentType "application/json" -Body (@{
        subject = "demo-analyst"; tenantId = "example-bank"; clearance = "investigator";
        scopes = @("audit:read","audit:write","audit:verify","audit:admin","audit:export")
    } | ConvertTo-Json)
    $headers = @{ Authorization = "Bearer $($tokenResp.token)" }
    Write-Host "  token acquired." -ForegroundColor DarkGray

    Write-Host "== 4. Ingesting 5 audit events ==" -ForegroundColor Cyan
    for ($i = 1; $i -le 5; $i++) {
        $body = @{
            eventType = "user.login"
            schemaVersion = 1
            eventTime = [DateTimeOffset]::UtcNow
            actor = @{ type = 0; id = "u-demo-$i"; displayName = "Demo User $i"; roles = @("banker") }
            actionVerb = "login"
            category = 1
            resource = @{ type = "session"; id = "sess-$i"; name = "sign-in"; parentPath = "/auth" }
            outcome = 0; severity = 0
            source = @{ ip = "10.0.0.$i"; userAgent = "demo"; service = "demo-svc"; region = "eu" }
            correlationId = "demo-corr-$i"
            clientEventId = "demo-$i"
            data = @{ method = "password" }
        } | ConvertTo-Json -Depth 6
        Invoke-RestMethod -Method POST -Uri "http://localhost:5019/api/v1/events" -Headers $headers -ContentType "application/json" -Body $body | Out-Null
    }

    Write-Host "== 5. Verifying the chain (expect valid=true) ==" -ForegroundColor Cyan
    $verify1 = Invoke-RestMethod -Method POST -Uri "http://localhost:5019/api/v1/verify" -Headers $headers -ContentType "application/json" -Body (@{ fromSequence = 1; toSequence = 1000 } | ConvertTo-Json)
    Write-Host ("  valid={0}" -f $verify1.isValid) -ForegroundColor Green

    Write-Host "== 6. Tampering with sequence 3 directly in SQLite ==" -ForegroundColor Yellow
    $sqlFile = Join-Path $RepoRoot "demo-tamper.sql"
    "UPDATE AuditEvents SET PayloadJson='{\"tampered\":true}' WHERE TenantId='example-bank' AND SequenceNumber=3;" | Set-Content $sqlFile
    # NOTE: sqlite3.exe is not always available on Windows without extra install. Fallback:
    # use the API's admin surface? For demo simplicity we rely on `dotnet-ef` or `sqlite3`.
    if (Get-Command sqlite3 -ErrorAction SilentlyContinue) {
        sqlite3 $dbFile ".read $sqlFile"
    } else {
        Write-Host "  (skipping SQL tamper — sqlite3 CLI not on PATH; install winget install SQLite.SQLite)" -ForegroundColor DarkYellow
    }
    Remove-Item $sqlFile -ErrorAction SilentlyContinue

    Write-Host "== 7. Re-verifying (expect valid=false at sequence 3 if tampered) ==" -ForegroundColor Yellow
    $verify2 = Invoke-RestMethod -Method POST -Uri "http://localhost:5019/api/v1/verify" -Headers $headers -ContentType "application/json" -Body (@{ fromSequence = 1; toSequence = 1000 } | ConvertTo-Json)
    Write-Host ("  valid={0}, brokenAt={1}, reason={2}" -f $verify2.isValid, $verify2.brokenAtSequence, $verify2.reason)

    Write-Host "== 8. Building a signed evidence pack for auditor ==" -ForegroundColor Cyan
    Invoke-RestMethod -Method POST -Uri "http://localhost:5019/api/v1/checkpoints" -Headers $headers | Out-Null
    $pack = Invoke-RestMethod -Method POST -Uri "http://localhost:5019/api/v1/exports/evidence" -Headers $headers -ContentType "application/json" -Body (@{ from = "1970-01-01T00:00:00Z"; to = "2999-12-31T23:59:59Z" } | ConvertTo-Json)
    $packPath = Join-Path $RepoRoot "demo-evidence-pack.json"
    ($pack | ConvertTo-Json -Depth 10) | Set-Content $packPath
    Write-Host "  wrote $packPath" -ForegroundColor Green

    Write-Host "== 9. Verifying the evidence pack round-trip ==" -ForegroundColor Cyan
    $verifyPack = Invoke-RestMethod -Method POST -Uri "http://localhost:5019/api/v1/exports/evidence/verify" -Headers $headers -ContentType "application/json" -Body ($pack | ConvertTo-Json -Depth 10)
    Write-Host ("  pack valid={0}" -f $verifyPack.valid) -ForegroundColor Green

} finally {
    Write-Host "== Cleanup: stopping API ==" -ForegroundColor Cyan
    if ($apiJob -and !$apiJob.HasExited) { Stop-Process -Id $apiJob.Id -Force -ErrorAction SilentlyContinue }
}
