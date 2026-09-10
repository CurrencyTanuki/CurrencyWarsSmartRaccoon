$p = Get-Process -Id 12096 -ErrorAction SilentlyContinue
if ($p) { Write-Output ("PATH=" + $p.Path) } else { Write-Output "PROC-NOT-FOUND" }
Write-Output "---TASK---"
schtasks /Query /TN CWSmartRaccoonCmdTest /V /FO LIST | Select-String -Pattern "Task To Run|Status|Last Run|Result" | ForEach-Object { $_.Line }
