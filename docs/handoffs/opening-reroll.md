# 开局导航与自动重刷

## 模块目标

安全完成 HUD 到开局读取的正向导航，按完整筛选方案判断保留/拒绝，不合格时退出结算并重新开始；在没有总轮数/总时长限制的前提下支持用户主动停止和安全熔断。

## 不属于本模块的内容

不实现底层像素算法、不绕过 Automation 直接输入、不决定 UI 主题、不把奖励关内部动作塞进开局状态机，也不直接消费上游原始 JSON。

## 当前真实状态

`CurrencyWarsNavigationTask`、`OpeningRerollLoopCoordinator` 和 `CurrencyWarsRejectedOpeningRecovery` 已接入生产 DI。正向导航有旧产物实机证据；当前连续重刷、退出恢复和方案锁定只有测试/代码证据，没有当前产物 L4。

## 已实机验证

- 较早产物完成 HUD → 指南 → 货币战争首页 → 模式 → 职级 → 敌人概览 → 位面 → 投资环境 → 1-1。
- 较早产物检测到已有进行中对局后停止。
- 较早产物出现过未知页面按 Esc 后重新识别成功。

证据：`logs/test-session-20260725-015803.jsonl`。当前 `live-test-30` 是否保持这些能力为**尚未确认**。

## 已实现但未实机验证

- 自动模式总轮数和总时长为空；测试覆盖超过旧 50 轮限制。
- 失焦暂停/回焦恢复、取消令牌和输入前台检查已接入。
- 识别失败会重试；连续三次相同失败会停止无效重复。
- 多套方案为 OR；同时命中时随机锁定一套完整方案。
- 不合格开局退出、放弃结算、返回首页有 Fake 驱动测试。

## 部分实现或占位功能

- `OpeningRerollTask`、`TemplateOpeningReader`、`OpeningRuleEvaluator` 是未注册的旧平行实现。
- `NotConfiguredRejectedOpeningRecovery` 是占位实现，当前 DI 已改用真实恢复，但占位类仍存在。
- `config/opening-rules.json` 和 `config/screen-layouts.1920x1080.json` 当前不由生产路径加载；部分规则/坐标由 C# 构造。
- 退出恢复的若干外层等待缺少统一的有限动作预算和截图证据。

## 核心代码入口

- `src/CurrencyWarsAssistant.Tasks/CurrencyWarsNavigation.cs`
- `src/CurrencyWarsAssistant.Tasks/OpeningRerollLoopCoordinator.cs`
- `src/CurrencyWarsAssistant.Tasks/CurrencyWarsRejectedOpeningRecovery.cs`
- `src/CurrencyWarsAssistant.Game/OpeningFilters.cs`
- `src/CurrencyWarsAssistant.App/MainViewModel.cs`（`BuildFilterSet` 和任务参数）
- `config/navigation-flow.json`

## 输入与输出

输入：页面分类、开局 OCR 快照、`OpeningFilterProfile` 集合、用户设置、取消令牌和前台状态。输出：导航结果、接受/拒绝原因、锁定的完整方案、恢复结果、最终停止原因和结构化日志事件。

## 依赖的其他模块

依赖 Vision 的页面/OCR 结果、Automation 的前台与输入保护、Game 的筛选语义、App 的用户配置；命中方案后可将锁定方案交给奖励关模块。

## 不允许破坏的既有规则

- 自动刷取不设总轮数或总时间上限；用户停止、满足条件或危险熔断才结束。
- 已有进行中对局必须安全停止，不能破坏用户进度。
- 未知页面默认 Esc 后重新识别；危险动作必须先确认页面并验证后置状态。
- 投资环境只正选；阵营和负面词条支持正选/排除；特殊组合必须全部同时出现。
- 多套方案之间是 OR；随机命中后锁定一套完整方案，绝不跨方案混合条件。

## 已知问题

- 当前产物没有 `RecoveryCompleted` 或多轮连续成功实机日志。
- 正向路径的旧 L4 证据不能证明当前 WGC/协调器版本已回归。
- 部分恢复循环没有统一动作次数预算，存在长期等待或重复输入风险。
- UI 当前默认会把命中流程继续扩展到未实机验证的奖励关。

## 下一步任务

1. 在获授权的当前产物冒烟通过后，先回归 HUD → 1-1 和 ActiveRun 安全停止。
2. 单独验证一次“不合格 → 退出 → 放弃结算 → 返回首页”。
3. 再验证 2～5 轮连续重刷、失焦/回焦和用户停止。
4. 给每个危险动作增加有限尝试、恢复路线、后置确认和截图事件。

## 验收标准

- 当前产物、游戏版本、分辨率、日志和最终状态绑定。
- 已有对局时零破坏性输入并明确停止。
- 不合格恢复闭环和连续多轮均有实机记录。
- 同时命中多方案时日志可证明只选一套，并且奖励参数没有混合。
- 识别暂态失败可恢复；持续未知或危险状态会安全停止。

## 测试与构建命令

```powershell
.\.tools\dotnet\dotnet.exe test tests\CurrencyWarsAssistant.Tests\CurrencyWarsAssistant.Tests.csproj -c Debug --no-restore --nologo --filter "OpeningFilterEvaluatorTests|OpeningRerollLoopCoordinatorTests|CurrencyWarsNavigationTaskTests|CurrencyWarsRejectedOpeningRecoveryTests|GameForegroundGuardTests"
.\.tools\dotnet\dotnet.exe build CurrencyWarsAssistant.sln -c Release --no-restore --nologo
```

## 最近可用构建

**尚未确认。** 最新候选为 `artifacts/live-test-30`；旧实机证据对应更早产物，不能直接迁移。

## 最后更新时间

2026-07-26（Asia/Shanghai）

## 2026-07-27 live-test-69 异常续跑与筛选冻结

- 自动运行期间冻结投资环境、竞争对手和敌人词条选择；运行中突变立即回滚，避免 9 项条件膨胀后错误跳过免费刷新。
- 非成功终态或异常不再结束自动模式，而是进入 `PassiveRecoveryMonitor`。监测阶段纯截图、纯分类，不发送输入；连续两帧确认货币战争主页或普通 HUD 后重新启动完整循环。
- `ActiveRunDetected` 只保持监测，不破坏进行中的对局；用户取消仍立即退出。
- 本机筛选恢复为 9 个原始 ID；联合定向回归包含在 89/89 中。候选 `artifacts/live-test-69`，尚未完成整局实机验证。
