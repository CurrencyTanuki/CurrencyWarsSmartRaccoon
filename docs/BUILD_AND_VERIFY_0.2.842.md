# Build and verify 0.2.842

Prerequisites: Windows 10/11 x64, .NET 8 SDK, .NET 8 Desktop Runtime and Microsoft Edge WebView2 Runtime.

From the source package root:

```powershell
dotnet restore CurrencyWarsAssistant.sln
dotnet build CurrencyWarsAssistant.sln -c Release --no-restore
dotnet test tests\CurrencyWarsAssistant.Tests\CurrencyWarsAssistant.Tests.csproj -c Release --no-build --logger "trx;LogFileName=full-0.2.842.trx"
dotnet publish src\CurrencyWarsAssistant.App\CurrencyWarsAssistant.App.csproj -c Release -r win-x64 --self-contained false --no-restore -o artifacts\publish
```

Focused privilege regression:

```powershell
dotnet test tests\CurrencyWarsAssistant.Tests\CurrencyWarsAssistant.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PrivilegeEquipmentOverlayRealFrameTests|FullyQualifiedName~EquipmentStateMergeRegressionTests|FullyQualifiedName~HistoricalEquipmentHtmlContractTests"
```

Published App/DI replay:

```powershell
$env:CURRENCY_WARS_PHASE2_TIMING='1'
dotnet artifacts\publish\CurrencyWarsAssistant.App.dll --phase2-batch-test --input verification\input --output verification\output --no-annotations
```

Expected release facts: product/file/informational version `0.2.842`; Release build 0 warnings and 0 errors; privilege focused suite 8/8. The recorded full suite has six timing-budget failures, documented in `HANDOFF_20260809_0.2.842.md`; do not treat those as functional passes.

Verify an extracted package against its manifest:

```powershell
Get-Content .\FILE_SHA256_MANIFEST.txt | ForEach-Object {
  $hash, $relative = $_ -split '  ', 2
  if ((Get-FileHash -Algorithm SHA256 -LiteralPath $relative).Hash -ne $hash) { throw "Hash mismatch: $relative" }
}
```
