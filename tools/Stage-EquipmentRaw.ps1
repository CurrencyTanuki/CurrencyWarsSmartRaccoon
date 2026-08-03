param(
    [Parameter(Mandatory = $false)]
    [string]$SourceRecords,

    [Parameter(Mandatory = $false)]
    [string]$SourceIconManifest,

    [Parameter(Mandatory = $false)]
    [string]$SourceAssetRoot,

    [Parameter(Mandatory = $false)]
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'

function Get-Sha256Text {
    param([Parameter(Mandatory = $true)][string]$Text)

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        return ([System.BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Write-DeterministicJson {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$Depth = 12
    )

    $encoding = [System.Text.UTF8Encoding]::new($false)
    $json = $Value | ConvertTo-Json -Depth $Depth
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, $encoding)
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($SourceRecords) -or
    [string]::IsNullOrWhiteSpace($SourceIconManifest)) {
    $candidates = @(
        Get-ChildItem -LiteralPath $repositoryRoot -Directory |
            Where-Object {
                (Test-Path -LiteralPath (
                    Join-Path $_.FullName 'research_cache\parsed_equipment_dataset.json') -PathType Leaf) -and
                (Test-Path -LiteralPath (
                    Join-Path $_.FullName 'assets\currency_wars_equipment_icons\manifest.json') -PathType Leaf)
            }
    )
    if ($candidates.Count -ne 1) {
        throw 'Could not uniquely discover the upstream equipment handoff. Pass both source paths explicitly.'
    }

    $SourceRecords = Join-Path $candidates[0].FullName 'research_cache\parsed_equipment_dataset.json'
    $SourceIconManifest = Join-Path $candidates[0].FullName 'assets\currency_wars_equipment_icons\manifest.json'
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot 'data\raw\4.4\equipment'
}

$resolvedRecords = [System.IO.Path]::GetFullPath($SourceRecords)
$resolvedManifest = [System.IO.Path]::GetFullPath($SourceIconManifest)
$resolvedSourceAssetRoot = if ([string]::IsNullOrWhiteSpace($SourceAssetRoot)) {
    [System.IO.Path]::GetFullPath((
        Split-Path -Parent (Split-Path -Parent $resolvedRecords)))
}
else {
    [System.IO.Path]::GetFullPath($SourceAssetRoot)
}
$resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$legacyRuntime = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'data\4.4'))

foreach ($sourcePath in @($resolvedRecords, $resolvedManifest)) {
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Source file does not exist: $sourcePath"
    }
}

if ($resolvedOutputRoot.Equals($legacyRuntime, [System.StringComparison]::OrdinalIgnoreCase) -or
    $resolvedOutputRoot.StartsWith(
        $legacyRuntime + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to stage raw data inside legacy runtime directory: $legacyRuntime"
}

$recordsHashBefore = (Get-FileHash -LiteralPath $resolvedRecords -Algorithm SHA256).Hash
$manifestHashBefore = (Get-FileHash -LiteralPath $resolvedManifest -Algorithm SHA256).Hash
$snapshotIdentity = Get-Sha256Text (
    "schema=1.0.0`ngame=4.4`nassets=embedded`nrecords=$recordsHashBefore`nmanifest=$manifestHashBefore")
$snapshotName = $snapshotIdentity.Substring(0, 16).ToLowerInvariant()
$targetDirectory = Join-Path $resolvedOutputRoot $snapshotName

[System.IO.Directory]::CreateDirectory($resolvedOutputRoot) | Out-Null
$stagingDirectory = Join-Path $resolvedOutputRoot (
    '.staging-' + $PID + '-' + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($stagingDirectory) | Out-Null

try {
    $stagedRecords = Join-Path $stagingDirectory 'records.json'
    $stagedManifest = Join-Path $stagingDirectory 'icon-manifest.json'
    [System.IO.File]::Copy($resolvedRecords, $stagedRecords, $false)
    [System.IO.File]::Copy($resolvedManifest, $stagedManifest, $false)

    $iconManifest = Get-Content -LiteralPath $resolvedManifest -Raw -Encoding UTF8 |
        ConvertFrom-Json
    $assetRootPrefix = $resolvedSourceAssetRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    foreach ($entry in @($iconManifest.records)) {
        $relativeAssetPath = [string]$entry.local_path
        if ([System.IO.Path]::IsPathRooted($relativeAssetPath)) {
            throw "Icon asset path must be relative: $relativeAssetPath"
        }
        $sourceAssetPath = [System.IO.Path]::GetFullPath((
            Join-Path $resolvedSourceAssetRoot $relativeAssetPath))
        if (-not $sourceAssetPath.StartsWith(
                $assetRootPrefix,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $sourceAssetPath -PathType Leaf)) {
            throw "Icon asset is missing or escapes the source root: $relativeAssetPath"
        }
        if ((Get-FileHash -LiteralPath $sourceAssetPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne
            ([string]$entry.sha256).ToLowerInvariant()) {
            throw "Icon asset hash mismatch while staging: $relativeAssetPath"
        }
        if ((Get-Item -LiteralPath $sourceAssetPath).Length -ne [int64]$entry.bytes) {
            throw "Icon asset length mismatch while staging: $relativeAssetPath"
        }

        $destinationAssetPath = Join-Path $stagingDirectory $relativeAssetPath
        [System.IO.Directory]::CreateDirectory(
            (Split-Path -Parent $destinationAssetPath)) | Out-Null
        [System.IO.File]::Copy($sourceAssetPath, $destinationAssetPath, $false)
    }

    $recordsHashAfter = (Get-FileHash -LiteralPath $resolvedRecords -Algorithm SHA256).Hash
    $manifestHashAfter = (Get-FileHash -LiteralPath $resolvedManifest -Algorithm SHA256).Hash
    if ($recordsHashAfter -ne $recordsHashBefore -or
        $manifestHashAfter -ne $manifestHashBefore) {
        throw 'Upstream equipment files changed while staging. No raw snapshot was published.'
    }

    $package = [ordered]@{
        schema_version = '1.0.0'
        game_version = '4.4'
        dataset = 'equipment'
        inputs = [ordered]@{
            records = [ordered]@{
                file = 'records.json'
                sha256 = $recordsHashBefore
            }
            icon_manifest = [ordered]@{
                file = 'icon-manifest.json'
                sha256 = $manifestHashBefore
            }
        }
    }
    Write-DeterministicJson -Value $package -Path (
        Join-Path $stagingDirectory 'package.json') -Depth 8

    if (Test-Path -LiteralPath $targetDirectory -PathType Container) {
        foreach ($fileName in @('records.json', 'icon-manifest.json', 'package.json')) {
            $existingPath = Join-Path $targetDirectory $fileName
            $stagedPath = Join-Path $stagingDirectory $fileName
            if (-not (Test-Path -LiteralPath $existingPath -PathType Leaf) -or
                (Get-FileHash -LiteralPath $existingPath -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash) {
                throw "Existing content-addressed raw snapshot differs: $targetDirectory"
            }
        }
        foreach ($entry in @($iconManifest.records)) {
            $existingAssetPath = Join-Path $targetDirectory ([string]$entry.local_path)
            if (-not (Test-Path -LiteralPath $existingAssetPath -PathType Leaf) -or
                (Get-FileHash -LiteralPath $existingAssetPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne
                ([string]$entry.sha256).ToLowerInvariant()) {
                throw "Existing raw snapshot has a missing or changed asset: $existingAssetPath"
            }
        }
    }
    else {
        Move-Item -LiteralPath $stagingDirectory -Destination $targetDirectory
        $stagingDirectory = $null
    }
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($stagingDirectory) -and
        (Test-Path -LiteralPath $stagingDirectory -PathType Container)) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}

[pscustomobject]@{
    schema_version = '1.0.0'
    game_version = '4.4'
    dataset = 'equipment'
    raw_directory = $targetDirectory
    records_sha256 = $recordsHashBefore
    icon_manifest_sha256 = $manifestHashBefore
}
