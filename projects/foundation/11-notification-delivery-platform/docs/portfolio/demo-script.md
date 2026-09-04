# Portfolio — Demo script

Run these steps to walk a reviewer or interviewer through the system in
about eight minutes.

## 0. Prepare

```powershell
cd C:\Users\rukwaropaul\Downloads\DEV\Projects\11-notification-delivery-platform
dotnet run --project src\NotificationPlatform.Api -c Release
```

The API is at `http://localhost:5011`. Open Swagger at
`http://localhost:5011/swagger` and the ops page at `/ops`.

## 1. Issue a dev token

```powershell
$body = @{ tenantId = "11111111-1111-1111-1111-111111111111" } | ConvertTo-Json
$token = (Invoke-RestMethod -Method Post -Uri http://localhost:5011/api/v1/auth/token `
    -ContentType 'application/json' -Body $body).access_token
$headers = @{ Authorization = "Bearer $token" }
```

## 2. Send a transactional email

```powershell
$req = @{
  templateKey = "order.confirmation"; channel = "Email"
  recipientExternalId = "cust-001"
  payload = @{ user=@{firstName="Alice"}; order=@{id="SO-1042"; item="Coffee"; total="KES 2,500"} }
  priority = "Transactional"; category = "Transactional"
} | ConvertTo-Json -Depth 5
Invoke-RestMethod -Method Post -Uri http://localhost:5011/api/v1/notifications `
    -Headers $headers -ContentType 'application/json' -Body $req
```

Show that the response is `Accepted` and includes a `notificationId`.

## 3. Show the notification and its attempts

```powershell
Invoke-RestMethod -Uri "http://localhost:5011/api/v1/notifications" -Headers $headers | ConvertTo-Json -Depth 6
```

## 4. Demonstrate idempotency

Repeat the send with `idempotencyKey = "demo-1"`. First call is accepted;
second call returns `IdempotentReplay` with the same `notificationId`.

## 5. Demonstrate quiet hours

Send a Marketing email to `cust-001` inside their quiet hours; the outcome
is `Accepted` but the notification is `Scheduled` with a future
`nextAttemptAt`.

## 6. Demonstrate quota

Show the tenant's `MonthlyQuotaHard` value; issue enough messages to trip
it; the API returns `422 Unprocessable Entity` with
`monthly_quota_hard_limit`.

## 7. Demonstrate provider failover

Configure the primary provider simulator to hard-fail via config; send a
message; watch the pipeline fail over to the secondary. Delivery still
succeeds and the delivery receipt lands on the notification.

## 8. Show the DLQ

Push a small burst of unroutable sends until they DLQ; open
`GET /api/v1/admin/dlq` in Swagger; replay one and show it transitions
back to `Queued`.

## 9. Ops dashboard

Show `/ops` — queue depth, provider health, DLQ depth, recent sends.

## 10. Tests

```powershell
dotnet test -c Release
```

Point out the fairness test, the circuit-breaker test, the HMAC replay
test, and the strict-mode template render test.
