$pfx = -join [char[]](0x6307, 0x4EE4, 0x6D4B, 0x8BD5)
$dir = Get-ChildItem 'C:\Users\zzz81\Desktop' -Directory | Where-Object { $_.Name -like 'CurrencyWarsAssistant-*' -and $_.Name -notlike '*sandbox*' } | Select-Object -First 1
$base = $dir.FullName
$cmdPath = Join-Path $base ($pfx + '-command.txt')
$resPath = Join-Path $base ($pfx + '-result.txt')
$sizeBefore = (Get-Item $resPath -ErrorAction SilentlyContinue).Length
[System.IO.File]::WriteAllText($cmdPath, "I1", [System.Text.Encoding]::ASCII)
$deadline = (Get-Date).AddSeconds(15)
$receipt = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $r = Get-Item $resPath -ErrorAction SilentlyContinue
    if ($r -and $r.Length -gt $sizeBefore) {
        $lines = [System.IO.File]::ReadAllLines($resPath, [System.Text.Encoding]::UTF8)
        $receipt = ($lines | Select-Object -Last 2) -join " | "
        break
    }
}
if ($receipt) { Write-Output ("RECEIPT: " + $receipt) } else { Write-Output "NO-RECEIPT-IN-15S" }
