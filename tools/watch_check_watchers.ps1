Write-Output "---WATCHERS---"
Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'monitor_cycle|watch_duty|watchdog' } | Select-Object ProcessId, Name, @{N='Cmd';E={$_.CommandLine.Substring(0, [Math]::Min(160, $_.CommandLine.Length))}} | Format-List
Write-Output "---TESTSESSIONS---"
Get-ChildItem "$env:LOCALAPPDATA\CurrencyWarsSmartRaccoon\logs" -Filter 'test-session-20260911-032*.jsonl' | ForEach-Object { "{0}  {1} bytes  mtime={2}" -f $_.Name, $_.Length, $_.LastWriteTime }
