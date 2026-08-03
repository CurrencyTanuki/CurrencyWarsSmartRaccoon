param(
    [Parameter(Mandatory = $false)]
    [string]$SourceDirectory,

    [Parameter(Mandatory = $false)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $candidates = @(
        Get-ChildItem -LiteralPath $repositoryRoot -Directory |
            Where-Object {
                $_.Name -like '*_4.4' -and
                @(Get-ChildItem -LiteralPath $_.FullName -Filter '*.md' -File).Count -eq 4
            }
    )
    if ($candidates.Count -ne 1) {
        throw "Could not uniquely discover the 4.4 report directory. Pass -SourceDirectory explicitly."
    }

    $SourceDirectory = $candidates[0].FullName
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\data\4.4'
}

$specifications = @(
    @{
        Output = 'investment-environments.json'
        ExpectedCount = 83
        Kind = 'investment_environments'
    },
    @{
        Output = 'investment-strategies.json'
        ExpectedCount = 334
        Kind = 'investment_strategies'
    },
    @{
        Output = 'enemy-affixes.json'
        ExpectedCount = 51
        Kind = 'enemy_affixes'
    },
    @{
        Output = 'competitors.json'
        ExpectedCount = 20
        Kind = 'competitors'
    }
)

$resolvedSource = [System.IO.Path]::GetFullPath($SourceDirectory)
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $resolvedSource -PathType Container)) {
    throw "Source directory does not exist: $resolvedSource"
}

[System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
$summary = [ordered]@{
    schema_version = 1
    game_version = '4.4'
    generated_from = '4.4 final markdown report'
    imported_at = [DateTimeOffset]::UtcNow.ToString('o')
    datasets = [ordered]@{}
}

foreach ($specification in $specifications) {
    $matchingFiles = @(
        Get-ChildItem -LiteralPath $resolvedSource -Filter '*.md' -File |
            Where-Object {
                $header = [System.IO.File]::ReadAllText(
                    $_.FullName,
                    [System.Text.Encoding]::UTF8)
                $header -match "(?m)^record_count:\s*$($specification.ExpectedCount)\s*$"
            }
    )
    if ($matchingFiles.Count -ne 1) {
        throw "Could not uniquely identify the source with record_count $($specification.ExpectedCount)."
    }

    $sourcePath = $matchingFiles[0].FullName
    $content = [System.IO.File]::ReadAllText($sourcePath, [System.Text.Encoding]::UTF8)
    $match = [regex]::Match(
        $content,
        '```jsonl\s*\r?\n(?<records>.*?)\r?\n```',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success) {
        throw "No JSONL block found in: $sourcePath"
    }

    $records = @(
        $match.Groups['records'].Value -split '\r?\n' |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { $_ | ConvertFrom-Json }
    )

    if ($records.Count -ne $specification.ExpectedCount) {
        throw "Unexpected record count in $sourcePath`: expected $($specification.ExpectedCount), actual $($records.Count)"
    }

    $duplicateIds = @(
        $records |
            Group-Object -Property id |
            Where-Object Count -gt 1 |
            Select-Object -ExpandProperty Name
    )
    if ($duplicateIds.Count -gt 0) {
        throw "Duplicate IDs in $sourcePath`: $($duplicateIds -join ', ')"
    }

    $duplicateNames = @(
        $records |
            Group-Object -Property name |
            Where-Object Count -gt 1 |
            Select-Object -ExpandProperty Name
    )
    if ($duplicateNames.Count -gt 0) {
        throw "Duplicate names in $sourcePath`: $($duplicateNames -join ', ')"
    }

    foreach ($record in $records) {
        if ([string]::IsNullOrWhiteSpace($record.id) -or
            [string]::IsNullOrWhiteSpace($record.name)) {
            throw "A record in $sourcePath is missing id or name."
        }
    }

    $outputPath = Join-Path $resolvedOutput $specification.Output
    $json = $records | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($outputPath, $json + [Environment]::NewLine, $utf8WithoutBom)

    $summary.datasets[$specification.Kind] = [ordered]@{
        file = $specification.Output
        count = $records.Count
        source_file = $matchingFiles[0].Name
    }
}

$metadataPath = Join-Path $resolvedOutput 'metadata.json'
$metadataJson = $summary | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText(
    $metadataPath,
    $metadataJson + [Environment]::NewLine,
    $utf8WithoutBom)

$summary
