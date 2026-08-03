param(
    [Parameter(Mandatory = $true)]
    [string]$RawDirectory,

    [Parameter(Mandatory = $false)]
    [string]$RuntimeOutputDirectory,

    [Parameter(Mandatory = $false)]
    [string]$ReportDirectory
)

$ErrorActionPreference = 'Stop'

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function ConvertTo-DeterministicJson {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [int]$Depth = 24
    )

    return ($Value | ConvertTo-Json -Depth $Depth) + [Environment]::NewLine
}

function Write-Utf8WithoutBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

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

function Get-JsonValueType {
    param($Value)

    if ($null -eq $Value) { return 'null' }
    if ($Value -is [bool]) { return 'boolean' }
    if ($Value -is [string]) { return 'string' }
    if ($Value -is [byte] -or $Value -is [sbyte] -or
        $Value -is [int16] -or $Value -is [uint16] -or
        $Value -is [int32] -or $Value -is [uint32] -or
        $Value -is [int64] -or $Value -is [uint64]) { return 'integer' }
    if ($Value -is [single] -or $Value -is [double] -or $Value -is [decimal]) {
        return 'number'
    }
    if ($Value -is [System.Collections.IDictionary] -or
        $Value -is [pscustomobject]) { return 'object' }
    if ($Value -is [System.Collections.IEnumerable]) { return 'array' }
    return 'unknown'
}

function Get-JsonObjectProperties {
    param([Parameter(Mandatory = $true)]$Object)

    if ($Object -is [System.Collections.IDictionary]) {
        return @(
            foreach ($key in @($Object.Keys)) {
                [pscustomobject]@{
                    Name = [string]$key
                    Value = $Object[$key]
                }
            }
        )
    }
    return @($Object.PSObject.Properties)
}

function Get-JsonObjectProperty {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) {
            return [pscustomobject]@{ Name = $Name; Value = $Object[$Name] }
        }
        return $null
    }
    return $Object.PSObject.Properties[$Name]
}

function Test-JsonSchemaNode {
    param(
        $Value,
        [Parameter(Mandatory = $true)]$Schema,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Errors
    )

    if ($null -ne $Schema.type) {
        $actualType = Get-JsonValueType $Value
        $allowedTypes = @($Schema.type)
        $typeMatches = $allowedTypes -contains $actualType -or
            ($actualType -eq 'integer' -and $allowedTypes -contains 'number')
        if (-not $typeMatches) {
            $Errors.Add("$Path expected type $($allowedTypes -join '|'), actual $actualType")
            return
        }
    }

    if ($null -ne $Schema.const -and $Value -cne $Schema.const) {
        $Errors.Add("$Path expected constant '$($Schema.const)', actual '$Value'")
    }

    if ($null -ne $Schema.enum) {
        $matched = @($Schema.enum | Where-Object { $_ -ceq $Value }).Count -gt 0
        if (-not $matched) {
            $Errors.Add("$Path contains unknown enum value '$Value'")
        }
    }

    $actual = Get-JsonValueType $Value
    if ($actual -eq 'string') {
        if ($null -ne $Schema.minLength -and $Value.Length -lt [int]$Schema.minLength) {
            $Errors.Add("$Path is shorter than minLength $($Schema.minLength)")
        }
        if ($null -ne $Schema.pattern -and $Value -cnotmatch [string]$Schema.pattern) {
            $Errors.Add("$Path does not match pattern $($Schema.pattern)")
        }
    }
    elseif ($actual -eq 'integer' -or $actual -eq 'number') {
        if ($null -ne $Schema.minimum -and $Value -lt $Schema.minimum) {
            $Errors.Add("$Path is below minimum $($Schema.minimum)")
        }
        if ($null -ne $Schema.maximum -and $Value -gt $Schema.maximum) {
            $Errors.Add("$Path is above maximum $($Schema.maximum)")
        }
    }
    elseif ($actual -eq 'array') {
        $items = @($Value)
        if ($null -ne $Schema.minItems -and $items.Count -lt [int]$Schema.minItems) {
            $Errors.Add("$Path has fewer than $($Schema.minItems) items")
        }
        if ($null -ne $Schema.items) {
            for ($index = 0; $index -lt $items.Count; $index++) {
                Test-JsonSchemaNode -Value $items[$index] -Schema $Schema.items `
                    -Path "$Path[$index]" -Errors $Errors
            }
        }
    }
    elseif ($actual -eq 'object') {
        $properties = @(Get-JsonObjectProperties -Object $Value)
        if ($null -ne $Schema.required) {
            foreach ($requiredName in @($Schema.required)) {
                if ($null -eq (Get-JsonObjectProperty -Object $Value `
                        -Name ([string]$requiredName))) {
                    $Errors.Add("$Path is missing required property '$requiredName'")
                }
            }
        }

        $knownNames = @()
        if ($null -ne $Schema.properties) {
            $knownNames = @($Schema.properties.PSObject.Properties.Name)
            foreach ($schemaProperty in @($Schema.properties.PSObject.Properties)) {
                $sourceProperty = Get-JsonObjectProperty -Object $Value `
                    -Name $schemaProperty.Name
                if ($null -ne $sourceProperty) {
                    Test-JsonSchemaNode -Value $sourceProperty.Value `
                        -Schema $schemaProperty.Value -Path "$Path.$($schemaProperty.Name)" `
                        -Errors $Errors
                }
            }
        }

        if ($Schema.additionalProperties -is [bool] -and
            -not $Schema.additionalProperties) {
            foreach ($property in $properties) {
                if ($knownNames -notcontains $property.Name) {
                    $Errors.Add("$Path contains unsupported property '$($property.Name)'")
                }
            }
        }
    }
}

function Resolve-ContainedInputPath {
    param(
        [Parameter(Mandatory = $true)][string]$BaseDirectory,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    if ([System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "Raw package input must be relative: $RelativePath"
    }

    $resolved = [System.IO.Path]::GetFullPath((Join-Path $BaseDirectory $RelativePath))
    $prefix = $BaseDirectory.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Raw package input escapes its directory: $RelativePath"
    }
    return $resolved
}

function Convert-UnknownProperties {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string[]]$KnownNames
    )

    $extensions = [ordered]@{}
    foreach ($property in @((Get-JsonObjectProperties -Object $Object) | Sort-Object Name)) {
        if ($KnownNames -notcontains $property.Name) {
            $extensions[$property.Name] = $property.Value
        }
    }
    return $extensions
}

function Write-FailureReport {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string[]]$Errors,
        $Package,
        $TransformMap
    )

    [System.IO.Directory]::CreateDirectory($Directory) | Out-Null
    $rejectedById = @{}
    foreach ($validationError in @($Errors | Sort-Object -Unique)) {
        $match = [regex]::Match(
            [string]$validationError,
            'currency_wars_equipment_[0-9]{3}')
        if ($match.Success) {
            if (-not $rejectedById.ContainsKey($match.Value)) {
                $rejectedById[$match.Value] = @()
            }
            $rejectedById[$match.Value] += [string]$validationError
        }
    }
    $rejectedRecords = @(
        foreach ($id in @($rejectedById.Keys | Sort-Object)) {
            [ordered]@{
                id = $id
                reasons = @($rejectedById[$id])
            }
        }
    )
    $report = [ordered]@{
        schema_version = '1.0.0'
        game_version = if ($null -ne $Package) { $Package.game_version } else { $null }
        dataset = 'equipment'
        status = 'rejected'
        output_written = $false
        errors = @($Errors | Sort-Object -Unique)
        field_mappings = if ($null -ne $TransformMap) { @($TransformMap.field_mappings) } else { @() }
        defaults = if ($null -ne $TransformMap) { @($TransformMap.defaults) } else { @() }
        rejected_records = $rejectedRecords
        unknown_fields = [ordered]@{}
    }
    Write-Utf8WithoutBom -Path (Join-Path $Directory 'conversion-report.json') `
        -Content (ConvertTo-DeterministicJson -Value $report)
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$schemaDirectory = Join-Path $repositoryRoot 'schemas\game-data\1.0.0\equipment'
$resolvedRawDirectory = [System.IO.Path]::GetFullPath($RawDirectory)
if ([string]::IsNullOrWhiteSpace($RuntimeOutputDirectory)) {
    $RuntimeOutputDirectory = Join-Path $repositoryRoot 'data\runtime\1.0.0\4.4\equipment'
}
if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path $repositoryRoot 'reports\data-import\4.4\equipment'
}
$resolvedRuntimeOutput = [System.IO.Path]::GetFullPath($RuntimeOutputDirectory)
$resolvedReportDirectory = [System.IO.Path]::GetFullPath($ReportDirectory)
$legacyRuntime = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'data\4.4'))

if (-not (Test-Path -LiteralPath $resolvedRawDirectory -PathType Container)) {
    throw "Raw directory does not exist: $resolvedRawDirectory"
}
if ($resolvedRuntimeOutput.Equals($legacyRuntime, [System.StringComparison]::OrdinalIgnoreCase) -or
    $resolvedRuntimeOutput.StartsWith(
        $legacyRuntime + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write normalized output inside legacy runtime directory: $legacyRuntime"
}

$package = $null
$transformMap = $null
$schemaErrors = New-Object 'System.Collections.Generic.List[string]'
try {
    $packagePath = Join-Path $resolvedRawDirectory 'package.json'
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Raw package is missing package.json: $resolvedRawDirectory"
    }

    $package = Read-JsonFile $packagePath
    $transformMap = Read-JsonFile (Join-Path $schemaDirectory 'transform-map.json')
    $packageSchema = Read-JsonFile (Join-Path $schemaDirectory 'raw-package.schema.json')
    Test-JsonSchemaNode -Value $package -Schema $packageSchema -Path '$' -Errors $schemaErrors
    if ($schemaErrors.Count -gt 0) {
        throw 'Raw package schema validation failed.'
    }

    $recordsPath = Resolve-ContainedInputPath -BaseDirectory $resolvedRawDirectory `
        -RelativePath $package.inputs.records.file
    $iconManifestPath = Resolve-ContainedInputPath -BaseDirectory $resolvedRawDirectory `
        -RelativePath $package.inputs.icon_manifest.file
    foreach ($inputPath in @($recordsPath, $iconManifestPath)) {
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) {
            throw "Raw package input does not exist: $inputPath"
        }
    }

    $recordsHash = (Get-FileHash -LiteralPath $recordsPath -Algorithm SHA256).Hash
    $iconManifestHash = (Get-FileHash -LiteralPath $iconManifestPath -Algorithm SHA256).Hash
    if ($recordsHash -cne $package.inputs.records.sha256) {
        throw 'Raw records hash does not match package.json.'
    }
    if ($iconManifestHash -cne $package.inputs.icon_manifest.sha256) {
        throw 'Raw icon manifest hash does not match package.json.'
    }

    $raw = Read-JsonFile $recordsPath
    $iconManifest = Read-JsonFile $iconManifestPath
    $rawSchema = Read-JsonFile (Join-Path $schemaDirectory 'equipment-raw.schema.json')
    $manifestSchema = Read-JsonFile (
        Join-Path $schemaDirectory 'equipment-icon-manifest.schema.json')
    Test-JsonSchemaNode -Value $raw -Schema $rawSchema -Path '$records' -Errors $schemaErrors
    Test-JsonSchemaNode -Value $iconManifest -Schema $manifestSchema `
        -Path '$icon_manifest' -Errors $schemaErrors
    if ($schemaErrors.Count -gt 0) {
        throw 'Equipment raw schema validation failed.'
    }

    $records = @($raw.records)
    $manifestRecords = @($iconManifest.records)
    $semanticErrors = New-Object 'System.Collections.Generic.List[string]'
    if ($raw.metadata.game_version -cne $package.game_version) {
        $semanticErrors.Add('Raw metadata game_version does not match package game_version.')
    }
    if ([int]$raw.metadata.record_count -ne $records.Count) {
        $semanticErrors.Add('Raw metadata record_count does not match records length.')
    }
    if ([int]$iconManifest.record_count -ne $manifestRecords.Count) {
        $semanticErrors.Add('Icon manifest record_count does not match records length.')
    }
    if ($manifestRecords.Count -ne $records.Count) {
        $semanticErrors.Add('Equipment and icon manifest record counts differ.')
    }

    $recordsById = @{}
    $recordsByName = @{}
    foreach ($record in $records) {
        if ($recordsById.ContainsKey([string]$record.id)) {
            $semanticErrors.Add("Duplicate equipment ID: $($record.id)")
        }
        else {
            $recordsById[[string]$record.id] = $record
        }
        if ($recordsByName.ContainsKey([string]$record.name)) {
            $semanticErrors.Add("Duplicate equipment name: $($record.name)")
        }
        else {
            $recordsByName[[string]$record.name] = $record
        }
    }

    $manifestById = @{}
    foreach ($entry in $manifestRecords) {
        if ($manifestById.ContainsKey([string]$entry.id)) {
            $semanticErrors.Add("Duplicate icon manifest ID: $($entry.id)")
        }
        else {
            $manifestById[[string]$entry.id] = $entry
        }
    }

    foreach ($record in $records) {
        $id = [string]$record.id
        $typeMapping = $transformMap.equipment_type_mappings.PSObject.Properties[
            [string]$record.equipment_type]
        if ($null -eq $typeMapping) {
            $semanticErrors.Add("$id has no runtime category mapping for '$($record.equipment_type)'.")
        }

        if (-not $manifestById.ContainsKey($id)) {
            $semanticErrors.Add("$id has no icon manifest entry.")
        }
        else {
            $iconEntry = $manifestById[$id]
            if ($iconEntry.name -cne $record.name -or
                $iconEntry.equipment_type -cne $record.equipment_type -or
                $iconEntry.local_path -cne $record.icon.local_path -or
                $iconEntry.source_url -cne $record.icon.source_url -or
                $iconEntry.sha256 -cne $record.icon.sha256 -or
                [int64]$iconEntry.bytes -ne [int64]$record.icon.bytes -or
                [int]$iconEntry.width -ne [int]$record.icon.width -or
                [int]$iconEntry.height -ne [int]$record.icon.height) {
                $semanticErrors.Add("$id does not match its icon manifest entry.")
            }

            $assetPath = [System.IO.Path]::GetFullPath((
                Join-Path $resolvedRawDirectory ([string]$iconEntry.local_path)))
            $rawPrefix = $resolvedRawDirectory.TrimEnd(
                [System.IO.Path]::DirectorySeparatorChar) +
                [System.IO.Path]::DirectorySeparatorChar
            if (-not $assetPath.StartsWith(
                    $rawPrefix,
                    [System.StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
                $semanticErrors.Add("$id references a missing or unsafe icon asset path.")
            }
            else {
                if ((Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne
                    ([string]$iconEntry.sha256).ToLowerInvariant()) {
                    $semanticErrors.Add("$id icon asset hash does not match the manifest.")
                }
                if ((Get-Item -LiteralPath $assetPath).Length -ne [int64]$iconEntry.bytes) {
                    $semanticErrors.Add("$id icon asset length does not match the manifest.")
                }
            }
        }

        foreach ($componentName in @($record.synthesis_components)) {
            if ($null -ne $componentName -and
                -not $recordsByName.ContainsKey([string]$componentName)) {
                $semanticErrors.Add("$id references unknown synthesis component '$componentName'.")
            }
        }
        if (-not ([string]::IsNullOrWhiteSpace([string]$record.base_equipment_name)) -and
            -not $recordsByName.ContainsKey([string]$record.base_equipment_name)) {
            $semanticErrors.Add(
                "$id references unknown base equipment '$($record.base_equipment_name)'.")
        }
    }

    if ($semanticErrors.Count -gt 0) {
        foreach ($validationError in $semanticErrors) {
            [void]$schemaErrors.Add($validationError)
        }
        throw 'Equipment semantic/reference validation failed.'
    }

    $knownSourceFields = @($transformMap.source_fields | ForEach-Object { [string]$_ })
    $unknownFieldCounts = [ordered]@{}
    $runtimeRecords = @()
    foreach ($record in @($records | Sort-Object id)) {
        $unknownProperties = Convert-UnknownProperties -Object $record `
            -KnownNames $knownSourceFields
        foreach ($name in @($unknownProperties.Keys)) {
            if (-not $unknownFieldCounts.Contains($name)) {
                $unknownFieldCounts[$name] = 0
            }
            $unknownFieldCounts[$name]++
        }

        $componentIds = @(
            foreach ($componentName in @($record.synthesis_components)) {
                if ($null -ne $componentName) {
                    $componentKey = [string]$componentName
                    $componentRecord = $recordsByName[$componentKey]
                    [string]$componentRecord.id
                }
            }
        )
        $baseEquipmentId = $null
        if (-not ([string]::IsNullOrWhiteSpace([string]$record.base_equipment_name))) {
            $baseEquipmentKey = [string]$record.base_equipment_name
            $baseEquipmentRecord = $recordsByName[$baseEquipmentKey]
            $baseEquipmentId = [string]$baseEquipmentRecord.id
        }
        $unspecifiedVersions = @($transformMap.unspecified_version_values)
        $introducedVersion = if ($unspecifiedVersions -contains
            [string]$record.implementation_version) {
            $null
        }
        else {
            [string]$record.implementation_version
        }
        $iconEntry = $manifestById[[string]$record.id]
        $typeMappingProperty = $transformMap.equipment_type_mappings.PSObject.Properties[
            [string]$record.equipment_type]
        $runtimeRecords += [ordered]@{
            id = [string]$record.id
            name = [string]$record.name
            category = [string]$typeMappingProperty.Value
            equippable = [bool]$record.equippable
            occupies_equipment_slot = [bool]$record.occupies_equipment_slot
            base_attributes = @($record.base_attributes)
            effect = [string]$record.effect
            acquisition_methods = @($record.acquisition_methods)
            introduced_version = $introducedVersion
            component_ids = $componentIds
            base_equipment_id = $baseEquipmentId
            icon = [ordered]@{
                asset_path = [string]$iconEntry.local_path
                source_url = [string]$iconEntry.source_url
                sha256 = [string]$iconEntry.sha256
                width = [int]$iconEntry.width
                height = [int]$iconEntry.height
            }
            source_extensions = $unknownProperties
        }
    }

    $runtime = [ordered]@{
        schema_version = '1.0.0'
        game_version = [string]$package.game_version
        dataset = 'equipment'
        source = [ordered]@{
            records_sha256 = $recordsHash
            icon_manifest_sha256 = $iconManifestHash
        }
        source_extensions = [ordered]@{
            metadata = Convert-UnknownProperties -Object $raw.metadata `
                -KnownNames @('game_version', 'record_count')
            dataset = Convert-UnknownProperties -Object $raw -KnownNames @('metadata', 'records')
            icon_manifest = Convert-UnknownProperties -Object $iconManifest `
                -KnownNames @('record_count', 'records')
        }
        records = $runtimeRecords
    }

    $runtimeSchema = Read-JsonFile (
        Join-Path $schemaDirectory 'equipment-runtime.schema.json')
    $runtimeErrors = New-Object 'System.Collections.Generic.List[string]'
    Test-JsonSchemaNode -Value $runtime -Schema $runtimeSchema `
        -Path '$runtime' -Errors $runtimeErrors
    if ($runtimeErrors.Count -gt 0) {
        foreach ($validationError in $runtimeErrors) {
            [void]$schemaErrors.Add($validationError)
        }
        throw 'Normalized runtime schema validation failed.'
    }

    $runtimeJson = ConvertTo-DeterministicJson -Value $runtime
    $runtimeHash = Get-Sha256Text $runtimeJson
    $runtimeManifest = [ordered]@{
        schema_version = '1.0.0'
        game_version = [string]$package.game_version
        dataset = 'equipment'
        files = @(
            [ordered]@{
                file = 'equipment.json'
                sha256 = $runtimeHash
                record_count = $runtimeRecords.Count
            }
        )
    }

    [System.IO.Directory]::CreateDirectory($resolvedRuntimeOutput) | Out-Null
    foreach ($entry in $manifestRecords) {
        $runtimeAssetPath = Join-Path $resolvedRuntimeOutput ([string]$entry.local_path)
        [System.IO.Directory]::CreateDirectory(
            (Split-Path -Parent $runtimeAssetPath)) | Out-Null
        [System.IO.File]::Copy(
            (Join-Path $resolvedRawDirectory ([string]$entry.local_path)),
            $runtimeAssetPath,
            $true)
    }
    Write-Utf8WithoutBom -Path (Join-Path $resolvedRuntimeOutput 'equipment.json') `
        -Content $runtimeJson
    Write-Utf8WithoutBom -Path (Join-Path $resolvedRuntimeOutput 'manifest.json') `
        -Content (ConvertTo-DeterministicJson -Value $runtimeManifest)

    $unknownSummary = [ordered]@{}
    foreach ($name in @($unknownFieldCounts.Keys | Sort-Object)) {
        $unknownSummary[$name] = [int]$unknownFieldCounts[$name]
    }
    $report = [ordered]@{
        schema_version = '1.0.0'
        game_version = [string]$package.game_version
        dataset = 'equipment'
        status = 'accepted'
        output_written = $true
        input = [ordered]@{
            records_sha256 = $recordsHash
            icon_manifest_sha256 = $iconManifestHash
            record_count = $records.Count
        }
        output = [ordered]@{
            file = 'equipment.json'
            sha256 = $runtimeHash
            record_count = $runtimeRecords.Count
        }
        field_mappings = @($transformMap.field_mappings)
        defaults = @($transformMap.defaults)
        rejected_records = @()
        unknown_fields = [ordered]@{
            record_fields = $unknownSummary
            preservation = 'runtime.records[*].source_extensions'
            metadata_preservation = 'runtime.source_extensions'
        }
        validations = @(
            'schema_version',
            'game_version',
            'schema_structure',
            'stable_id_pattern_and_uniqueness',
            'enum_and_value_ranges',
            'synthesis_component_references',
            'base_equipment_references',
            'icon_manifest_cross_file_references',
            'icon_asset_hashes'
        )
    }
    [System.IO.Directory]::CreateDirectory($resolvedReportDirectory) | Out-Null
    Write-Utf8WithoutBom -Path (
        Join-Path $resolvedReportDirectory 'conversion-report.json') `
        -Content (ConvertTo-DeterministicJson -Value $report)

    [pscustomobject]@{
        status = 'accepted'
        record_count = $runtimeRecords.Count
        runtime_file = Join-Path $resolvedRuntimeOutput 'equipment.json'
        runtime_sha256 = $runtimeHash
        report_file = Join-Path $resolvedReportDirectory 'conversion-report.json'
    }
}
catch {
    if ($schemaErrors.Count -eq 0) {
        $schemaErrors.Add($_.Exception.Message)
    }
    else {
        $schemaErrors.Add($_.Exception.Message)
    }
    Write-FailureReport -Directory $resolvedReportDirectory `
        -Errors @($schemaErrors) -Package $package -TransformMap $transformMap
    throw
}
