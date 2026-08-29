# ISSUE-019 验证记录

## 最终修改

生产文件：

- `src/CurrencyWarsAssistant.Tasks/Phase2RealtimeRecognitionPipeline.cs`
- 最终 SHA-256：`8A0D2C887FAD71DF366D1075289E97807D5694C2A504E534BE1F36EF2C7711C6`
- 文件写入时间：`2026-08-09 14:46:55.991 +08:00`

相对 ISSUE-010/018 批准基线 `BD166644...5DB2`，唯一生产差异位于 `DropStaleFrames()`：从队尾之前查找最近 critical，并按两个节点引用遍历删除其他项。selector、Enqueue、容量、1.5 秒阈值、识别器和 tracker 均未改。

测试文件：

- `Phase2RecognitionQueueTests.cs`：SHA-256 `8AC94652B4F88476BF20DD2C3046D9F4286BE24D03B1F425ACD933A455AE2713`
- `Phase2RealtimeFrameBufferTests.cs`：SHA-256 `0AD15210319B12080B9E7EC4195420C4D95C63B71E2B4DC5F3BE3CA026E036C5`

ISSUE-020 前置修复保持冻结：

- `Phase2OperationalScreenshotAnalyzer.cs`：`63F8492BC8FB16FBA475A13DE863040EF7E487F09D9294F4CCA53D21011C9C95`
- `Phase2OperationalCollectionTests.cs`：`0A5CB0486E0D42FED4911871025B699685BC6656914F97C10327BC095CCF310A`

## 修前红灯

| 证据 | 结果 | SHA-256 | 失败事实 |
|---|---:|---|---|
| `before-targeted.trx` | 0/1 | `C3194391EFBE5042BB280F5622FE82776F17412926BFF98A36FCCE7472FAF403` | all-critical 首项 expected seq1，actual seq2 |
| `before-expanded-contracts.trx` | 0/2 | `E61B7F3E6A34C8C5CBB0269B969961759A00F1E3B47CAADEBE7FBC9974103763` | mixed expected1/actual3；selector-to-queue expected2/actual3 |

两组 TRX 均早于最终生产修改；红灯顺序和源码时间边界成立。

## 修后自动化

| 测试集 | 结果 | SHA-256 |
|---|---:|---|
| `after-targeted-final.trx` | 3 Passed / 0 Failed | `F6DF876C64B0EEEE4BF29A3062D08C9602986096371DA9C24CC65F41AB6EDC30` |
| `after-related-final.trx` | 49 Passed / 0 Failed | `AC171F05A33B1EC1BECCA9E283FF5E3DA5FC529A77D6AD62A4CD8B08D869E946` |
| `after-full-final.trx` | 820 Passed / 0 Failed / 2 NotExecuted | `C53EDE39C1F590572E26EBB5FD7161DC84B97E6A9B65F91732756945B4FB6574` |

全量运行区间：`2026-08-09 14:48:11.856` 至 `14:51:35.846 +08:00`。

VSTest 的 `<Counters notExecuted>` 仍写为 0，但 822 条 `UnitTestResult` 按 outcome 独立计数为 820 Passed、2 NotExecuted；两项均是项目既有的显式跳过性能测试。

## 与 ISSUE-018 全量基线逐测试名比较

基线：`ISSUE-018/.../after-full-rerun.trx`，SHA-256 `37469DB855E7738D8937DB18FE35546EDA96D432535AEA51754476C05C0D8393`，814 个唯一测试名；最终：822 个唯一测试名。

- 新增：8，全部 Passed；
  - ISSUE-019：3 个队列/selector 合同测试；
  - ISSUE-020：5 个战斗千分位语义测试；
- 缺失：0；
- 既有结果变化：0。

因此本项与前置 ISSUE-020 没有造成已批准测试结果回归。

## Release 构建

- `release-build-final.log`：SHA-256 `E5019B17CBD6A99C76F5FB24213CBBC0548C0EB32E116795A0F0A51CEC1CAE1F`
- `release-build-final.binlog`：SHA-256 `698839118636195D6C0F1BE9CF09CBFEF9E35DB01487DBC20E8B7360E2726CDC`
- 结果：0 warnings / 0 errors

关键产物：

- `CurrencyWarsAssistant.Tasks.dll`：1,168,896 bytes；`DC951C3E0B12E1A88BAA102CFA52425285995369A10A39F80EA6ECE2BBACF857`；写入 `14:47:11.993`
- `CurrencyWarsAssistant.Tests.dll`：844,800 bytes；`12D1B0B91BD02CE6DFF356B9D730AC1015A76CDEDD3CDAF6BA844E88D6F00FB9`；写入 `14:47:15.942`
- `CurrencyWarsAssistant.App.dll`：664,576 bytes；`40EAE0D95A01309E6AEBFC105CCE1215D40E8EF574FD4B5A6C8098738A1D0432`；写入 `14:47:14.610`

测试程序集和正式产物均晚于最终源码；最终 full 使用该 Release 测试程序集。

## 当前结论边界

自动化证明超过两项的积压队列现在按合同裁剪为“最新帧 + 其前最近 critical”，并保持 semaphore 一致；既有的两项以内快速返回未改。它没有证明用户真实 1-1/1-2 的全部历史字段已经恢复。真实短战斗复跑、采用帧落盘、行动值、successor health 和两个历史界面仍需后续独立验收。

## 独立只读终审

结论：`APPROVE`（严格限定为本项队列代码、测试补丁及自动化验收边界）。

审查员独立复核了两组先红时序、最终三个源码/测试哈希、mixed 与 selector-to-queue 算法、semaphore 不变量、3/3、49/49、822 项逐名差分、Release 构建，以及 ISSUE-020 冻结状态。审查明确不把该批准外推为“真实短战斗字段已恢复”或“项目已完成”。
