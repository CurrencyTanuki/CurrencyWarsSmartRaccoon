# ISSUE-004 验证记录：完整场上槽位单次识别与按索引星级带

## 结论边界

本项只修复阵容识别器完整槽位列表的调用契约：未知角色证据不再因前/后台子调用重复，已识别
特殊占用单位不再因拆分丢失。没有修改装备识别、真实帧 pending 装备、奖励商店关闭、OCR 或
性能逻辑；这些相邻失败继续单独追踪。

## 最小生产改动

本项只改两个生产文件，测试未改：

1. `src/CurrencyWarsAssistant.Vision/CharacterCardRecognition.cs`
   - 在 `StarBand` 末尾新增 `BoardBySlotIndex`，保持既有枚举数值不变；
   - 该策略在完整场上列表内按 `index < 4` 使用 `FrontCenter`，其余使用
     `BackRight`；角色匹配、阈值、模板和星级算法本身未改。
2. `src/CurrencyWarsAssistant.Tasks/Phase2OperationalScreenshotAnalyzer.cs`
   - `RecognizeCharactersSafely` 的场上默认策略改为 `BoardBySlotIndex`；
   - 非应援路径移除前/后台两次调用与直接拼接，恢复一次完整列表调用；
   - 应援路径仍逐槽识别并恢复原始索引，备战席仍显式使用 `BenchRight`。

本项修改前/后 SHA-256：

| 文件 | ISSUE-004 前 | ISSUE-004 后 |
|---|---|---|
| `Phase2OperationalScreenshotAnalyzer.cs` | `BBE210BE6B21FA89FA486DAA690B5F965CF2E199EB0EE7AC71305868A49A2F97` | `6D13BD5DAB99BAC96AEF4255FAA79FD7FAD29A8964B76AA0F7B578BE3196B32A` |
| `CharacterCardRecognition.cs` | `26F370E4979033932FA7E91E2AEF914FA9DB7024996B634669974DCAF7BC80BA` | `CCA7358047EFC0899026A19002CC6EF784453C846A244788A052279EBA348E78` |

交接原件与工作副本的 tests 目录排除 `bin/obj` 后均为 267 个文件；逐相对路径 SHA-256
差异为 0。

## 构建验证

命令：

```powershell
dotnet build tests\CurrencyWarsAssistant.Tests\CurrencyWarsAssistant.Tests.csproj `
  -c Release --no-restore --nologo
```

结果：成功，0 警告、0 错误，用时 15.67 秒。

## 定点测试

最终源码上重跑修复前的两个确定性测试：2 通过、0 失败、0 跳过。

| 测试 | 修复前 | 修复后 |
|---|---|---|
| `UnknownFormationEvidenceUsesReferenceSpaceAtAnyResolution` | 同一区域产生 Front/Back 两条 | 只保留一条且参考坐标正确 |
| `KnownSpecialFormationUnitIsPreservedAsNonDecisionEvidence` | Formation 空 | 保留特殊单位和非决策待定证据 |

原始 TRX：`test-results/ISSUE-004-after-targeted.trx`  
SHA-256：`FFBF0FA7D1B8351D3E8BEA39C3379BFBF97FE22098E575F83A81B9415A4DD522`

## 关联回归

覆盖 000032 用户真值、角色卡真图、奖励商店阵容隔离、紧凑商店、两张旧参考图及实时准备帧，
共 20 项：16 通过、4 失败、0 跳过。

通过项包含：

- 000032 的前台角色、后台佩佩特殊单位、完整槽位索引、后台星级、4 个备战席角色；
- `LivePreparationRecognizesCharacterIdentityAndStarLevelPerSlot`；
- `ExpandedShopFormationUsesTheCompactReferenceLayout`；
- 3 项 `Phase2FormationRewardShopIsolationTests`；
- 本项两个定点测试。

4 个失败逐测试名与 ISSUE-003 全量基线比较，全部为既有装备问题：商店装备状态、实时准备帧
装备槽状态，以及 125924/132307 的 pending 高级装备为空；新增失败 0。两张旧参考图在失败前
已经通过 Formation 非空断言，不能误报为阵容为空。

原始 TRX：`test-results/ISSUE-004-related-regression.trx`  
SHA-256：`EB74728554873A161ED46F83C7357D6DDDFD7297F4D09086ECF0B6A04F0E4CFC`

## 全量回归

命令：

```powershell
dotnet test CurrencyWarsAssistant.sln -c Release --no-build --nologo `
  --results-directory audit\issues\ISSUE-004-formation-recognizer-slot-contract\test-results `
  --logger "trx;LogFileName=ISSUE-004-full-regression.trx"
```

结果：782 总计，769 通过、12 失败、1 跳过，用时 4 分 59 秒。相对 ISSUE-003 基线
753/28/1 逐测试名比较：新增失败 0，16 个旧失败本轮转绿。

其中只有以下 2 项由本次代码路径直接修复并有定点证据：

1. `UnknownFormationEvidenceUsesReferenceSpaceAtAnyResolution`
2. `KnownSpecialFormationUnitIsPreservedAsNonDecisionEvidence`

其余 14 项是未修改路径上的 OCR、战斗实时预算和奖励商店关闭测试，在机器负载较低的本轮
偶然转绿；此前同一代码反复红绿，仍标为“不稳定”，不计入 ISSUE-004 完成范围，也不从问题
队列删除。

原始 TRX：`test-results/ISSUE-004-full-regression.trx`  
SHA-256：`C1468C59B5A3C7591A1D657C213826C79E98C000AA80F8665D1434C3EA662CB6`

## 本项验收判断

- 修复前确定性复现：0/2，原始 TRX 已保存；
- 根因：前/后台拆分改变调用次数、局部索引和完整列表语义；
- 最小修改：两文件，只表达每槽星级带并恢复单次调用；
- 定点测试：2/2；
- 真图与相邻阵容回归：核心阵容断言通过，4 个旧装备失败明确隔离；
- 全量回归：新增失败 0；
- 独立审查员结论：批准，见下节。

## 独立审查员最终裁决（2026-08-09）

**批准 ISSUE-004。** 审查员直接读取最终源码、测试替身和所有原始 TRX，独立确认：

- 修复前 0/2 为确定性失败，根因准确解释 1→2 与 1→0；
- `BoardBySlotIndex` 追加在枚举末尾，旧值 0/1/2 不变，也未进入状态/历史序列化；
- 非应援 board 恢复完整列表单次调用；应援、bench、reward-shop compact 语义均保留；
- 测试 267/267 哈希无差异，构建与最终程序集/测试时间顺序一致；
- 定点 2/2、关联 16/20（4 个均为旧装备失败）、全量 769/12/1 且新增失败 0；
- 只有两个阵容契约测试可归因本项，其余 14 个偶然转绿继续标为不稳定。

非否决残余风险：未来调用方不得把 `BoardBySlotIndex` 用于不符合“前四槽+后台槽”顺序的
列表；当前唯一生产设置点受完整 board 契约约束，未发现实际遗漏或阻断性回归。
