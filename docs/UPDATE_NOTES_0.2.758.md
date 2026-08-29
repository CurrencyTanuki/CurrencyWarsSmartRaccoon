# 货币战争智能狸 0.2.758 更新说明（候选审计版）

> 用户实机测试一批后的统一修复版本。修复了 4 项累积问题（见 `PENDING_USER_FIXES.md`）。

## 修复清单

### 1. 悬浮窗默认位置（用户截图确认）
- 日志悬浮窗默认位置：屏幕**左上角贴近顶部**（原为顶部下移 120–200px，现改为 `WorkArea.Top + 14`）。
- 历史节点悬浮窗默认位置：屏幕**右上角**（`Top` 由 48 微调为 20，与用户截图一致）。
- 逻辑位置：`MainWindow.xaml.cs` 的 `PositionLogOverlay` / `PositionOperationPanel`。

### 2. 补给选择页（reward_shop）不再污染备战数据
- `Phase2OperationalStateTracker.ObserveHealth`：补给页帧血量不再采集（防止污染回填与 pre-battle 血量）。
- `HistoricalDashboardProjection.Observe`：补给页帧金币不再写入节点 PreBattleGold、不再回填上一节点 EndingGold。
- 补给选择节点（1-5 型，无战斗）本身不会产生 FinalBattle，因此不会出现在历史图表 Nodes 中；有战斗的补给节点（reward_battle）不受影响。

### 3. 结算页被快速跳过时奖励缺失 —— 备战金币差分回填
- `HistoricalDashboardProjection.RecalculateEconomyDeltas`：已封存节点若 `GoldReward` 缺失且下一节点备战金币差分可算，用差分回填奖励列（如 1-4 结尾金币 23 − 1-3 结尾 15 = **+8**）。
- 结算页奖励后续到达时 `ObserveFinalBattle` 会覆盖该推断值（结算页优先）。
- 已知局限（用户接受）：奖励节点间因晶矿加金币差分可能不准。

### 4. 快速跳过时单帧信息丢失 —— 场景切换强制关键帧
- `Phase2RealtimeFrameSelector.Observe`：非战斗页面的 `SceneTransition`（结算→备战、备战→下一节点等快速切换）强制按页面边界处理，把切换前后最近帧送完整 OCR——玩家快速跳过结算/备战页时也能抓住那一两帧。
- 战斗页面内的画面变化仍视为动画，不触发关键帧（避免战斗动画刷爆识别队列）。
- 同时修复 `Phase2OperationalScreenshotAnalyzer` 结算伤害 `rows==0` 时 evidence.Summary 误用帧尺寸字符串（rawOcr "2560x1440" 假象）的问题。

## 新增测试
- `Observe_BackfillsRewardFromPreparationGoldDeltaWhenSettlementMissing`：结算奖励缺失时金币差分回填奖励列。
- `Observe_RewardShopFrameDoesNotPollutePreparationGold`：补给页金币不回填上一节点 EndingGold。

## 测试与交付
- 定向回归（历史投影 / 帧缓冲 / 关键帧策略）：39/39 通过。
- 全量回归：**557/557 通过，0 失败，0 跳过**（含新增测试）。
- 未做实机对局验证（本机未安装游戏）；"快速跳过"场景需用户实机确认单帧抓取效果。

## 交付物
- 候选发布包：`artifacts/CurrencyWarsSmartRaccoon-0.2.758-win-x64-portable.zip`（162.8 MB，1736 条目）
  SHA-256：`BD04F7C984E59938F03C3412956D640C04E345E69ADF48325A08F74056EADD39`
- 启动冒烟：解压后启动 12 秒进程存活、主窗口正常（2026-08-01 实测）。
- 回退基线：0.2.751 原包；上一候选 0.2.756 / 0.2.757 均保留在 artifacts/。
