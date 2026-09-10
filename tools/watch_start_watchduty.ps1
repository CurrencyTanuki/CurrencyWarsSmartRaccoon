# ASCII launcher for watch_duty_123.ps1 (60s app-state snapshot + sleep-process detector).
$ErrorActionPreference = 'Stop'
$ws = (Get-ChildItem 'C:\Users\zzz81\Desktop' -Directory | Where-Object { $_.Name -like '*20260826' } | Select-Object -First 1).FullName
$script = Join-Path $ws 'artifacts\watch_duty_123.ps1'
$p = Start-Process -FilePath 'powershell' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File', $script) -WindowStyle Hidden -PassThru
if ($p) {
  $p.PriorityClass = 'Idle'
  Write-Output ('WATCHDUTY-STARTED pid=' + $p.Id)
} else {
  Write-Output 'START-FAILED'
  exit 1
}
