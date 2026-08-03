param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePackage,

    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}

$sourceRoot = [System.IO.Path]::GetFullPath($SourcePackage)
$destinationRoot = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'data\4.4\phase2-icon-assets'))
$manifestPath = Join-Path $sourceRoot 'manifest.jsonl'

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Asset package manifest was not found: $manifestPath"
}

$sourcePrefix = $sourceRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$destinationPrefix = $destinationRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$records = @(Get-Content -LiteralPath $manifestPath -Encoding UTF8 | ForEach-Object {
    $_ | ConvertFrom-Json
})

if ($records.Count -eq 0) {
    throw 'Asset package manifest contains no records.'
}

$duplicateIds = @($records | Group-Object id | Where-Object Count -gt 1)
if ($duplicateIds.Count -gt 0) {
    throw "Asset package contains duplicate IDs: $($duplicateIds.Name -join ', ')"
}

[System.IO.Directory]::CreateDirectory($destinationRoot) | Out-Null
$sanitizedLines = [System.Collections.Generic.List[string]]::new()

foreach ($record in $records) {
    if (-not $record.available) {
        continue
    }

    $relativePath = [string]$record.standardized_path
    if ([string]::IsNullOrWhiteSpace($relativePath) -or [System.IO.Path]::IsPathRooted($relativePath)) {
        throw "Asset $($record.id) has an invalid standardized path."
    }

    $relativeWindowsPath = $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $sourcePath = [System.IO.Path]::GetFullPath((Join-Path $sourceRoot $relativeWindowsPath))
    $destinationPath = [System.IO.Path]::GetFullPath((Join-Path $destinationRoot $relativeWindowsPath))
    if (-not $sourcePath.StartsWith($sourcePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Asset $($record.id) escapes the source package root."
    }
    if (-not $destinationPath.StartsWith($destinationPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Asset $($record.id) escapes the destination root."
    }
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Asset file was not found: $sourcePath"
    }

    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $destinationPath)) | Out-Null
    if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
        $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash
        if ($sourceHash -ne $destinationHash) {
            throw "Refusing to overwrite a different project asset: $destinationPath"
        }
    }
    else {
        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath
    }

    $sanitized = [ordered]@{}
    foreach ($property in $record.PSObject.Properties) {
        if ($property.Name -in @('local_source_path', 'raw_path', 'composed_from')) {
            continue
        }

        $sanitized[$property.Name] = $property.Value
    }
    $sanitized['project_relative_path'] = ('data/4.4/phase2-icon-assets/' + $relativePath)
    $sanitizedLines.Add(($sanitized | ConvertTo-Json -Depth 12 -Compress))
}

$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllLines(
    (Join-Path $destinationRoot 'asset-manifest.jsonl'),
    $sanitizedLines,
    $utf8WithoutBom)

$supportFiles = @{
    'coverage_report.md' = 'source-coverage-report.md'
    'missing_or_ambiguous.csv' = 'source-missing-or-ambiguous.csv'
    'taxonomy.json' = 'source-taxonomy.json'
    'validation.json' = 'source-validation.json'
}

foreach ($entry in $supportFiles.GetEnumerator()) {
    $sourcePath = Join-Path $sourceRoot $entry.Key
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Required package metadata was not found: $sourcePath"
    }

    $destinationPath = Join-Path $destinationRoot $entry.Value
    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
}

Write-Output "Imported $($sanitizedLines.Count) asset records into $destinationRoot"
