# ISSUE-001 验证记录

## 针对性验证

命令：

```powershell
dotnet build src\CurrencyWarsAssistant.App\CurrencyWarsAssistant.App.csproj -c Release --no-restore --nologo
```

结果：通过，0 个警告、0 个错误。

应用输出目录已包含 `THIRD_PARTY_NOTICES.md`，其 SHA-256 与工作副本源文件、原项目文件、
交接程序包文件完全一致：

```text
87DCF2CF15B2813A253244E343B2281A64B03F730D22F6C23BAEE462522A7755
```

## 构建回归

命令：

```powershell
dotnet build CurrencyWarsAssistant.sln -c Release --no-restore --nologo
```

结果：通过，0 个警告、0 个错误。

## 全量测试基线

命令：

```powershell
dotnet test tests\CurrencyWarsAssistant.Tests\CurrencyWarsAssistant.Tests.csproj -c Release --no-build --nologo --logger "console;verbosity=minimal"
```

结果：失败；687 通过、94 失败、1 跳过，共 782 项，耗时 7 分 40 秒。

这些失败不由 ISSUE-001 的通知文件恢复引入。首要共同失败是交接源码缺失
`config\page-recognition.1920x1080.json` 等运行时资产，另有
`HistoricalUiFieldCoverageTests.Registry_CoversEveryPublicPropertyOfFinalDataTypes`、
金币识别和性能断言等独立失败。它们必须作为后续问题逐项复现和修复；在修复前不得宣称
全量回归通过。

## 当前结论

ISSUE-001 的构建阻断已由单文件恢复直接消除，并通过应用和解决方案两级构建验证。
全量测试仍为红色基线，因此该结论仅限于本问题，不代表项目整体稳定。

## 独立审查结论

审查员于 2026-08-08 正式批准 ISSUE-001。审查确认：项目文件对根目录通知文件形成
确定的构建依赖，缺失文件与 `MSB3030` 一一对应；恢复文件与原项目及交接程序包两份
可信来源的 SHA-256 完全一致；生产改动仅为恢复该静态文件；应用与解决方案 Release
构建以及输出复制链均已覆盖。该批准只针对 ISSUE-001，不代表全量测试、识别系统或
项目整体达到发布标准。
