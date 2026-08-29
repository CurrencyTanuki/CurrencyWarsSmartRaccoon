# ISSUE-003 复现记录：滑窗数字序列混入不同尺度的局部伪字形

## 用户影响

真实备战截图中的金币被保存为错误值或 0，真实战斗截图中的行动点 80 被读为 87。
这些值会继续进入状态合并、对局存储、历史记录和决策逻辑。

## 保存的输入图

本轮输入已从项目现有 fixture 原样复制到 `inputs/`，并逐项核对画面中的真实数字：

| 文件 | 画面真值 | SHA-256 |
|---|---:|---|
| `preparation-1-7-user.png` | 金币 32 | `52D3183D7090B4C06068BF582E2A700D31CBD8015AFFD108245B91B155161D0E` |
| `preparation-1-3-gold-23-user.png` | 金币 23 | `336E3E394647F1A4DEACABA5AD4D42E8444D32A078E8C50A44B55FF5D1A73FD7` |
| `preparation-shop-1-4.png` | 金币 6 | `84401946415672084A0BE47ADB39B69A6C28D5710FC44E18878C31BCD3458A24` |
| `preparation_privilege_boxes_gold3_2559x1439.png` | 金币 3 | `9B5ABE6FEC47D9BA4717E39302FE7435EEF40E03CAD1D3FA4E4713E0873F519C` |
| `battle-1-7.png` | 行动点 80 | `00ACC431AB8756440BED0F6DA8DA978C841DDED8138720CE7781EE2F15649189` |

## 复现命令和结果

对 6 个关联的真实截图测试运行 Release 二进制，结果 0 通过、6 失败；原始 TRX 为
`test-results/ISSUE-003-before.trx`。

| 测试 | 期望 | 实际 |
|---|---:|---:|
| `LocalizedUiDigitsReadLivePreparationResourcesWithoutGeneralOcr`（economy 子项） | 32 | 328 |
| `ExpandedEconomyCropDoesNotPrependCoinIconToSingleDigitGold` | 3 | 38 |
| `PreparationSnapshotReadsAbsoluteHealthAndGoldFromTightRegions` | 32 | 0 |
| `PreparationEconomyKeepsLeadingDigitAtShopCardBoundary` | 23 | 0 |
| `ExpandedShopEconomyUsesTightEvidenceInsteadOfAdjacentZero` | 6 | 7 |
| `GlowingActionValueUsesLocalizedDigitTemplatesWithoutRelaxingOcr` | 80 | 87 |

`LocalizedUiDigits...` 同时出现难度 126 子项失败。后续定点回归表明，它也随同一滑窗
小尺度污染根因一起恢复，因此不再按独立根因拆分。

## 根因证据

`OpenCvUiDigitSequenceRecognizer.RecognizeBySlidingTemplates` 在本轮交接中把全局滑窗搜索高度
扩展为 16、20、24、28、32、36、40、48、56、64 像素。16/20 像素原本用于商店等级
`Lv.` 反相拉伸区域中很小的右端数字，但被无差别应用到了金币、行动值和难度等正常字段。

真实失败输出直接显示混合尺度：

```text
32 -> 328:
3: 27x40, 2: 27x40, 伪 8: 11x16

6 -> 86（模板候选）:
伪 8: 11x16, 6: 27x40

80 -> 87:
8: 27x40, 伪 7: 11x16
```

16px 伪字形位于正常 40px 字形的下部，底边恰好相近，因中心点的垂直差超出旧聚类容差
而被当成独立数字，随后与 40px 字形拼接。全分析器对这些相互冲突的伪候选进行多数/窄区
选择后，进一步得到 0 或 7。根因是专用的小字滑窗高度被全局启用，不是截图真值、配置缺失、
OCR 模型、经济区域、候选投票或状态合并错误。

## 被否决的初始方案

初始尝试在序列形成后把混合高度候选整组降级为 Unknown。原始证据保存在
`test-results/ISSUE-003-after-initial.trx` 和 `test-results/store-diagnostic.trx`。它只恢复了
金币 32 和 3，金币 23、商店金币 6、行动值仍失败；商店等级诊断还从可识别路径退化为
`recognized=False/value=null`，因为 `Lv.7` 本来就依赖右端 16px 数字。该方案会破坏成熟功能，
已撤销，未进入最终改动。

## 最小修复方向

保留 48/56/64 像素以支持高分辨率缩放；普通数字字段默认只搜索
24、28、32、36、40、48、56、64 像素。识别器提供默认关闭的小字滑窗选项，只有商店等级
`Lv.` 路径显式启用 16/20 像素。这样修复根因，同时不改区域、模板、置信阈值、OCR、经济
候选仲裁或状态合并逻辑。
