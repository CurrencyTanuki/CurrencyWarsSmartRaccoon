# 0.2.793 更新说明（2026-08-03 傍晚）

## 修复：自动战斗状态检测"消失/不再判定"（用户实测反馈）

### 现象
- 用户实测：进入奖励关战斗后，自动战斗状态检测（AutoBattle：
  检测自动战斗是否开启，未开启则按 V）**不再执行/看不到判定输出**。
- 日志证据对比：
  - 0.2.789（03:41）：AutoBattle 事件 12 个，完整检测+开启+验证
  - 0.2.790（08:25）：AutoBattle 事件 **0 个**（失焦 92 秒期间检测全停）
  - 0.2.791（10:15）：AutoBattle 事件 3 个（观察 3 次后 V 发送，之后因
    反复失焦+动画帧判定，**V 发送后的开启验证没跑**）

### 根因
- **不是逻辑被删除**：`RewardStageAutomation.Battle.cs` 的 AutoBattle 检测
  代码（355-435 行）完整存在，`RewardAutoBattlePolicy` 完整。
- 真正原因：战斗观察循环的捕获走 `CaptureForegroundAsync` →
  `foregroundGuard.WaitUntilForegroundAsync`——**失焦时无限阻塞等待前台**。
  用户测试期间窗口反复失焦（用户主动切走/测试），检测整段暂停，
  观感就是"逻辑没了、不再判定"。
- 失焦保护（0.2.776 加的 UIPI 权限配套）对**输入**是必要的，
  但对**只读检测**（截图+分类+自动战斗状态观察）过于严格。

### 修复（保守，只改捕获路径，不改识别逻辑）
1. `RewardStageAutomationController` 新增可选依赖 `IGameWindowService`；
2. 新增 `TryCaptureAsync`（非阻塞捕获）：失焦时不等待前台，
   直接按窗口客户区截帧——战斗中的只读检测（页面分类、AutoBattle 状态）
   在失焦期间**继续执行**；画面被遮挡时分类为 Unknown 自然跳过，不误发输入；
3. `WaitForBattleSuccessAsync` 观察循环改用 `TryCaptureAsync`；
4. **V 键发送前仍走前台守卫**（`WaitUntilForegroundAsync`）——输入安全不变；
5. 未注入窗口服务（测试构造）时回退到原前台捕获，行为不变。

### 测试
- 全量回归 **653/653 通过**（含 33 个 Reward/Shop/Stage 相关测试）。
- 既有 AutoBattle 视觉检测测试（RewardVisualDetectorTests）通过。

## 需实机验证
1. 进入奖励关战斗后，AutoBattle 检测日志（AutoBattleFrameObserved /
   AutoBattleConsensusObserved）**持续输出**，不再因切窗暂停；
2. 自动战斗未开启时仍会按 V（EnableAutoBattleAttempt），且发送后
   **继续验证**（AutoBattleFrameObserved 观察 4+，金色像素变化确认开启）；
3. 窗口失焦期间只读检测继续，但**不会误发任何输入**。
