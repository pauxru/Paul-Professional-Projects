[CmdletBinding()]
param(
    [string] $BaseUrl = 'http://localhost:5028'
)

$ErrorActionPreference = 'Stop'
$live = Invoke-RestMethod "$BaseUrl/health/live"
$ready = Invoke-RestMethod "$BaseUrl/health/ready"
$startup = Invoke-RestMethod "$BaseUrl/health/startup"

if ($live.status -ne 'Healthy' -or
    $ready.status -ne 'Healthy' -or
    $startup.status -ne 'Healthy') {
    throw 'One or more health probes are not healthy.'
}

$metrics = Invoke-WebRequest "$BaseUrl/metrics"
if ($metrics.Content -notmatch 'storefront_requests_total') {
    throw 'Metrics endpoint did not return the expected metric.'
}

Write-Host "Smoke test passed for $BaseUrl."
