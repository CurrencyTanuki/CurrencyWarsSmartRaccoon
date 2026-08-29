# ISSUE-021 修前复现：node-final 引用的战斗证据 PNG 未落盘

## 真实运行证据

RunId：`run-20260809-133022-r2-73793ac9e84b47a8b8d7366a69e24d02`。

递归枚举两份 node-final JSON 中所有 `run:<runId>/screenshots/...` 来源：

- node 1-1：`20260809-053110348.png`
- node 1-2：`20260809-053148998.png`、`20260809-053151226.png`、`20260809-053152148.png`

四个唯一 SourceId 对应文件 4/4 均不存在。该 run 的 `screenshots` 目录只有 8 张准备、过场或最终触发页截图，没有任何上述战斗证据。

这使用户看到行动值/伤害 Unknown 后，无法回放原帧判断是 OCR、区域、调度还是合并问题；node-final 的证据引用也不满足参照完整性。

## 代码链

- `Phase2NodeRetentionPolicy.ShouldPersistCurrent` 明确排除普通 Battle/Preparation。
- pending preparation 只在进入 Battle 时保存。
- tracker 在结算页或后继 Preparation 上 finalize 后，`SaveObservationAsync` 只保存“触发 finalize 的当前帧”。
- `SaveFinalNodeBattleAsync` 随后原样写入 final 中更早 battle/damage/action Evidence 的 SourceId，却没有保存这些来源帧。
- heartbeat finalize 也只保存当前 heartbeat 帧，存在同样断链。

## 修前生产基线

- `Phase2LiveCollectionService.cs`：`55E169B1FA9D826EEAA95CACA287CBAE1AAD8E55BD3FCDA8D68E96F8A72C5645`
- `Phase2OperationalStateTracker.cs`：`C0A298A537C2C465CC0DDE7D8DB8D4B52F316062BF136E09988AC9CE51AC31C3`

## 确定性集成红灯

测试 `FinalNodeBattleReferencesOnlyBattleScreenshotsThatExist` 用固定 marker 帧驱动真实：

`Phase2LiveCollectionService -> Phase2RealtimeRecognitionPipeline -> selector -> queue -> tracker -> LocalRunStore`

序列为 Prep 1-1 ×3、Battle 1-1 ×6、Prep 1-2 持续；CapturedAt 每帧递增 400ms，因此 selector 的 300ms/1s/2s 契约由数据时钟稳定触发，真实墙钟约 1 秒。

测试在收到 final battle 发布后读取真正生成的 node-final JSON，递归检查：

1. 每个 canonical run screenshot SourceId 对应 PNG 存在；
2. PNG 可解码且 marker 证明像素来自 Battle，而不是把后继 Preparation 冒充旧 SourceId；
3. 至少一个未被 final 引用的 Battle 捕获帧没有落盘，防止修复退化成保存全部战斗帧。

修前结果：node-final 正常生成，但首个引用 `run:run-final-evidence-contract/screenshots/20260809-133111200.png` 不存在，测试稳定失败。

- 测试源码 SHA-256：`E98B4CD996EDD23F6C6331C4ADFBBBC74C367AC3281A34145BD561E01F1F8949`
- 修前 TRX：`test-results/before-targeted.trx`
- TRX SHA-256：`25C5F51A2570514A78D7A680828EAEE3AF912EF7017B05FAE908B0C7CB1392F0`
- 结果：`0 Passed / 1 Failed`
- 测试时长：1.423 秒
- TestRun：`2026-08-09 15:22:03.802` 至 `15:22:09.700 +08:00`

红灯运行时两个生产文件仍分别为修前 SHA `55E169...C5645` 与 `C0A298...C31C3`；测试先于任何生产修改。

## 边界

本项只恢复 final 实际引用的战斗/行动/结算证据帧参照完整性。不得保存全部 Battle 捕获帧，不修改 OCR、tracker 选择规则、伤害数值、行动值、血量或历史 UI。
