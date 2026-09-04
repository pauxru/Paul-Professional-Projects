[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [Parameter(Mandatory)] [string] $ContainerApp,
    [Parameter(Mandatory)] [string] $CandidateRevision,
    [Parameter(Mandatory)] [string] $StableRevision,
    [int[]] $TrafficSteps = @(5, 20, 50, 100),
    [double] $MaximumErrorRate = 0.02,
    [double] $MaximumP95LatencyMs = 500,
    [int] $MinimumSampleCount = 20
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CanaryGate.psm1') -Force

$fqdn = az containerapp show `
    --resource-group $ResourceGroup `
    --name $ContainerApp `
    --query properties.configuration.ingress.fqdn `
    --output tsv

if (-not $fqdn) {
    throw 'Container App FQDN could not be resolved.'
}

try {
    foreach ($candidateWeight in $TrafficSteps) {
        $stableWeight = 100 - $candidateWeight
        az containerapp ingress traffic set `
            --resource-group $ResourceGroup `
            --name $ContainerApp `
            --revision-weight "$StableRevision=$stableWeight" "$CandidateRevision=$candidateWeight" `
            --output none

        $ready = Invoke-RestMethod "https://$fqdn/health/ready" -TimeoutSec 15
        if ($ready.status -ne 'Healthy') {
            throw "Readiness gate failed at $candidateWeight% candidate traffic."
        }

        $prometheus = Invoke-RestMethod "https://$fqdn/metrics" -TimeoutSec 15
        $errorMatch = [regex]::Match($prometheus, '(?m)^storefront_error_rate\s+([0-9.]+)$')
        $latencyMatch = [regex]::Match($prometheus, '(?m)^storefront_request_duration_ms_p95\s+([0-9.]+)$')
        $sampleMatch = [regex]::Match($prometheus, '(?m)^storefront_requests_total\s+([0-9.]+)$')

        $metrics = if ($errorMatch.Success -and $latencyMatch.Success -and $sampleMatch.Success) {
            [pscustomobject]@{
                ErrorRate    = [double]::Parse($errorMatch.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
                P95LatencyMs = [double]::Parse($latencyMatch.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
                SampleCount  = [int][double]::Parse($sampleMatch.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
            }
        }

        $gate = Test-CanaryGate `
            -Metrics $metrics `
            -MaximumErrorRate $MaximumErrorRate `
            -MaximumP95LatencyMs $MaximumP95LatencyMs `
            -MinimumSampleCount $MinimumSampleCount

        Write-Host "Traffic $candidateWeight%: $($gate.Decision) — $($gate.Reason)"
        if ($gate.Decision -ne 'Promote') {
            throw $gate.Reason
        }
    }
}
catch {
    Write-Warning "Canary failed; routing 100% to $StableRevision."
    az containerapp ingress traffic set `
        --resource-group $ResourceGroup `
        --name $ContainerApp `
        --revision-weight "$StableRevision=100" "$CandidateRevision=0" `
        --output none
    throw
}
