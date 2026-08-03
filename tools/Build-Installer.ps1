[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.2.729'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$publishDirectory = Join-Path $artifactsRoot (
    "CurrencyWarsAssistant-$Version-win-x64")
$installerDirectory = Join-Path $artifactsRoot 'installers'
$msiPath = Join-Path $installerDirectory (
    "CurrencyWarsAssistant-$Version-win-x64.msi")
$portableZipPath = Join-Path $installerDirectory (
    "CurrencyWarsAssistant-$Version-win-x64-portable.zip")
$payloadWixSource = Join-Path $installerDirectory (
    "CurrencyWarsAssistant-$Version.Payload.wxs")
$checksumPath = Join-Path $installerDirectory (
    "CurrencyWarsAssistant-$Version-win-x64.sha256.txt")
$dotnet = Join-Path $repositoryRoot '.tools\dotnet\dotnet.exe'
$wix = Join-Path $repositoryRoot '.tools\wix\wix.exe'
$project = Join-Path $repositoryRoot (
    'src\CurrencyWarsAssistant.App\CurrencyWarsAssistant.App.csproj')
$wixSource = Join-Path $repositoryRoot (
    'installer\CurrencyWarsAssistant.wxs')

if (-not (Test-Path -LiteralPath $dotnet)) {
    throw "Bundled .NET SDK was not found: $dotnet"
}

if (-not (Test-Path -LiteralPath $wix)) {
    throw "WiX 4.0.6 was not found: $wix"
}

New-Item -ItemType Directory -Force -Path $artifactsRoot | Out-Null
New-Item -ItemType Directory -Force -Path $installerDirectory | Out-Null

foreach ($generatedPath in @(
        $publishDirectory,
        $msiPath,
        $portableZipPath,
        $payloadWixSource,
        $checksumPath)) {
    $resolvedParent = [IO.Path]::GetFullPath(
        (Split-Path -Parent $generatedPath))
    if (-not $resolvedParent.StartsWith(
            $artifactsRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace a path outside artifacts: $generatedPath"
    }

    if (Test-Path -LiteralPath $generatedPath) {
        Remove-Item -LiteralPath $generatedPath -Recurse -Force
    }
}

& $dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    /p:Version=$Version
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$requiredFiles = @(
    'CurrencyWarsAssistant.App.exe',
    'config\page-recognition.1920x1080.json',
    'data\4.4\phase2-icon-assets\asset-manifest.jsonl',
    'data\advisor\1.0.0\4.4\guides\taptap-himeko-train-shield.json')
foreach ($relativePath in $requiredFiles) {
    $requiredPath = Join-Path $publishDirectory $relativePath
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Published package is missing required content: $relativePath"
    }
}

function Get-StableInstallerId {
    param(
        [Parameter(Mandatory)]
        [string]$Prefix,
        [Parameter(Mandatory)]
        [string]$Value
    )

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())
        $hash = [BitConverter]::ToString(
            $sha256.ComputeHash($bytes)).Replace('-', '')
        return "${Prefix}_$($hash.Substring(0, 24))"
    }
    finally {
        $sha256.Dispose()
    }
}

$directoryIds = @{}
$publishRootWithSeparator = $publishDirectory.TrimEnd('\') + '\'
$payloadFiles = Get-ChildItem -LiteralPath $publishDirectory -Recurse -File |
    Sort-Object FullName
foreach ($directory in Get-ChildItem `
        -LiteralPath $publishDirectory `
        -Recurse `
        -Directory) {
    $relativeDirectory = $directory.FullName.Substring(
        $publishRootWithSeparator.Length)
    $directoryIds[$directory.FullName] = Get-StableInstallerId `
        -Prefix 'Dir' `
        -Value $relativeDirectory
}

$xmlSettings = [Xml.XmlWriterSettings]::new()
$xmlSettings.Encoding = [Text.UTF8Encoding]::new($false)
$xmlSettings.Indent = $true
$writer = [Xml.XmlWriter]::Create($payloadWixSource, $xmlSettings)
try {
    $wixNamespace = 'http://wixtoolset.org/schemas/v4/wxs'
    $writer.WriteStartDocument()
    $writer.WriteStartElement('Wix', $wixNamespace)

    $writer.WriteStartElement('Fragment', $wixNamespace)
    $writer.WriteStartElement('DirectoryRef', $wixNamespace)
    $writer.WriteAttributeString('Id', 'INSTALLFOLDER')

    function Write-InstallerDirectories {
        param(
            [Parameter(Mandatory)]
            [string]$ParentPath
        )

        foreach ($child in Get-ChildItem `
                -LiteralPath $ParentPath `
                -Directory | Sort-Object Name) {
            $writer.WriteStartElement('Directory', $wixNamespace)
            $writer.WriteAttributeString('Id', $directoryIds[$child.FullName])
            $writer.WriteAttributeString('Name', $child.Name)
            Write-InstallerDirectories -ParentPath $child.FullName
            $writer.WriteEndElement()
        }
    }

    Write-InstallerDirectories -ParentPath $publishDirectory
    $writer.WriteEndElement()
    $writer.WriteEndElement()

    $writer.WriteStartElement('Fragment', $wixNamespace)
    $writer.WriteStartElement('ComponentGroup', $wixNamespace)
    $writer.WriteAttributeString('Id', 'PayloadComponents')
    foreach ($file in $payloadFiles) {
        $relativeFile = $file.FullName.Substring(
            $publishRootWithSeparator.Length)
        $componentId = Get-StableInstallerId `
            -Prefix 'Cmp' `
            -Value $relativeFile
        $fileId = Get-StableInstallerId `
            -Prefix 'File' `
            -Value $relativeFile
        $directoryId = if ($file.DirectoryName -eq $publishDirectory) {
            'INSTALLFOLDER'
        }
        else {
            $directoryIds[$file.DirectoryName]
        }

        $writer.WriteStartElement('Component', $wixNamespace)
        $writer.WriteAttributeString('Id', $componentId)
        $writer.WriteAttributeString('Directory', $directoryId)
        $writer.WriteAttributeString('Guid', '*')
        $writer.WriteStartElement('File', $wixNamespace)
        $writer.WriteAttributeString('Id', $fileId)
        $writer.WriteAttributeString('Source', $file.FullName)
        $writer.WriteAttributeString('KeyPath', 'yes')
        $writer.WriteEndElement()
        $writer.WriteEndElement()
    }

    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndDocument()
}
finally {
    $writer.Dispose()
}

& $wix build $wixSource $payloadWixSource `
    -arch x64 `
    -d "ProductVersion=$Version" `
    -d "PublishDir=$publishDirectory" `
    -pdbtype none `
    -out $msiPath
if ($LASTEXITCODE -ne 0) {
    throw "WiX build failed with exit code $LASTEXITCODE."
}

Compress-Archive `
    -Path (Join-Path $publishDirectory '*') `
    -DestinationPath $portableZipPath `
    -CompressionLevel Optimal

$hashes = Get-FileHash -Algorithm SHA256 -LiteralPath @(
    $msiPath,
    $portableZipPath)
$hashLines = $hashes | ForEach-Object {
    "{0}  {1}" -f $_.Hash.ToLowerInvariant(), (Split-Path -Leaf $_.Path)
}
Set-Content `
    -LiteralPath $checksumPath `
    -Value $hashLines `
    -Encoding utf8NoBOM

[PSCustomObject]@{
    Version = $Version
    PublishDirectory = $publishDirectory
    Installer = $msiPath
    PortableZip = $portableZipPath
    Checksums = $checksumPath
}
