# 0.2.802 更新说明（2026-08-03）

## 修复 1：战斗页节点号 1-1 被识别成 1-9（0.2.786 修复被回退后复发）

### 问题
实机日志：备战页正确识别"备战阶段1-1（90.2%）"，但进入战斗后日志显示
"**节点 1-9 战斗开始**"——污染对局归档。

### 根因
`Phase2OperationalScreenshotAnalyzer.cs` 的 `AnalyzeBattleAsync`（880 行）直接采信
战斗页数字 OCR 结果（`var node = nodeResult.Value`）。战斗页节点数字区域 OCR 会把
"1-1"读成"1-9"（数字区域误读，0.2.786 已知问题）。0.2.800 回退代码时，战斗页的
节点继承逻辑没有恢复。

### 修复
战斗页节点号**优先继承备战页已确认节点**（`GetStableRun(runId).LastPreparationNodeId`，
程序刚退出备战即进入战斗，节点号必然一致）；OCR 仅在无继承时兜底。

## 修复 2：刷开局非常慢（快速路径未启用）

### 问题
实机日志（19:06 会话）：主界面 → 备战 1-1 耗时 64 秒。逐帧分析发现**快速路径
（盲点连点）根本没有触发**——走的是普通导航（每页稳定识别 2 帧 + 用户手动中断重来）。

### 根因
快速路径触发条件被错误地绑定到设置页"快速刷开局模式"：
`options.FastReroll != FastRerollMode.Stable`（默认"稳定版"）→ 快速路径永不触发。

### 修复
按用户方案"**标准博弈选项 → 这些延时全部去掉**"：
- `OpeningRerollLoopCoordinator.cs` 两处导航调用：`FastPathFromHome = GameMode == Standard`
- `CurrencyWarsNavigation.cs` 4 处快速分支条件：`options.FastPathFromHome && GameMode == Standard`
- 设置页 3 模式（稳定/快速/极速）继续控制**备战页**行为，不再控制导航快速路径

## 验证
- 全量回归 **650/650 通过**
- 冒烟通过（进程正常启动）

## 交付
- exe: `artifacts\CurrencyWarsSmartRaccoon-0.2.802-win-x64-portable\CurrencyWarsAssistant.App.exe`

## 需实机验证点
1. **节点号**：战斗开始日志应显示"节点 1-1 战斗开始"（不再 1-9）
2. **速度**：标准博弈下主界面 → 备战 1-1 应大幅提速（盲点连点 4s + 敌人概览识别 + 位面 3s + 投资环境）
3. 整局：1-1 → 商店 → 1-2 → 战斗全流程
