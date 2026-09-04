Set-StrictMode -Version Latest

function Get-Percentile {
    param(
        [Parameter(Mandatory)]
        [double[]] $Values,
        [Parameter(Mandatory)]
        [ValidateRange(0, 1)]
        [double] $Percentile
    )

    if ($Values.Count -eq 0) {
        return 0
    }

    $sorted = @($Values | Sort-Object)
    $index = [Math]::Max(0, [Math]::Ceiling($sorted.Count * $Percentile) - 1)
    return [double]$sorted[$index]
}

function New-CanaryMetrics {
    param(
        [Parameter(Mandatory)]
        [int[]] $StatusCodes,
        [Parameter(Mandatory)]
        [double[]] $LatenciesMs
    )

    if ($StatusCodes.Count -eq 0 -or $StatusCodes.Count -ne $LatenciesMs.Count) {
        return $null
    }

    $errors = @($StatusCodes | Where-Object { $_ -ge 500 }).Count
    [pscustomobject]@{
        SampleCount  = $StatusCodes.Count
        ErrorRate    = $errors / $StatusCodes.Count
        P95LatencyMs = Get-Percentile -Values $LatenciesMs -Percentile 0.95
    }
}

function Test-CanaryGate {
    param(
        [AllowNull()]
        $Metrics,
        [double] $MaximumErrorRate = 0.02,
        [double] $MaximumP95LatencyMs = 500,
        [int] $MinimumSampleCount = 20
    )

    if ($null -eq $Metrics) {
        return [pscustomobject]@{ Decision = 'Rollback'; Reason = 'Metrics are missing.' }
    }

    if ($Metrics.SampleCount -lt $MinimumSampleCount) {
        return [pscustomobject]@{
            Decision = 'Rollback'
            Reason   = "Only $($Metrics.SampleCount) samples were available; $MinimumSampleCount are required."
        }
    }

    if ($Metrics.ErrorRate -gt $MaximumErrorRate) {
        return [pscustomobject]@{
            Decision = 'Rollback'
            Reason   = "Error rate $([Math]::Round($Metrics.ErrorRate * 100, 2))% breached the gate."
        }
    }

    if ($Metrics.P95LatencyMs -gt $MaximumP95LatencyMs) {
        return [pscustomobject]@{
            Decision = 'Rollback'
            Reason   = "P95 latency $([Math]::Round($Metrics.P95LatencyMs, 1)) ms breached the gate."
        }
    }

    return [pscustomobject]@{
        Decision = 'Promote'
        Reason   = 'Error-rate and latency gates are healthy.'
    }
}

Export-ModuleMember -Function Get-Percentile, New-CanaryMetrics, Test-CanaryGate
