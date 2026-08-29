# ISSUE-010 修前复现：跳帧清理误删最新关键帧

## 用户影响

实时识别一帧超过 1.5 秒后，管线会调用 `DropStaleFrames()` 清理积压帧。契约要求保留“最新关键帧 + 最新帧”，以免页面边界、节点、结算或终局证据丢失；当前实现会静默删除最新关键帧，只留下最后一张普通帧。程序仍可能继续运行，但节点、结算或整局封存会缺失。

## 未修改生产代码时的基线

- 工作副本生产文件：`src/CurrencyWarsAssistant.Tasks/Phase2RealtimeRecognitionPipeline.cs`
- SHA-256：`2E967F695DFDBC011CE7BAA51001987B7198B9C009D83AA7D022485FF8BD147D`
- 桌面交接原件同名文件 SHA-256：`2E967F695DFDBC011CE7BAA51001987B7198B9C009D83AA7D022485FF8BD147D`
- 工作副本测试文件：`tests/CurrencyWarsAssistant.Tests/Phase2RecognitionQueueTests.cs`
- SHA-256：`DDC89F1B742106EE6765C383D6891829948C10412516EA26CCF4527E5E830701`
- 桌面交接原件同名测试文件 SHA-256：`DDC89F1B742106EE6765C383D6891829948C10412516EA26CCF4527E5E830701`

因此缺陷和漏测均来自交接版本，不是本轮先前修复引入。

## 确定性复现序列

构造队列：

1. 入队关键帧 `C0`；
2. 入队关键帧 `C1`；
3. 入队普通帧 `R2`；
4. 调用 `DropStaleFrames()`；
5. 正确结果应依次出队 `C1`、`R2`；
6. 修前实际第一项即为 `R2`，`C1` 已被删除。

已新增一个独立 xUnit 回归测试覆盖该序列，并在生产代码保持上述原始 SHA-256 时运行：

- 测试：`DropStaleFramesWithMultipleCriticalFramesKeepsNewestCriticalBeforeLatestFrame`
- 修前结果：0/1，命令退出码 1；
- 错误：第一项 `IsCritical` 期望 `true`、实际 `false`，证明第一项已经是普通帧 `R2`；
- 原始 TRX：`test-results/ISSUE-010-before-focused.trx`
- TRX SHA-256：`F41FB57EFC60DA96C7D3BEAB3A92CF11FCC28E5441F4A20C1433D5166C5767E8`
- 运行时生产文件 SHA-256 仍为 `2E967F695DFDBC011CE7BAA51001987B7198B9C009D83AA7D022485FF8BD147D`。

## 现有测试为何没有发现

现有 `DropStaleFramesKeepsLatestCriticalAndDequeueIsConsistent` 连续加入普通帧 0、1、3、4 和关键帧 2；普通帧在 `Enqueue` 中相互替换，所以调用清理前实际只有 `[C2, R4]`。`DropStaleFrames()` 因 `items.Count <= 2` 直接返回，从未执行有缺陷的循环。

## 真实调用可达性

- 帧选择器可一次保留多张前驱关键帧并再加入当前关键帧；
- 成功识别耗时超过 `FrameSkipThreshold`（1.5 秒）时，识别消费者会调用 `DropStaleFrames()`；
- 最新全量中的识别性能样本均明显超过 1.5 秒，因此清理分支不是理论死代码；
- 随后到达的普通帧可形成 `[关键帧, 关键帧, 普通帧]`，与确定性单测序列一致。
