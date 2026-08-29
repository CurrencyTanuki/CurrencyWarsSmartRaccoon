# ISSUE-003 验证记录：小字滑窗仅对商店等级显式启用

## 结论边界

本项修复并验证了同一根因导致的金币、行动值和难度数字误识别；没有修改截图区域、模板、
OCR、经济候选仲裁、状态合并、存储或历史界面。项目全量测试仍有 28 项既有失败，因此本记录
只支持关闭 ISSUE-003，不能据此宣称识别系统或项目整体完成。

## 最小生产改动

与只读交接原件比较，本项只改两个生产文件，未改测试：

测试目录排除 `bin/obj` 后，交接原件与工作副本均为 267 个文件；逐相对路径 SHA-256
比较差异为 0。

1. `src/CurrencyWarsAssistant.Vision/UiDigitSequenceRecognition.cs`
   - 普通字段的滑窗高度改为 24、28、32、36、40、48、56、64；
   - 保留含 16/20 的专用高度表；
   - `Recognize` 新增默认值为 `false` 的 `includeSmallSlidingGlyphs` 参数；
   - 只有显式请求时才搜索 16/20px 小字。
2. `src/CurrencyWarsAssistant.Tasks/Phase2OperationalScreenshotAnalyzer.cs`
   - 仅 `ReadStoreLevelAsync` 的 `Lv.` 反相拉伸路径传入
     `includeSmallSlidingGlyphs: true`。

原件/修后 SHA-256：

| 文件 | 交接原件 | 修后工作副本 |
|---|---|---|
| `UiDigitSequenceRecognition.cs` | `F2CA6FF090356E9066CBB49B91CB61ABCD65406E5A0454D18D07A4561D5B6145` | `0C24127EB4FE979C859DB9BC9F8E3F671184F39EE908E5E9A9013FF3C7F1AEBE` |
| `Phase2OperationalScreenshotAnalyzer.cs` | `7097A7877581D61133DDB60365F0CB90E32343F9D65BA3A73C22E42A5CC09633` | `BBE210BE6B21FA89FA486DAA690B5F965CF2E199EB0EE7AC71305868A49A2F97` |

## 被否决方案的回归证据

初始方案把混合高度序列整体降级为 Unknown。该方案只使金币 32 和 3 的测试转绿，金币 23、
商店金币 6、行动值 80 仍失败；`store-diagnostic.trx` 还记录商店等级反相路径为
`recognized=False/value=null`。因为 `Lv.7` 的真实右端数字会形成 16px 候选，任何全局
“混尺度即 Unknown”规则都会损坏成熟功能。该方案已完全撤销。

| 原始证据 | SHA-256 |
|---|---|
| `test-results/ISSUE-003-after-initial.trx` | `4AB728C341A3280EC1F6F54E9A64AF3ADC0C8ACF95867C93B765082CED3DDDCE` |
| `test-results/store-diagnostic.trx` | `552AE0903A6C6A013E61C7CB0921047EDD030BEE1F7972F71B671E5ECE928B1D` |

## 构建验证

命令：

```powershell
dotnet build tests\CurrencyWarsAssistant.Tests\CurrencyWarsAssistant.Tests.csproj -c Release --no-restore --nologo
```

最终结果：成功，0 警告、0 错误，用时 15.50 秒。此前一次中间构建因仍存活的 testhost
短暂锁定输出出现 MSB3026；测试宿主退出后用同一最终源码重跑，干净构建 0/0，故不作为
最终结果隐藏或忽略。

## 定点真实截图测试

最终源码上运行 7 个测试，结果 7 通过、0 失败、0 跳过，用时 11 秒：

| 验证对象 | 结果 |
|---|---|
| 聚合本地 UI 数字 | 难度 126、工具 2、消费 2、利息 3、金币 32 均通过 |
| 单金币 | 3，通过 |
| 完整准备状态 | 金币 32，通过 |
| 商店卡边界 | 金币 23，通过；wide=23、narrow=3 |
| 扩展商店区域 | 金币 6，通过 |
| 共享行动值识别器 | 行动值 80，通过 |
| 商店等级回归 | `Known/7`，通过 |

原始 TRX：`test-results/ISSUE-003-after-scoped-small.trx`  
SHA-256：`A585A135BB5C44BEA8F7862213A0C26D9671B23FCE5D2D57A981AD8FAB64050A`

## 关联功能回归

运行全部 `Phase2OperationalCollectionTests` 和 `UserReferenceGroundTruthTests`：

- 总计 182：175 通过、7 失败、0 跳过；
- 7 个失败与 ISSUE-002 全量基线逐测试名比较，全部为既有失败，新增失败 0；
- `ProductionOcrSeparatesRoundZeroFromTwoDigitActionValues` 通过；
- 7 组 `LiveCapturedBattleFramesProduceCoreStateWithinRealtimeBudget` 均通过，节点、行动值和伤害
  字段断言通过；
- 本项 6 个原失败和商店等级 7 全部通过。

原始 TRX：`test-results/ISSUE-003-related-regression.trx`  
SHA-256：`E25886224FF8974E4CDF652E7D3715750A181D7CD514FA18D4F420E4C2F77E0E`

## 全量回归

命令：

```powershell
dotnet test CurrencyWarsAssistant.sln -c Release --no-build --nologo `
  --results-directory audit\issues\ISSUE-003-mixed-scale-digit-candidates\test-results `
  --logger "trx;LogFileName=ISSUE-003-full-regression.trx"
```

结果：782 总计，753 通过、28 失败、1 跳过，用时 9 分 35 秒。与 ISSUE-002 基线
747/34/1 逐测试名比较：

- 新增失败：0；
- 消除失败：6；
- 仍有失败：28，全部为基线中已存在的其他问题。

消除的 6 项恰好是修复前保存的本项失败：

1. `ExpandedEconomyCropDoesNotPrependCoinIconToSingleDigitGold`
2. `ExpandedShopEconomyUsesTightEvidenceInsteadOfAdjacentZero`
3. `GlowingActionValueUsesLocalizedDigitTemplatesWithoutRelaxingOcr`
4. `LocalizedUiDigitsReadLivePreparationResourcesWithoutGeneralOcr`
5. `PreparationEconomyKeepsLeadingDigitAtShopCardBoundary`
6. `PreparationSnapshotReadsAbsoluteHealthAndGoldFromTightRegions`

原始 TRX：`test-results/ISSUE-003-full-regression.trx`  
SHA-256：`25FBA29971A0DBE0189EFD7145B5EAB38185DD2CC6555B4415A2E54C0E94D626`

全量运行中部分战斗帧仍因实时耗时上限失败；同一最终二进制在上述关联回归中字段和值均通过，
而且这些测试名已存在于 ISSUE-002 的失败集合，故归入后续独立性能问题，不冒充本项完成证据。

## 本项验收判断

- 复现输入、错误值和原始 TRX：已保存；
- 根因：已闭环到全局误启用 16/20px 专用小字滑窗；
- 初始错误方案：已用回归证据否决并撤销；
- 最小修改：两个生产文件，测试/区域/OCR/业务合并未改；
- 定点测试：7/7；
- 关联回归：无新增失败；
- 全量回归：净消除 6 项、无新增失败；
- 独立审查员结论：批准，见下节。

## 独立审查员最终裁决（2026-08-09）

**批准 ISSUE-003。** 审查员直接读取交接原件、工作副本、5 张输入和全部原始 TRX，独立确认：

- 根因成立，被否决的全局混合尺度规则已完全撤回；
- 排除生成物后，160 个生产源码文件中只有上述两个文件变化；
- 全仓只有 `ReadStoreLevelAsync` 传入小字 opt-in，另一条等级路径使用 OCR，不存在遗漏；
- 267 个测试文件逐路径 SHA-256 差异为 0；
- 定点 7/7，关联失败均为既有，全量新增失败 0、恰好消除本项 6 个失败；
- 28 个既有性能、OCR、阵容、装备管线和历史字段等失败仍明确保留。

非否决残余风险：未来未覆盖的低分辨率画面若普通字段的真实字号只有 16/20px，默认滑窗路径
可能漏识别。本次批准只覆盖当前真实图集与既有降级路径，不外推为所有分辨率已经验证。
