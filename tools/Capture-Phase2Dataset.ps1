param(
    [ValidateRange(1, 360)]
    [int]$DurationMinutes = 10,

    [ValidateRange(4, 6)]
    [double]$FramesPerSecond = 5,

    [string]$OutputDirectory = "",

    [ValidateRange(1, 6)]
    [int]$EncoderWorkers = 3
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$portableDirectory = Join-Path `
    $repositoryRoot `
    "artifacts\CurrencyWarsAssistant-0.2.733-win-x64-portable"
$executable = Join-Path $portableDirectory "CurrencyWarsAssistant.App.exe"
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Portable test executable was not found: $executable"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path `
        $env:LOCALAPPDATA `
        "CurrencyWarsAssistant\datasets\dataset-$timestamp"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$durationSeconds = $DurationMinutes * 60
$outputRoot = [System.IO.Path]::GetPathRoot($OutputDirectory)
$driveName = $outputRoot.TrimEnd('\').TrimEnd(':')
$drive = Get-PSDrive -Name $driveName -ErrorAction Stop
$estimatedBytes = $DurationMinutes * 1.2GB
if ($drive.Free -lt ($estimatedBytes * 1.15)) {
    throw "Insufficient free space. Estimated need: $([Math]::Round($estimatedBytes / 1GB, 1)) GB; free: $([Math]::Round($drive.Free / 1GB, 1)) GB."
}

Write-Host "Currency Wars dataset capture is starting."
Write-Host "Output: $OutputDirectory"
Write-Host "Target: $FramesPerSecond FPS for $DurationMinutes minute(s)."
Write-Host "Read-only window capture: no input and no game-memory access."
Write-Warning "Lossless 1440p PNG can use about 1 GB per minute. Check free disk space first."

$arguments = @(
    "--phase2-capture-dataset",
    "--output", ('"{0}"' -f $OutputDirectory),
    "--duration-seconds", $durationSeconds.ToString(),
    "--fps", $FramesPerSecond.ToString(
        [System.Globalization.CultureInfo]::InvariantCulture),
    "--encoder-workers", $EncoderWorkers.ToString()
)
$process = Start-Process `
    -FilePath $executable `
    -ArgumentList $arguments `
    -WindowStyle Hidden `
    -Wait `
    -PassThru

if ($process.ExitCode -ne 0) {
    $errorPath = Join-Path $OutputDirectory "capture-error.txt"
    if (Test-Path -LiteralPath $errorPath) {
        Get-Content -LiteralPath $errorPath
    }
    throw "Dataset capture failed with exit code $($process.ExitCode)."
}

$reportPath = Join-Path $OutputDirectory "capture-report.json"
if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
    throw "Capture exited without producing a report: $reportPath"
}

$report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 |
    ConvertFrom-Json
Write-Host "Completed: $($report.successfulFrames) frames," `
    "$([Math]::Round($report.actualFramesPerSecond, 2)) actual FPS," `
    "$($report.failedFrames) failures."
Write-Host "Maximum frame interval: $([Math]::Round($report.maximumIntervalMilliseconds, 1)) ms"
Write-Host "Report: $reportPath"
