# 货币战争智能狸 0.2.759 更新说明（回归修复）

> 紧急修复 0.2.758 引入的严重回归：**主界面/开局阶段被误判为"未知节点战斗"，导致无法正常记录**。

## 回归根因

0.2.758 为修复"快速跳过结算/备战页时单帧信息丢失"，加入了"非战斗页面 SceneTransition 强制关键帧"。
但该条件过宽：**主界面（`currency_wars_home`，`Phase2PageFamily.Main`）的画面切换也被当作页面边界**，
导致用户仅在主界面/开局阶段操作时，关键帧队列被刷爆
（日志实锤：`08:34:49 [WRN] 关键页面识别队列已满`），识别错乱后主页帧被误判为战斗
（日志：`08:34:53/08:35:06 [INF] 节点 未知节点 战斗开始`）。

## 修复内容

`Phase2RealtimeFrameSelector.Observe`（Phase2RealtimeRecognitionPipeline.cs）：
SceneTransition 强制关键帧**收窄为仅备战（Preparation）/结算（BattleSettlement）页面之间的切换**：

- ✅ 快速跳过结算/备战页的单帧抓取能力**保留**（原修复目标不丢）
- ✅ 主界面（Main）、未知页面等画面切换**不再触发**关键帧 → 关键帧队列不再被刷爆
- ✅ 主界面开局识别不受影响：主页在 fast classifier 的正常页面列表中，正常页面切换仍走原有关键帧逻辑
- ✅ 战斗页面动画仍不触发（0.2.758 已排除）

## 回归测试

- 新增 `MainPageSceneTransitionsDoNotQueueCriticalFrames`：主界面（Main）切换不产生关键帧。
- 全量回归：**560/560 通过，0 失败，0 跳过**（含全部既有测试 + 新回归测试）。
- 冒烟：解压启动 10 秒进程存活、主窗口正常。

## 交付物

- 候选发布包：`artifacts/CurrencyWarsSmartRaccoon-0.2.759-win-x64-portable.zip`（162.8 MB，1736 条目）
  SHA-256：`4EFDA90514B36E3C87B530BDFCA690752C0FB2A28883EC730AAEBA47CA95561B`
- 回退基线：0.2.751 原包；0.2.756 / 0.2.757 / 0.2.758 均保留在 artifacts/。
