# ISSUE-005 验证记录

## 结论边界

本项修复了一个确定的数据语义错误：共享同一视觉资源、无法唯一解析身份的装备图标，不再因 `confidence >= 0.60` 被强制保存为某个具体装备 ID，而是保留为不可驱动决策的 `Unknown`，同时写入 pending 并保留全部候选。

本项不宣称以下问题已经完成：

- `preparation-1-7-user.png` 的空装备槽仍被前景检测判为有内容；
- `preparation-shop-1-4.png` 的视觉真值候选应为 `066/105`，匹配器仍给出 `080/119`；
- 全量测试仍存在历史界面 Health、工具脚本缺失、性能、OCR 和奖励商店关闭等既有问题；
- 尚未进行最终软件包和两个可见历史界面的整体验收。

## 修改

生产代码仅修改：

- `src/CurrencyWarsAssistant.Tasks/Phase2OperationalScreenshotAnalyzer.cs`
- 修改前 SHA-256：`6D13BD5DAB99BAC96AEF4255FAA79FD7FAD29A8964B76AA0F7B578BE3196B32A`
- 修改后 SHA-256：`E8A159177C1C3C5D13FA5D9F5B286D2EAC6730A7A75EE7D5E854E367DAA283E8`

语义差异只有：

1. 删除装备专用 `EquipmentKnownConfidenceThreshold = 0.60`；
2. `Equipped` 分支从 `item.IsKnown || confidence >= 0.60` 改为只接受 `item.IsKnown`；
3. 注释明确区分视觉相似度与唯一身份。

没有修改前景检测、槽位区域、模板匹配、图标资产、候选排序、数据合并或界面代码。

新增一个真实截图契约测试：

- `AmbiguousShopEquipmentRemainsNonDecisionEvidence`
- 测试文件修改前 SHA-256：`E7CDB0DCFEA00DB11FD951455975D851767FC87044018A0A8CFFAEEC994E80A5`
- 修改后 SHA-256：`AEFF9CF73512061049BF3ABC53A4AE8840C84BC13F1FFAF9C4894B631AA2D32B`
- 与交接原件对比只有一个新增测试块；未修改既有测试或既有断言。
- 新测试在生产修复前为 0/1，修复后为 1/1。

## 构建

命令：

```powershell
dotnet build .\CurrencyWarsAssistant.sln -c Release --nologo
```

结果：成功，0 警告、0 错误。

- 构建日志：`build-release.log`
- 构建日志 SHA-256：`FBE27F1CAC5D1B193362E76F88D4530AF15C9518EC9A5AD8209DC149F856CD3A`
- `CurrencyWarsAssistant.Tasks.dll` SHA-256：`4F9F0485DAC373C8F803D6010033583F096254D1CF76EFA224855CBCF6DE4404`
- `CurrencyWarsAssistant.Tests.dll` SHA-256：`7288A926B350E85EB20E2241BC02DE5C90503498BEAC3C32B0A8402D8B428DA5`

源码和测试文件修改时间均早于程序集生成时间；下列 TRX 均晚于首次生成最终二进制。

## 修复前复现

### 既有测试

- 文件：`test-results/ISSUE-005-before.trx`
- SHA-256：`2AB08EF4D2D954EFF05BF648DC38FEA8816934F1F33D2FCAB53166DF07595C71`
- 2 项：1 通过、1 失败。
- 失败：期望 `Unknown`，实际 `Equipped`；输出保存具体 `080`，同时候选为 `[080|119]`。

### 新增最小契约测试

- 文件：`test-results/ISSUE-005-before-focused.trx`
- SHA-256：`37EA857364A3A0AC9BCC025F4F0C0B2961C9BBCA44F217800A974084B7AEFE15`
- 1 项：0 通过、1 失败；同样为期望 `Unknown`、实际 `Equipped`。

## 修复后定向测试

- 文件：`test-results/ISSUE-005-after-focused.trx`
- SHA-256：`ABEA6AF3CBF8AD8FEB0BD2B3F4EEACD9E938EFCE2F27254551B2DABF09080890`
- 1 项：1 通过、0 失败。

覆盖真实截图分析后的完整本项契约：

- 装备槽为 `Unknown`；
- `EquipmentId=null`；
- 多候选保留；
- `CanDriveDecisions=false`；
- 同一拥有者和槽位写入 `AdvancedEquipment` pending；
- pending 状态为 `ambiguous-visual-identity`；
- pending 候选与槽状态候选一致。

## 核心关联回归

- 文件：`test-results/ISSUE-005-after-related-core.trx`
- SHA-256：`AD940D4B58E8BC45F2A04AAEF91BCB2A7CA9501FA7A089DFC0DCF55897D13833`
- 9 项：9 通过、0 失败。

覆盖：

- 新增真实截图契约；
- 图标目录的显式视觉歧义；
- `125924.png`、`132307.png` 的准备页分析和 `AdvancedEquipment` pending；
- `EquipmentStateMergeRegressionTests` 全类；
- 历史详情构建器逐装备槽展示测试。

两个参考图用例在 ISSUE-004 全量基线中均失败于 `AdvancedEquipment` pending 为空；本项修改后在输入、识别器和断言不变的情况下同时通过，形成直接 A/B 根因证据。

## 邻接问题隔离测试

- 文件：`test-results/ISSUE-005-after-boundary-old-failures.trx`
- SHA-256：`FC575CD61B433CA4D2B4C6C79C8AB5A734050427508642E5DD826BAE835B6ABE`
- 2 项：0 通过、2 失败。

失败位置已经从本项契约之后继续推进，分别证明：

1. 商店截图的槽状态已正确为 `Unknown`，但候选仍是 `080/119`，而真值断言为 `066/105`；
2. 准备页的歧义装备槽已正确为 `Unknown` 且候选为 `061/100`，但相邻空槽仍错误为 `Unknown`，期望 `Empty`。

因此这两个红灯不是 ISSUE-005 未修好，而是已保存证据、必须分别立项的匹配/裁剪问题和前景门槛问题。

## 全量回归

- 文件：`test-results/ISSUE-005-after-full.trx`
- SHA-256：`6C1876ED34F5B7392087F705B7812530246004BEE5440EC445CF922EA2EBECC2`
- 783 项：757 通过、25 失败、1 跳过；原始全量仍为红，不宣称全项目回归通过。

相对 ISSUE-004 基线：

- ISSUE-004：782 项，769 通过、12 失败、1 跳过；TRX SHA-256
  `C1468C59B5A3C7591A1D657C213826C79E98C000AA80F8665D1434C3EA662CB6`；
- 新增 1 项是本轮契约测试，并已通过；
- 两个 `PreparationReferencesExposeNodeDifficultyAndFormation` 既有失败转为通过，且与本项根因直接相关；
- 全量中相对 ISSUE-004 多出 15 个失败：7 个 PpOCR 耗时、7 个战斗耗时、1 个奖励商店关闭时序；其中 13 个在 ISSUE-002 或 ISSUE-003 已经失败过，另 2 个战斗项也只失败于相同耗时断言；
- 这 15 项均不经过本次装备身份分支，且在全量结束后按原最终二进制独立复跑全部通过：
  - `ISSUE-005-rerun-battle-budget.trx`：7/7，SHA-256
    `5EE38ED896D845217AA477B78E91122A207A3F3F3473BFFE0455D988E7431C10`；
  - `ISSUE-005-rerun-ppocr.trx`：12/12，SHA-256
    `3B8888C3A0DD3E4CFB0A4061D18A46D1C0B90E5CB8CA97A88CEBC862ACC6B8CA`；
  - `ISSUE-005-rerun-reward-shop-close.trx`：1/1，SHA-256
    `6A6E62A873B474A8FF0849D753C83DF61DD260622CF11328C43B5A70606C668F`。

因此未发现由 ISSUE-005 引入的稳定可重复新失败；原始全量的波动仍证明 OCR、战斗识别和关闭时序不稳定，必须继续保留为未完成，不能因单独复跑通过而关闭。

排除上述波动后仍稳定存在的 10 个旧失败为：

- 5 个 `EquipmentDataPipelineTests`（工作副本缺少工具脚本）；
- 1 个历史字段注册表缺少 `Health`；
- 1 个单帧识别性能失败；
- 1 个重复准备页实时预算失败；
- 1 个准备页空装备槽前景误判；
- 1 个商店装备候选错组。

## 交接材料一致性

交接原件：

- `C:\Users\zzz81\Desktop\货币战争Codex交接包-20260808\源代码\docs\HANDOFF_20260808_0.2.838.md`
- SHA-256：`05A9A66A6D3A0437AAF6E96E26FFA753567D95ECC9C57A4E3842C531C7A1B848`
- 第 37-40 行明确记录 `0.60` 兜底导致“非 IsKnown 也算装备”，要求恢复“前景预过滤 + IsKnown”。

本项修复与这项已记录但在交接源码中实际未落实的要求一致；仍以当前源码、真实截图测试和 A/B 结果作为完成依据，而不是仅依据交接声明。
