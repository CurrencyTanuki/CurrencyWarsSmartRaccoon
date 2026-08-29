# 手动完成 WiX Payload.wxs 生成 + wix build（对 framework-dependent 发布目录）
# 复用 Build-Installer.ps1 的 WXS 生成逻辑，但独立运行，避免原脚本 $PSScriptRoot 问题。
$ErrorActionPreference = 'Stop'
$repoRoot = 'D:\CWAFix-20260814'
$publishDir = 'D:\CWAFix-20260814\artifacts\CurrencyWarsAssistant-0.2.842-win-x64'
$installerDir = 'D:\CWAFix-20260814\artifacts\installers'
$version = '0.2.842'
$wix = 'D:\Codex-2\.tools\wix\wix.exe'
$wxsMain = 'D:\CWAFix-20260814\installer\CurrencyWarsAssistant.wxs'
$payloadWxs = Join-Path $installerDir "CurrencyWarsAssistant-$version.Payload.wxs"
$msi = Join-Path $installerDir "CurrencyWarsAssistant-$version-win-x64.msi"

New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

function Get-StableInstallerId {
    param([string]$Prefix, [string]$Value)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())
        $hash = [BitConverter]::ToString($sha256.ComputeHash($bytes)).Replace('-','')
        return "${Prefix}_$($hash.Substring(0,24))"
    } finally { $sha256.Dispose() }
}

$directoryIds = @{}
$pubRootSep = $publishDir.TrimEnd('\') + '\'
$payloadFiles = Get-ChildItem -LiteralPath $publishDir -Recurse -File | Sort-Object FullName
foreach ($dir in Get-ChildItem -LiteralPath $publishDir -Recurse -Directory) {
    $rel = $dir.FullName.Substring($pubRootSep.Length)
    $directoryIds[$dir.FullName] = Get-StableInstallerId -Prefix 'Dir' -Value $rel
}

$xmlSettings = [Xml.XmlWriterSettings]::new()
$xmlSettings.Encoding = [Text.UTF8Encoding]::new($false)
$xmlSettings.Indent = $true
$writer = [Xml.XmlWriter]::Create($payloadWxs, $xmlSettings)
try {
    $ns = 'http://wixtoolset.org/schemas/v4/wxs'
    $writer.WriteStartDocument()
    $writer.WriteStartElement('Wix', $ns)
    $writer.WriteStartElement('Fragment', $ns)
    $writer.WriteStartElement('DirectoryRef', $ns)
    $writer.WriteAttributeString('Id', 'INSTALLFOLDER')
    function Write-Dirs { param($Parent)
        foreach ($child in Get-ChildItem -LiteralPath $Parent -Directory | Sort-Object Name) {
            $writer.WriteStartElement('Directory', $ns)
            $writer.WriteAttributeString('Id', $directoryIds[$child.FullName])
            $writer.WriteAttributeString('Name', $child.Name)
            Write-Dirs $child.FullName
            $writer.WriteEndElement()
        }
    }
    Write-Dirs $publishDir
    $writer.WriteEndElement()  # DirectoryRef
    $writer.WriteEndElement()  # Fragment

    $writer.WriteStartElement('Fragment', $ns)
    $writer.WriteStartElement('ComponentGroup', $ns)
    $writer.WriteAttributeString('Id', 'PayloadComponents')
    foreach ($file in $payloadFiles) {
        $rel = $file.FullName.Substring($pubRootSep.Length)
        $compId = Get-StableInstallerId -Prefix 'Cmp' -Value $rel
        $fileId = Get-StableInstallerId -Prefix 'File' -Value $rel
        $dirId = if ($file.DirectoryName -eq $publishDir) { 'INSTALLFOLDER' } else { $directoryIds[$file.DirectoryName] }
        $writer.WriteStartElement('Component', $ns)
        $writer.WriteAttributeString('Id', $compId)
        $writer.WriteAttributeString('Directory', $dirId)
        $writer.WriteAttributeString('Guid', '*')
        $writer.WriteStartElement('File', $ns)
        $writer.WriteAttributeString('Id', $fileId)
        $writer.WriteAttributeString('Source', $file.FullName)
        $writer.WriteAttributeString('KeyPath', 'yes')
        $writer.WriteEndElement()  # File
        $writer.WriteEndElement()  # Component
    }
    $writer.WriteEndElement()  # ComponentGroup
    $writer.WriteEndElement()  # Fragment
    $writer.WriteEndElement()  # Wix
    $writer.WriteEndDocument()
} finally { $writer.Dispose() }

Write-Host "Payload.wxs written: $payloadWxs ($($payloadFiles.Count) files)"

# WiX 构建
& $wix build $wxsMain $payloadWxs -arch x64 -d "ProductVersion=$version" -d "PublishDir=$publishDir" -pdbtype none -out $msi
if ($LASTEXITCODE -ne 0) { throw "WiX build failed: $LASTEXITCODE" }
Write-Host "MSI created: $msi"
