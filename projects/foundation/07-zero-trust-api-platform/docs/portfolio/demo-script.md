# Demo Script (10-minute walkthrough)

Audience: hiring manager or client architecture lead. Keeps the terminal on one side and
the code on the other. Every step maps to a specific file so you can pivot to code the
moment they ask "how?".

---

## 0. Setup (30s)

```powershell
cd 07-zero-trust-api-platform
dotnet build -c Release
dotnet test  -c Release   # green summary — show the numbers
dotnet run --project src\ZeroTrust.Api
# in a second shell
.\scripts\demo.ps1
```

Say: *"One process, three surfaces, port 5007. No Docker, no Postgres. Everything you're
about to see runs from `dotnet run`."*

## 1. Three surfaces on one host (60s)

Open `Program.cs`. Point at:
- `AddPolicyScheme("MultiScheme", ...)` — auto-picks ApiKey vs JwtBearer by header.
- Named policies: `customer.read`, `partner.payments.initiate`, `admin.audit`.

Say: *"Each surface has its own audience, scope, role and step-up requirement. This is
not `[Authorize]` scattered through controllers — it's declarative and it composes."*

## 2. Customer surface (2 min)

Show a request:

```powershell
$body = @{ grant_type="password"; subject="alice"; password="CustomerPassw0rd!"; audience="ntsf-customer-api"; scope="customer.read customer.write" } | ConvertTo-Json
$tok = Invoke-RestMethod -Method Post -Uri http://localhost:5007/api/v1/auth/token -ContentType "application/json" -Body $body
Invoke-RestMethod -Uri http://localhost:5007/api/v1/customer/accounts -Headers @{ Authorization = "Bearer $($tok.access_token)" }
```

Then IDOR check:

```powershell
# Alice tries to read Bob's account
# → 403, audited, principal never touches Bob's data
```

Pivot to `AccountOwnerHandler` — resource-typed authorization requirement. Point at the
test `Alice_Cannot_Read_Bobs_Account_By_Guessing_Id`.

## 3. Partner surface (2 min)

Show client-credentials + all the checks that stack up:

```powershell
$pbody = @{ grant_type="client_credentials"; client_id="acme-treasury-client"; client_secret="PartnerSecret!ExampleOnly"; audience="ntsf-partner-api"; scope="partner.payments.initiate" } | ConvertTo-Json
$ptok = Invoke-RestMethod -Method Post -Uri http://localhost:5007/api/v1/auth/token -ContentType "application/json" -Body $pbody

Invoke-RestMethod -Method Post -Uri http://localhost:5007/api/v1/partner/payments `
  -Headers @{ Authorization = "Bearer $($ptok.access_token)"; "X-Client-Cert-Thumbprint" = "AA11BB22CC33DD44EE55FF66AA11BB22CC33DD44"; "Idempotency-Key" = "demo-ext-1" } `
  -ContentType "application/json" `
  -Body (@{ externalReference="demo-ext-1"; debtorAccountNumber="NTSF-0001-ALICE"; creditorAccountNumber="PARTNER-9999"; amountMinorUnits=12500; currency="USD" } | ConvertTo-Json)
```

Say: *"The stack that ran: audience check (partner), scope (`partner.payments.initiate`),
IP allow-list, `X-Client-Cert-Thumbprint` match, per-partner rate limit, then the
endpoint. Missing the cert header → 401. Wrong scope → 403. Idempotency-Key replay →
returns the original result."*

Point to the ADR-005 honesty on mTLS.

## 4. Admin surface + explainable authz (2 min)

Get a step-up admin token, then hit the PDP:

```powershell
$abody = @{ grant_type="password"; subject="admin"; password="AdminPassw0rd!"; audience="ntsf-admin-api"; scope="admin.audit admin.users admin.authz.evaluate"; amr="mfa"; acr="urn:ntsf:acr:step-up" } | ConvertTo-Json
$atok = Invoke-RestMethod -Method Post -Uri http://localhost:5007/api/v1/auth/token -ContentType "application/json" -Body $abody

Invoke-RestMethod -Method Post -Uri http://localhost:5007/api/v1/admin/authz/evaluate `
  -ContentType "application/json" `
  -Headers @{ Authorization = "Bearer $($atok.access_token)" } `
  -Body (@{ token = $tok.access_token; action = "customer:read-account"; resource = "<some account guid>" } | ConvertTo-Json)
# Response includes: allowed, decidingRequirement, why
```

Say: *"You can ask the system 'would principal X be allowed to do Y on Z, and why?'.
Auditors love this."*

## 5. Key rotation (2 min)

```powershell
Invoke-RestMethod http://localhost:5007/.well-known/jwks.json
Invoke-RestMethod -Method Post -Uri http://localhost:5007/api/v1/admin/keys/rotate -Headers @{ Authorization = "Bearer $($atok.access_token)" }
Invoke-RestMethod http://localhost:5007/.well-known/jwks.json   # 2 keys now
# The customer token from step 2 still works — signed with previous key.
Invoke-RestMethod -Uri http://localhost:5007/api/v1/customer/accounts -Headers @{ Authorization = "Bearer $($tok.access_token)" }
```

Say: *"Two active keys means zero-downtime rotation."*

## 6. Audit hash chain (1 min)

```powershell
Invoke-RestMethod -Uri http://localhost:5007/api/v1/admin/audit/verify-chain -Headers @{ Authorization = "Bearer $($atok.access_token)" }
```

Say: *"Every allow AND deny you just saw was chained. If someone edited a row, this
endpoint would tell us the first tampered sequence number."*

## 7. Close (30s)

- `docs/security/threat-model.md` — 20 STRIDE threats mapped to code files.
- `docs/decisions/` — 5 ADRs.
- `docs/runbooks/` — key rotation, revoke a partner, break-glass, api-key cutover.
- `dotnet test -c Release` → all pass. Numbers in `docs/test-results.md`.

*"Any question about how any of that works, I can pivot to the code in 10 seconds."*
