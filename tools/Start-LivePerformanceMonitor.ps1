[CmdletBinding()]
param(
    [Parameter()]
    [ValidateRange(1, 60)]
    [int] $IntervalSeconds = 2,

    [Parameter()]
    [ValidateRange(1, 1440)]
    [int] $DurationMinutes = 30,

    [Parameter()]
    [ValidateRange(0, 100000)]
    [int] $SampleCount = 0,

    [Parameter()]
    [string] $OutputDirectory = (Join-Path $env:LOCALAPPDATA 'CurrencyWarsAssistant\live-monitoring'),

    [Parameter()]
    [string[]] $ProcessNames = @('StarRail', 'StarRailBase', 'CurrencyWarsAssistant.App', 'obs64', 'livehime')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
}

$resolvedOutput = (Resolve-Path -LiteralPath $OutputDirectory).Path
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputFile = Join-Path $resolvedOutput "live-performance-$timestamp.csv"
$endAt = (Get-Date).AddMinutes($DurationMinutes)
$samplesTaken = 0

Write-Host "Monitoring until $endAt. Press Ctrl+C to stop early."
Write-Host "Output: $outputFile"

while ((Get-Date) -lt $endAt -and
       ($SampleCount -eq 0 -or $samplesTaken -lt $SampleCount)) {
    $os = Get-CimInstance Win32_OperatingSystem
    $systemCpu = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
    $availableMemoryMb = [math]::Round($os.FreePhysicalMemory / 1KB, 1)
    $timestampUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $matchedAny = $false

    foreach ($processName in $ProcessNames) {
        foreach ($process in @(Get-Process -Name $processName -ErrorAction SilentlyContinue)) {
            $matchedAny = $true
            [pscustomobject]@{
                TimestampUtc = $timestampUtc
                SystemCpuPercent = $systemCpu
                AvailableMemoryMb = $availableMemoryMb
                ProcessName = $process.ProcessName
                ProcessId = $process.Id
                WorkingSetMb = [math]::Round($process.WorkingSet64 / 1MB, 1)
                PrivateMemoryMb = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)
                TotalCpuSeconds = [math]::Round($process.CPU, 2)
                ThreadCount = $process.Threads.Count
                Responding = $process.Responding
            } | Export-Csv -LiteralPath $outputFile -Append -NoTypeInformation -Encoding UTF8
        }
    }

    if (-not $matchedAny) {
        [pscustomobject]@{
            TimestampUtc = $timestampUtc
            SystemCpuPercent = $systemCpu
            AvailableMemoryMb = $availableMemoryMb
            ProcessName = '(none)'
            ProcessId = $null
            WorkingSetMb = $null
            PrivateMemoryMb = $null
            TotalCpuSeconds = $null
            ThreadCount = $null
            Responding = $null
        } | Export-Csv -LiteralPath $outputFile -Append -NoTypeInformation -Encoding UTF8
    }

    $samplesTaken++
    if ($SampleCount -gt 0 -and $samplesTaken -ge $SampleCount) {
        break
    }

    Start-Sleep -Seconds $IntervalSeconds
}

Write-Host "Monitoring completed: $outputFile"
