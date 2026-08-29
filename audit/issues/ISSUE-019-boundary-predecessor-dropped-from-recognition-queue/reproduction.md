# ISSUE-019 修前复现：页面边界关键前驱被识别队列清理

## 用户可见现象

2026-08-09 的真实自动刷运行中，悬浮节点历史显示了节点和金币，但最终伤害、理论伤害、剩余行动值、完美状态和血量变化均为未知。

- RunId：`run-20260809-133022-r2-73793ac9e84b47a8b8d7366a69e24d02`
- 实际 UI 截图：`../ISSUE-018-operation-panel-intercepts-automation-input/screenshots/ui-after-operation-panel-20260809-1331.jpg`
- 截图 SHA-256：`13FDD9D65041509827E425FF813E49E48FD596BD0827513B660DC103F3048B04`

这不是整组识别未运行。准备页已经识别金币、血量、阵容、羁绊、装备等字段；节点最终文件也保留了部分伤害行。但是可靠的最终伤害与剩余行动值没有形成。

## 真实运行证据与边界

会话日志：`logs/test-session-20260809-132746.jsonl`，SHA-256 `6AE01D5074BFE96646A0A3961BB61BC67F33BE89750CB30D1ED0B568A363AAB6`。

- 节点 1-1：13:31:10.640 确认进入战斗，13:31:13.256 确认挑战成功；最终伤害证据仍停在 13:31:10.348 的早期战斗帧。
- 节点 1-2：13:31:48.450 确认进入战斗，13:31:53.973 确认挑战成功；最新保存的伤害证据为 13:31:52.148，但仍没有可靠行动值或最终伤害。

节点文件：

- `node-1-1-final.json`，SHA-256 `F9C7371F31EE61F9466BEC23A6F9EFEF1321496AA27CCED2CA1A32C785581AB5`：`PreBattleHealth=80`、`AllRecordedDamage=35,487,000`，但 `SelectedDamage/TotalDamage/RemainingActionValue` 均为空。
- `node-1-2-final.json`，SHA-256 `2360CE7844C23D2BA172E5E72103EDFDD4949CB9E682B7D0A471F5AFC5EF9151`：`PreBattleHealth=82`、`AllRecordedDamage=88,694,000`，但同样没有可靠最终值。

这些真实证据与本项队列缺陷高度一致，但当时日志没有记录被删除的帧序号、边界队列内容或该轮识别耗时。因此它们不能单独证明 1-1/1-2 的缺字段就是由本项造成；实际因果仍须修后真实短战斗复跑确认。

`AllRecordedDamage` 也不能直接冒充最终伤害。它包含身份或数量级仍不确定的行，紧凑历史继续只使用可靠的 `SelectedDamage ?? TotalDamage` 是正确安全边界。

## 生产可达性

`Phase2RealtimeFrameSelector` 在页面边界会把最近的战斗前驱和新页面当前帧按时间顺序全部标为 critical。正式捕获生产者依次入队；单消费者的一帧识别耗时超过 1.5 秒时会调用 `DropStaleFrames()`。

因此队列可以稳定形成：

1. 战斗末帧前驱（critical）；
2. 更近的未分类结算/战斗前驱（critical）；
3. 成功页或下一准备页当前帧（critical）。

旧实现取队列中的最后一个 critical。当队尾当前帧本身也是 critical 时，`lastCritical == last`，清理循环会删除它之前的所有关键前驱，只留下新页面当前帧。

## 修前基线

- `src/CurrencyWarsAssistant.Tasks/Phase2RealtimeRecognitionPipeline.cs`
  - SHA-256：`BD1666448830B6C569ABD7453C3E0F61CD74B374CF334DF7F2F963620F475DB2`
- 初始测试文件 `Phase2RecognitionQueueTests.cs`
  - SHA-256：`FBE5E023F541C6D9FB18C9D85EE3648C3CDBB3D4AE14789FBBEB90014FD525A2`

## 第一组确定性红灯

- 测试：`DropStaleFramesWhenLatestFrameIsCriticalKeepsItsNewestCriticalPredecessor`
- 输入：`critical seq0 -> critical seq1 -> critical seq2`
- 期望：依次取到 `seq1, seq2`
- 修前实际：第一次直接取到 `seq2`
- TRX：`test-results/before-targeted.trx`
- TRX SHA-256：`C3194391EFBE5042BB280F5622FE82776F17412926BFF98A36FCCE7472FAF403`
- 结果：`0 Passed / 1 Failed`
- 运行区间：`2026-08-09 14:01:51.112` 至 `14:01:56.435 +08:00`

测试和红灯均早于任何候选生产修改；当时生产源码仍为上述批准基线 SHA。

## 扩展合同红灯

在最终生产修改前再加入两条边界测试：

1. 合法混合拓扑 `[C0,C1,R2,C3]` 应只保留 `[C1,C3]`；旧实现首个出队值为 `3`，期望 `1`。
2. 由真实 selector 构造 `battle -> unclassified settlement predecessor -> preparation` 边界组，再逐项入队并清理；旧实现只留下 preparation `seq3`，期望首先留下 settlement predecessor `seq2`。

- TRX：`test-results/before-expanded-contracts.trx`
- SHA-256：`E61B7F3E6A34C8C5CBB0269B969961759A00F1E3B47CAADEBE7FBC9974103763`
- 结果：`0 Passed / 2 Failed`
- 运行区间：`2026-08-09 14:46:20.479` 至 `14:46:22.990 +08:00`
- 两个测试文件写入时间：`14:46:00 +08:00`
- 最终生产修改写入时间：`14:46:55.991 +08:00`

因此扩展红灯同样运行于未修复的生产实现。

## 前置安全依赖

第一次候选队列补丁曾使队列测试变绿，但审查发现保留更多战斗帧会暴露既有的“战斗裸千分位被按结算万位推断”问题，并可能被两帧稳定规则提升为 Known。候选补丁因此撤回；先完成并独立批准 ISSUE-020，再重新实施本项。这样避免把“历史为空”变成“历史显示错误巨额伤害”。

## 本项边界

本项只修复“selector 已选择的关键前驱在进入识别前又被队列清理删除”。不修改伤害 OCR、行动值区域、历史投影、HTML renderer、血量确认、截图保存策略、页面分类或自动化点击。
