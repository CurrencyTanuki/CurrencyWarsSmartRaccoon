# 货币战争智能狸交接增量（2026-08-02）

> 这是对 2026-08-01 原始交接目录的增量说明。原交接文件与基线发布包均保留；接手者应先读本文件，再读 `HANDOFF_FOR_CODEX.md` 和 `docs/PENDING_USER_FIXES.md`。

## 当前基线

- 源码目录：`CurrencyWarsSmartRaccoon-CodexHandoff-20260801`
- 解决方案：`CurrencyWarsAssistant.sln`
- 当前源码版本：`0.2.778`
- 最新可运行候选：`artifacts/CurrencyWarsSmartRaccoon-0.2.778-win-x64-portable-shutdownfix-20260802/CurrencyWarsAssistant.App.exe`
- EXE SHA-256：`745037720AA84058C44D3B43924B4A2623718EC0A674C210857E7A7877CEFCBF`
- 原始可回退发布基线：`release-baseline/CurrencyWarsSmartRaccoon-0.2.751-win-x64-portable.zip`

## 本轮实际进入目录的成果

### 1. 页面、结算与对局边界

- 增加成功结算、失败结算、最终失败和最终成功等语义分类与对局边界处理。
- 增加相关真实帧素材和定向回归测试。
- 主要文件：
  - `src/CurrencyWarsAssistant.Tasks/Phase2SettlementSemanticClassifier.cs`
  - `src/CurrencyWarsAssistant.Tasks/Phase2PostCompletionBoundaryDetector.cs`
  - `src/CurrencyWarsAssistant.Tasks/Phase2RunCompletionDetector.cs`
  - `tests/CurrencyWarsAssistant.Tests/RunCompletionArchiveTests.cs`

### 2. 多帧状态合并、断点与归档

- 节点数据链、checkpoint、completed-run 和逐字段可靠状态合并均有修改。
- 新增或加强 Unknown 不覆盖 Known、集合状态保留、装备状态合并等回归保护。
- 主要文件：
  - `src/CurrencyWarsAssistant.Advisor/Contracts.cs`
  - `src/CurrencyWarsAssistant.Advisor/LocalRunStore.cs`
  - `src/CurrencyWarsAssistant.Advisor/Phase2OperationalContracts.cs`
  - `src/CurrencyWarsAssistant.Advisor/RunCheckpointContracts.cs`
  - `tests/CurrencyWarsAssistant.Tests/DataChainRegressionTests.cs`
  - `tests/CurrencyWarsAssistant.Tests/EquipmentStateMergeRegressionTests.cs`
  - `tests/CurrencyWarsAssistant.Tests/StableFactDataChainTests.cs`

### 3. 实时识别、角色、装备及图标数据链

- 修改实时采集、快速页面判断、稳定状态追踪、识别区域、OCR 与模板匹配相关实现。
- 增加角色卡、负面词条、动画过场、页面结构等真实帧测试素材和测试。
- 主要文件位于：
  - `src/CurrencyWarsAssistant.Tasks/Phase2LiveCollectionService.cs`
  - `src/CurrencyWarsAssistant.Tasks/Phase2RealtimeRecognitionPipeline.cs`
  - `src/CurrencyWarsAssistant.Tasks/Phase2OperationalScreenshotAnalyzer*.cs`
  - `src/CurrencyWarsAssistant.Vision/CharacterCardRecognition.cs`
  - `src/CurrencyWarsAssistant.Vision/Phase2IconRecognition.cs`
  - `tests/CurrencyWarsAssistant.Tests/Fixtures/phase2-live-2026-07-29/`
  - `tests/CurrencyWarsAssistant.Tests/Fixtures/phase2-transition-2026-08-01/`

### 4. 历史数据 UI 与字段覆盖

- 增加完成对局窗口、历史详情 ViewModel、字段覆盖契约和相关测试。
- 主要文件：
  - `src/CurrencyWarsAssistant.App/CompletedRunsWindow.xaml*`
  - `src/CurrencyWarsAssistant.App/DetailedHistoryWindow.xaml*`
  - `src/CurrencyWarsAssistant.App/HistoricalDetailViewModels.cs`
  - `src/CurrencyWarsAssistant.App/HistoricalUiFieldCoverage.cs`
  - `docs/HISTORICAL_UI_FIELD_COVERAGE_2026-08-01.md`
  - `tests/CurrencyWarsAssistant.Tests/HistoricalUiFieldCoverageTests.cs`

### 5. 自动刷开局与奖励阶段衔接

- 修改开局循环、奖励战斗和手动接管路径；保留原有刷取业务规则。
- 主要文件：
  - `src/CurrencyWarsAssistant.Tasks/OpeningRerollLoopCoordinator.cs`
  - `src/CurrencyWarsAssistant.Tasks/RewardStageAutomation.cs`
  - `src/CurrencyWarsAssistant.Tasks/RewardStageAutomation.Battle.cs`
  - `tests/CurrencyWarsAssistant.Tests/RewardStageManualHandoffTests.cs`

### 6. 程序退出与后台任务收敛（最新修复）

根因是关闭流程没有统一停止接收新工作、真实后台任务未完整跟踪、清理缺少外层超时，以及 `Closing` 内同步再次调用 `Close()` 可能产生重入异常。

修复内容：

- 关闭时先进入 quiesce 状态，拒绝新任务，再传播取消。
- 跟踪实际运行的截图、识别、记录任务。
- 使用共享 3 秒关闭期限和外层有界等待；不响应取消的任务不会无限阻塞退出。
- 清理辅助窗口、鼠标钩子及相关资源后，通过 Dispatcher 完成第二阶段关闭，避免关闭重入。
- 主要修改：
  - `src/CurrencyWarsAssistant.App/MainWindow.xaml.cs`
  - `src/CurrencyWarsAssistant.App/MainViewModel.cs`
  - `src/CurrencyWarsAssistant.App/SituationAnalysisViewModel.cs`
  - `tests/CurrencyWarsAssistant.Tests/ApplicationShutdownContractTests.cs`

最近一次定向验证记录：

- Release 构建：0 warning / 0 error。
- `ApplicationShutdownContractTests`：3/3 通过。
- 关闭、实时识别、checkpoint、归档等相关筛选回归：38/38 通过。
- 实际 UI 进程验证覆盖空闲、实时采集、正式记录和模拟不响应后台任务；不响应任务在约 3.2 秒的期限后退出。
- 仍未在真实 OCR 引擎恰好处于底层调用中时稳定命中关闭时机；该项属于未验证范围，不应表述为已全面覆盖。

### 7. 独立攻略研究交接包

- `external-handoffs/guide-research-v1.0.0/` 和对应 ZIP 已加入交接目录。
- 内含两套版本化 Schema、标准 ID、示例、非法样本、校验脚本和外部 AI 任务说明。
- 此包目前仍是独立研究交付物，尚未正式接入生产攻略/建议模块。

## 已知未完成或仍需实机确认

以下内容没有因为本次打包而自动变成“已修复”：

1. 关键帧队列满时，终局/主动结算帧仍可能被丢弃；主动退出后的失败归档可能漏掉。
2. 主动结算后立即开始新局的自动 runId 边界识别仍需修复和实机验证。
3. 已有真实 1-3 备战素材曾被识别为 `preparation_1_2`，页面/节点 OCR 冲突仍是识别风险。
4. 攻略研究 JSON 尚未接入正式建议系统。
5. `docs/PENDING_USER_FIXES.md` 保留了更早期实机问题记录，其中部分可能已被后续代码覆盖；处理前应以当前版本日志和真实复现为准，避免重复修复。

## 接手建议顺序

1. 先使用最新 `0.2.778` 便携候选复现“主动结算 + 新局”边界问题。
2. 优先修复关键帧终局保留策略与新 runId 判定，不要同时重构识别管线。
3. 用本包真实帧 fixtures 做定向回放，再进行一次实际游戏测试。
4. 识别和状态边界稳定后，再接入 `external-handoffs/guide-research-v1.0.0`。

## 清单说明

- `FILE_SHA256_MANIFEST.csv`：2026-08-01 原始交接基线，可用于判断原文件是否变化。
- `HANDOFF_CHANGE_MANIFEST_20260802.csv`：相对上述基线的 Added / Modified / Deleted 精确哈希差异。
- `PACKAGE_MANIFEST_2026-08-02.json`：本次 ZIP 的包含/排除规则、文件数量、关键产物及校验信息。

本包刻意不包含 `%LOCALAPPDATA%` 中的用户日志、对局记录、设置、UID、个人截图，也不包含 `bin/`、`obj/`、临时隔离运行目录和重复发布目录。
