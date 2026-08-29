# ISSUE-004 复现记录：前/后台拆分破坏阵容识别器槽位契约

## 用户影响

阵容识别不完整时，程序必须保留已识别部分和待定证据。当前非应援路径把一份完整场上槽位
列表拆成前台、后台两次调用后直接拼接，导致识别器返回的局部槽位索引和“完整列表”语义丢失：

- 一条未知角色证据可被重复保存成 Front/Back 两条；
- 已识别的特殊占用单位可整条消失；
- 后续阵容、站位、装备、羁绊及历史链会收到错误或缺失的降级证据。

## 独立范围

本项只处理阵容识别器调用契约和前/后台星级带选择。以下相邻问题不纳入本项：

- 125924/132307 真图失败位于 `pending AdvancedEquipment` 为空；两图的 Formation 已通过
  非空断言，属于后续装备证据问题；
- 真图中的空槽/装备图标误判；
- 奖励商店关闭失败；
- 识别耗时超预算。

## 修复前复现

命令：

```powershell
dotnet test tests\CurrencyWarsAssistant.Tests\CurrencyWarsAssistant.Tests.csproj `
  -c Release --no-build --nologo `
  --filter "FullyQualifiedName~UnknownFormationEvidenceUsesReferenceSpaceAtAnyResolution|FullyQualifiedName~KnownSpecialFormationUnitIsPreservedAsNonDecisionEvidence" `
  --results-directory audit\issues\ISSUE-004-formation-recognizer-slot-contract\test-results `
  --logger "trx;LogFileName=ISSUE-004-before.trx"
```

结果：0 通过、2 失败、0 跳过。

1. `UnknownFormationEvidenceUsesReferenceSpaceAtAnyResolution`
   - 期望：同一参考区只有 1 条未知角色待定证据；
   - 实际：2 条，分别标为 `formation-Front-1` 与 `formation-Back-1`；
   - 两条的 Region、TemplateId、Confidence 均相同。
2. `KnownSpecialFormationUnitIsPreservedAsNonDecisionEvidence`
   - 期望：保留 1 个已识别特殊单位及对应非决策待定证据；
   - 实际：Formation 集合为空。

原始 TRX：`test-results/ISSUE-004-before.trx`  
SHA-256：`3D88FC756E2C90C5B3A95B2D499FD60F0228A6E2EEC14388CCEE3362F8097844`

## 根因证据

`RecognizeCharactersSafely` 的非应援路径在场上槽位多于 4 个时：

1. 以前 4 个槽调用一次识别器；
2. 以剩余槽再调用一次识别器；
3. 直接 `front.Concat(back)`。

`ICharacterCardRecognizer` 的返回 `SlotIndex` 是相对本次输入列表的索引；第二次调用从 0
重新计数，却没有恢复为完整场上列表索引。更重要的是，识别器实现或降级适配器可以依赖
一次收到完整槽位列表：复现中的 uncertain 适配器会在“每次调用的首槽”各返回一次，special
适配器只在收到完整 Preparation 列表时返回特殊单位。拆分因此稳定产生 1→2 和 1→0。

拆分是为了让前 4 个槽使用 `FrontCenter` 星级带、后续槽使用 `BackRight` 星级带；需求本身
正确，但不应靠改变识别器调用次数和列表语义实现。

## 最小修复方向

在 `StarBand` 中表达“一份完整场上列表中前 4 个为 Front、其余为 Back”的选择方式，让
`OpenCvCharacterCardRecognizer` 按完整列表索引解析每槽星级带；`RecognizeCharactersSafely`
恢复单次完整列表调用。应援路径仍逐槽调用并显式选择星级带，备战席仍使用 `BenchRight`。
不修改测试、阵容业务模型、槽位坐标、角色阈值、模板或装备逻辑。

