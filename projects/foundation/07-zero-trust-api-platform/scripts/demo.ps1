# =====================================================================
# Zero-Trust API Platform - end-to-end demo
# Exercises all three surfaces (customer, partner, admin) and the
# trust-core capabilities (JWKS, key rotation, audit chain verification,
# API-key migration report).
#
# Prereq: `dotnet run --project src\ZeroTrust.Api` is running on port 5007.
# Usage:  .\scripts\demo.ps1
# =====================================================================
$ErrorActionPreference = "Stop"
$base = "http://localhost:5007"

function Ok($m)   { Write-Host "  [ok] $m"  -ForegroundColor Green }
function Info($m) { Write-Host "* $m"       -ForegroundColor Cyan  }
function Note($m) { Write-Host "    $m"     -ForegroundColor Gray  }

function BearerHeaders($token, [hashtable]$extra) {
    $h = @{ Authorization = "Bearer $token" }
    if ($extra) { $extra.GetEnumerator() | ForEach-Object { $h[$_.Key] = $_.Value } }
    return $h
}

# ---------------------------------------------------------------------
# Trust core: JWKS and OIDC-ish discovery
# ---------------------------------------------------------------------
Info "1. Trust core"
$jwks = Invoke-RestMethod "$base/.well-known/jwks.json"
Ok "JWKS returns $(($jwks.keys).Count) key(s); primary kid = $($jwks.keys[0].kid)"

$discovery = Invoke-RestMethod "$base/.well-known/openid-configuration"
Ok "Discovery issuer = $($discovery.issuer)"

# ---------------------------------------------------------------------
# Customer surface - password grant + IDOR check
# ---------------------------------------------------------------------
Info "2. Customer surface"
$body = @{
    grant_type = "password"
    subject    = "alice"
    password   = "CustomerPassw0rd!"
    audience   = "ntsf-customer-api"
    scope      = "customer.read customer.write"
} | ConvertTo-Json
$aliceTok = Invoke-RestMethod -Method Post -Uri "$base/api/v1/auth/token" -ContentType "application/json" -Body $body
Ok "Alice obtained bearer + refresh"

$accounts = Invoke-RestMethod -Uri "$base/api/v1/customer/accounts" -Headers (BearerHeaders $aliceTok.access_token)
Ok "Alice sees $(($accounts).Count) account(s), all owned by 'alice'"
Note "Sample: $($accounts[0].accountNumber) balance=$($accounts[0].balanceMinorUnits) $($accounts[0].currency)"

# Attempt IDOR - Alice reads Bob's account by ID
$bobBody = @{
    grant_type = "password"
    subject    = "bob"
    password   = "CustomerPassw0rd!"
    audience   = "ntsf-customer-api"
    scope      = "customer.read"
} | ConvertTo-Json
$bobTok = Invoke-RestMethod -Method Post -Uri "$base/api/v1/auth/token" -ContentType "application/json" -Body $bobBody
$bobAccounts = Invoke-RestMethod -Uri "$base/api/v1/customer/accounts" -Headers (BearerHeaders $bobTok.access_token)
$bobsId = $bobAccounts[0].id
try {
    Invoke-RestMethod -Uri "$base/api/v1/customer/accounts/$bobsId" -Headers (BearerHeaders $aliceTok.access_token) | Out-Null
    Write-Host "  [FAIL] Alice was able to read Bob's account (IDOR)" -ForegroundColor Red
}
catch {
    if ($_.Exception.Response.StatusCode.value__ -eq 403) {
        Ok "IDOR blocked: Alice cannot read Bob's account (403)"
    }
    else { throw }
}

# ---------------------------------------------------------------------
# Partner surface - client_credentials + payment initiation (idempotent)
# ---------------------------------------------------------------------
Info "3. Partner surface"
$pbody = @{
    grant_type    = "client_credentials"
    client_id     = "acme-treasury-client"
    client_secret = "PartnerSecret!ExampleOnly"
    audience      = "ntsf-partner-api"
    scope         = "partner.payments.initiate partner.payments.read"
} | ConvertTo-Json
$ptok = Invoke-RestMethod -Method Post -Uri "$base/api/v1/auth/token" -ContentType "application/json" -Body $pbody
Ok "Partner ACME-TREASURY obtained access token"

$idem = "demo-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
$payment = @{
    externalReference     = $idem
    debtorAccountNumber   = "NTSF-0001-ALICE"
    creditorAccountNumber = "PARTNER-9999"
    amountMinorUnits      = 12500
    currency              = "USD"
} | ConvertTo-Json
$phdr = BearerHeaders $ptok.access_token @{
    "X-Client-Cert-Thumbprint" = "AA11BB22CC33DD44EE55FF66AA11BB22CC33DD44"
    "Idempotency-Key"          = $idem
}
$p1 = Invoke-RestMethod -Method Post -Uri "$base/api/v1/partner/payments" -Headers $phdr -ContentType "application/json" -Body $payment
Ok "Payment initiated: $($p1.id) status=$($p1.status)"

$p2 = Invoke-RestMethod -Method Post -Uri "$base/api/v1/partner/payments" -Headers $phdr -ContentType "application/json" -Body $payment
if ($p1.id -eq $p2.id) { Ok "Idempotency: replay returned the same payment id" }
else { Write-Host "  [FAIL] Idempotency broke; got a new id on replay" -ForegroundColor Red }

# ---------------------------------------------------------------------
# Admin surface - step-up + explainable authz + audit chain verify
# ---------------------------------------------------------------------
Info "4. Admin surface"
$abody = @{
    grant_type = "password"
    subject    = "admin"
    password   = "AdminPassw0rd!"
    audience   = "ntsf-admin-api"
    scope      = "admin.audit admin.users admin.authz.evaluate"
    amr        = "mfa"
    acr        = "urn:ntsf:acr:step-up"
} | ConvertTo-Json
$atok = Invoke-RestMethod -Method Post -Uri "$base/api/v1/auth/token" -ContentType "application/json" -Body $abody
Ok "Admin obtained step-up token"

$adminH = BearerHeaders $atok.access_token
$audit = Invoke-RestMethod -Uri "$base/api/v1/admin/audit?pageSize=5" -Headers $adminH
Ok "Audit contains $($audit.items.Count) recent record(s) of $($audit.totalCount) total"

$verify = Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/audit/verify-chain" -Headers $adminH
if ($verify.ok) { Ok "Audit hash chain verifies clean" }
else { Write-Host "  [FAIL] Audit chain broken at $($verify.failingRow)" -ForegroundColor Red }

# Explainable authz - evaluate what Alice's token would allow on her own account
$aliceOwnId = $accounts[0].id
$explainBody = @{
    Token    = $aliceTok.access_token
    Action   = "customer:read-account"
    Resource = $aliceOwnId
} | ConvertTo-Json
$explain = Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/authz/evaluate" -Headers $adminH -ContentType "application/json" -Body $explainBody
Ok "PDP: allowed=$($explain.allowed) deciding=$($explain.decidingRequirement) reason=$($explain.reason)"

# API-key migration report
$report = Invoke-RestMethod -Uri "$base/api/v1/admin/api-keys/migration-report" -Headers $adminH
Ok "Migration report: $($report.rows.Count) key(s) tracked"

# Key rotation demo (produces two active JWKS keys)
$rotation = Invoke-RestMethod -Method Post -Uri "$base/api/v1/admin/keys/rotate" -Headers $adminH
Ok "Rotation performed: JWKS now advertises $($rotation.keyCount) key(s)"
$jwks2 = Invoke-RestMethod "$base/.well-known/jwks.json"
Ok "JWKS now shows $(($jwks2.keys).Count) key(s) - old primary still verifiable for its window"

Info "Demo complete."
