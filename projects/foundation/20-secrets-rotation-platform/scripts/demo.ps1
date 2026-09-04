param(
    [string]$BaseUrl = "http://localhost:5020"
)

$ErrorActionPreference = "Stop"
$suffix = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

function Invoke-Api {
    param(
        [string]$Method,
        [string]$Path,
        [object]$Body,
        [string]$Token
    )
    $headers = @{}
    if ($Token) { $headers.Authorization = "Bearer $Token" }
    $parameters = @{
        Method = $Method
        Uri = "$BaseUrl$Path"
        Headers = $headers
        ContentType = "application/json"
    }
    if ($null -ne $Body) { $parameters.Body = ($Body | ConvertTo-Json -Depth 10) }
    Invoke-RestMethod @parameters
}

Write-Host "1. Obtain a local-only demonstration token"
$tokenResponse = Invoke-Api POST "/api/v1/auth/token" @{
    subject = "demo-admin"
    scopes = @("secrets.manage", "secrets.read", "secrets.rotate", "secrets.ack", "secrets.breakglass", "secrets.approve")
} $null
$token = $tokenResponse.accessToken

Write-Host "2. Register a synthetic consumer"
$consumer = Invoke-Api POST "/api/v1/consumers" @{
    name = "demo-consumer-$suffix"
    application = "demo-app-$suffix"
    webhookUrl = "https://demo-webhook.invalid/consumer"
    email = "demo-consumer@example.invalid"
} $token

Write-Host "3. Register a secret and its rotation schedule"
$secretName = "demo-app-$suffix/prod/database"
$secret = Invoke-Api POST "/api/v1/secrets" @{
    name = $secretName
    type = "DatabasePassword"
    ownerTeam = "Northstar Platform Engineering (fictional)"
    environment = "prod"
    criticality = "High"
    tags = @("synthetic", "demo")
    description = "Runtime-generated demo credential."
    rotationIntervalHours = 24
    maxAgeHours = 72
    gracePeriodHours = 4
    consumerIds = @($consumer.id)
} $token

Write-Host "4. Request a dual-write rotation; it pauses for acknowledgement"
$rotation = Invoke-Api POST "/api/v1/rotations" @{
    secretName = $secretName
    strategy = "DualWrite"
    idempotencyKey = "demo-success-$suffix"
    maintenanceWindowStart = $null
} $token
$rotation | ConvertTo-Json -Depth 10

Write-Host "5. Consumer pulls the notice, refreshes the version, and acknowledges"
$pending = Invoke-Api GET "/api/v1/consumers/$($consumer.id)/pending" $null $token
$completed = Invoke-Api POST "/api/v1/consumers/$($consumer.id)/rotations/$($rotation.id)/acknowledge" $null $token
$completed | ConvertTo-Json -Depth 10

Write-Host "6. Register a synthetic failure case and prove automatic rollback"
$failureName = "demo-failure-$suffix/prod/database"
Invoke-Api POST "/api/v1/secrets" @{
    name = $failureName
    type = "DatabasePassword"
    ownerTeam = "Northstar Platform Engineering (fictional)"
    environment = "prod"
    criticality = "Critical"
    tags = @("synthetic", "verification-fail")
    description = "Forces the deterministic verifier to fail."
    rotationIntervalHours = 24
    maxAgeHours = 72
    gracePeriodHours = 4
} $token | Out-Null
$rolledBack = Invoke-Api POST "/api/v1/rotations" @{
    secretName = $failureName
    strategy = "DualWrite"
    idempotencyKey = "demo-failure-$suffix"
    maintenanceWindowStart = $null
} $token
$rolledBack | ConvertTo-Json -Depth 10

if ($completed.state -ne "Completed" -or $rolledBack.state -ne "RolledBack") {
    throw "Demo did not reach the expected terminal states."
}

Write-Host "Demo completed: one successful rotation and one verified rollback."
