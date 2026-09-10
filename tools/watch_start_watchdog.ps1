# Launcher for the anti-idle watchdog daemon (standard form since keeper retirement).
# Heartbeat/state/alerts live in the guide workspace (hook_gate.py reads it there);
# watched output dirs point at THIS shift's real output surface (currency-wars dev
# workspace) so legit work keeps the idle clock alive and true idling fires alerts.
# ASCII-only on purpose: Chinese paths are resolved by Desktop filter, not literals.
$ErrorActionPreference = 'Stop'
$guide = 'C:\Users\zzz81\Desktop\cw-guide-night-20260905'
$ws2 = Get-ChildItem 'C:\Users\zzz81\Desktop' -Directory | Where-Object { $_.Name -like '*20260826' } | Select-Object -First 1
if (-not $ws2) { Write-Output 'WS2-NOT-FOUND'; exit 1 }

# stale-daemon guard: refuse to double-start while a live heartbeat exists
$statePath = Join-Path $guide 'tools\watchdog_state.json'
if (Test-Path $statePath) {
  $st = Get-Content $statePath -Raw | ConvertFrom-Json
  if ($st.active -and ((Get-Date) - [datetime]$st.heartbeat).TotalSeconds -lt 180) {
    Write-Output ('ALREADY-ACTIVE pid=' + $st.pid)
    exit 0
  }
}

$py = (Get-Command python).Source
$dirs = @('docs', 'artifacts', 'tools') | ForEach-Object { Join-Path $ws2.FullName $_ }
$dirs += (Join-Path $ws2.FullName 'HANDOFF_SESSION_20260902.md')
$dirs += (Join-Path $ws2.FullName 'rule.md')
$argList = @('tools\watchdog_idle.py', '--dirs') + $dirs
$p = Start-Process -FilePath $py -ArgumentList $argList -WorkingDirectory $guide -WindowStyle Hidden -PassThru
if ($p -and -not $p.HasExited) {
  $p.PriorityClass = 'Idle'
  Write-Output ('STARTED pid=' + $p.Id + ' ws2=' + $ws2.Name)
} else {
  Write-Output 'START-FAILED'
  exit 1
}
