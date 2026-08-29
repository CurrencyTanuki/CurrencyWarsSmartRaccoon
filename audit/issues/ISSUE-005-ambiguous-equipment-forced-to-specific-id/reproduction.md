# ISSUE-005 复现：共享图标歧义被强制保存为具体装备 ID

## 范围

本项只处理装备图标已检测到、但同一视觉资源对应多个本地装备 ID 时，分析器仍用置信度阈值把结果强制标为 `Equipped` 并保存某一个具体 ID 的问题。

以下相邻问题不属于本项：

- 空装备槽因前景阈值过宽而被判为有内容；
- `preparation-shop-1-4.png` 的实际图标与候选 `080/119` 之间的裁剪或匹配错误；
- 装备名称、图标、分类和网络资料的全量专项核对。

## 未修改生产代码前的输入与环境

- 输入：`inputs/preparation-shop-1-4.png`
- 输入 SHA-256：`84401946415672084A0BE47ADB39B69A6C28D5710FC44E18878C31BCD3458A24`
- 分析器源码 SHA-256：`6D13BD5DAB99BAC96AEF4255FAA79FD7FAD29A8964B76AA0F7B578BE3196B32A`
- 复现时生产代码仍为已批准 ISSUE-004 后版本；本项尚未修改生产代码。

## 复现 1：既有真实截图测试

原始结果：`test-results/ISSUE-005-before.trx`

- SHA-256：`2AB08EF4D2D954EFF05BF648DC38FEA8816934F1F33D2FCAB53166DF07595C71`
- 2 项：1 通过、1 失败；
- 失败测试：`ExpandedShopAnalyzerKeepsVisibleFormationWithCompactEvidenceRegions`；
- 实际输出：角色 `currency_wars_character_44` 的装备槽 1 被标为
  `Equipped/currency_wars_equipment_080/0.745/[080|119]`；
- 断言：期望 `Unknown`，实际 `Equipped`。

配套目录测试 `IconCatalogLoadsExistingAndImportedResourcesWithExplicitVisualAmbiguity` 通过，证明目录层已明确表达共享视觉资源的多 ID 歧义。

## 复现 2：新增最小真实截图契约测试

为了把“歧义不得驱动决策”与后续图标错配问题分开，新增测试
`AmbiguousShopEquipmentRemainsNonDecisionEvidence`。它只验证：

1. 有多个候选的装备槽为 `Unknown`；
2. 不保存具体 `EquipmentId`；
3. 候选列表仍保留；
4. `CanDriveDecisions=false`；
5. 同一装备槽进入 `PendingIconObservation`，状态为
   `ambiguous-visual-identity`。

原始结果：`test-results/ISSUE-005-before-focused.trx`

- SHA-256：`37EA857364A3A0AC9BCC025F4F0C0B2961C9BBCA44F217800A974084B7AEFE15`
- 1 项：0 通过、1 失败；
- 失败点仍是期望 `Unknown`、实际 `Equipped`。

该失败发生在任何 ISSUE-005 生产代码修改之前，因此不是为修复后结果反向编写的绿色测试。

## 根因

`Phase2OperationalScreenshotAnalyzer.RecognizeEquipmentSlots` 使用：

```text
item.IsKnown || item.Confidence >= 0.60
```

共享同一图标字节的装备组会返回多个候选，`ResolvesExactIdentity=false`，因此
`item.IsKnown=false`。0.60 兜底却忽略了这项身份歧义，把匹配器当前排序第一的候选
写入 `EquipmentId`，同时跳过已有的 `Unknown`/pending 降级分支。

交接原件
`源代码/docs/HANDOFF_20260808_0.2.838.md:37-40` 也明确记录：
`0.60` 阈值兜底会让“非 IsKnown 也算装备”，要求恢复“前景预过滤 + IsKnown”。

## 最小修复边界

- 删除装备专用的 `0.60` 强制已知阈值；
- 只有 `item.IsKnown` 才能保存具体装备 ID；
- 多候选或未可靠解析的可见装备继续使用现有 `Unknown`/pending 分支；
- 不改前景检测、裁剪区域、模板匹配、图标资产、候选顺序或其他识别字段。
