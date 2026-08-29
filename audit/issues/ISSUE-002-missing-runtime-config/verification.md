# ISSUE-002 验证记录

## 修改范围

从同一交接包的 `程序/config/` 原样恢复工作副本根目录 `config/`：40 个文件，
998,658 字节。恢复后与来源逐文件计算 SHA-256，缺失 0、哈希不一致 0。
没有修改 C#、项目文件、测试或既有业务配置内容。

选择交接程序包而非 `D:/Codex-2/config` 的依据：当前源码测试要求
`challenge_health_depleted` 页面，而旧原项目配置不包含该页面；交接程序包配置包含它，
且其 30 个页面锚点引用的模板全部存在。

## 构建及输出复制验证

命令：

```powershell
dotnet build src\CurrencyWarsAssistant.App\CurrencyWarsAssistant.App.csproj `
  -c Release --no-restore --nologo
```

结果：通过，0 个警告、0 个错误。应用 Release 输出中有 40 个 `config` 文件；
与工作副本源配置逐文件 SHA-256 比较，缺失 0、哈希不一致 0。

## 针对性页面分类测试

命令：

```powershell
dotnet test tests\CurrencyWarsAssistant.Tests\CurrencyWarsAssistant.Tests.csproj `
  -c Release --no-build --nologo `
  --filter "FullyQualifiedName=CurrencyWarsAssistant.Tests.GamePageClassifierTests.ClassifierRecognizesAllPrivacySafeReplayFrames"
```

结果：通过 1/1，约 21 秒。该测试会加载恢复后的页面配置和模板，并逐张检查项目现有
隐私安全真实回放图集的页面分类；修复前同一命令在加载配置时直接失败。

## 真实应用入口验证

使用构建出的 WPF DLL 入口运行项目自带无界面批处理命令，输入为空目录，以验证完整
启动、配置加载、识别资源预热、批处理及退出链。结果在约 12 秒内以退出码 0 结束，
日志依次包含：

```text
command-parsed
wpf-started
configuration-loaded
batch-analysis-starting
batch-service-resolved
recognition-warmup-starting
recognition-warmup-completed
batch-analysis-completed
```

修复前相同入口 5 秒内不退出，只有前两行，并记录配置缺失异常。

## 全量回归

命令：

```powershell
dotnet test tests\CurrencyWarsAssistant.Tests\CurrencyWarsAssistant.Tests.csproj `
  -c Release --no-build --nologo `
  --logger "console;verbosity=minimal" `
  --logger "trx;LogFileName=ISSUE-002-after.trx" `
  --results-directory audit\issues\ISSUE-002-missing-runtime-config\test-results
```

结果：782 项中 747 通过、34 失败、1 跳过，耗时约 9 分 35 秒。修复前基线为
687 通过、94 失败、1 跳过；本项恢复后增加 60 项通过。TRX 中
`DirectoryNotFoundException` 为 0，`config/page-recognition` 缺失为 0。

TRX：`test-results/ISSUE-002-after.trx`

```text
SHA-256 DF5A983B6A3921C36EC801B543D2C92435898EB98C530ACBFB5DD5D09EDA840E
```

剩余 34 个失败按测试类聚类：

| 测试类 | 失败数 | 边界 |
|---|---:|---|
| Phase2OperationalCollectionTests | 18 | 识别正确性/性能断言，另行逐项定位 |
| PpOcrOfflineOcrTests | 8 | OCR 正确性/预热时延，另行处理 |
| EquipmentDataPipelineTests | 5 | 交接源码还缺 `tools`/schema 管线资产，另立问题 |
| RecognitionPerformanceTests | 1 | 性能断言，另行处理 |
| HistoricalUiFieldCoverageTests | 1 | 历史字段覆盖缺口，另立问题 |
| RewardShopCloseIntegrationTests | 1 | 商店关闭行为断言，另立问题 |

因此全量回归仍为红色，本项结论只表示缺失配置资产这一启动阻断已消除；不代表页面分类
之外的识别字段正确、历史界面完整或项目可发布。

## 独立审查结论

替代独立审查员于 2026-08-08 批准 ISSUE-002。审查员直接核验了交接源码、交接程序包、
工作副本、项目/启动代码、测试及 TRX，并确认：根因链成立；交接程序包是与当前测试契约
匹配的来源；工作副本 `src`（排除 `bin/obj`）与交接源码逐哈希无差异；40 个恢复资产及
Release 输出逐哈希一致；目标真实回放分类测试通过；无界面启动完整到达退出分支；
全量结果及 34 个剩余失败的聚类准确。批准仅覆盖配置资产缺失这一问题，不覆盖真实字段
识别、可见 WPF 界面、剩余失败或最终发布包。
