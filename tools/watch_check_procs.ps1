Get-Process | Where-Object { $_.ProcessName -match 'StarRail|CurrencyWars' } | Select-Object Id,ProcessName,StartTime | Format-Table -AutoSize | Out-String -Width 200
