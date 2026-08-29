# ISSUE-007 复现：商店展开真图的装备候选错组

## 范围

本项只处理 `preparation-shop-1-4.png` 中角色 44 的可见高级装备图标应降级保存为共享视觉组 `066/105`，当前却保存为另一共享视觉组 `080/119` 的问题。

不属于本项：

- 共享图标不得强制保存具体 ID（ISSUE-005 已修复）；
- 相邻空装备槽前景误判（ISSUE-006 已修复）；
- 全部 157 项装备资产的图标—名称—ID—分类专项；
- 其他页面或其他装备图标的识别错误，除非后续证据证明与本项完全同根因。

## 输入

- 审计副本：`inputs/preparation-shop-1-4.png`
- 原 fixture：`tests/CurrencyWarsAssistant.Tests/Fixtures/phase2-live-2026-07-29/preparation-shop-1-4.png`
- 两者 SHA-256：`84401946415672084A0BE47ADB39B69A6C28D5710FC44E18878C31BCD3458A24`
- 大小：2,620,127 字节；审计副本与原 fixture 逐字节相同。

## 修复前稳定复现

测试：

- `Phase2OperationalCollectionTests.ExpandedShopAnalyzerKeepsVisibleFormationWithCompactEvidenceRegions`
- 该测试是交接原件已有测试；本轮未修改其断言。

命令：

```powershell
dotnet test .\tests\CurrencyWarsAssistant.Tests\CurrencyWarsAssistant.Tests.csproj `
  -c Release --no-build --no-restore `
  --filter "FullyQualifiedName~Phase2OperationalCollectionTests.ExpandedShopAnalyzerKeepsVisibleFormationWithCompactEvidenceRegions" `
  --logger "trx;LogFileName=...\ISSUE-007-before-existing.trx"
```

结果：

- `test-results/ISSUE-007-before-existing.trx`
- SHA-256：`1ADB2CD94B7F280FB80EAE5800CDACC35E9B10FD96B2027E24675381E3C2C2F2`
- 1 项：0 通过、1 失败，退出码 1。
- 角色 44 装备槽输出：
  - 槽 0：`Empty`；
  - 槽 1：`Unknown / null ID / 0.745 / [080,119]`；
  - 槽 2：`Empty`。
- 失败断言：期望候选 `[066,105]`，实际 `[080,119]`。
- 页面、阵容、Front44、Back43、库存四件物品及全部槽状态断言均已越过；失败不是页面分类、阵容丢失、前景门禁或 ISSUE-005 身份语义造成。

## 图标真值与重复簇

从当前 runtime manifest 和本地图标逐哈希核对：

| ID | 中文名 | 分类 | PNG SHA-256 |
|---|---|---|---|
| `066` | 火力风暴潮 | advanced | `3F53C178DAA9CE82877B9E7BF4D39F0DCAC781E269E892B800F0551C68D6F548` |
| `105` | 火力风暴潮·特权 | privileged | `3F53C178DAA9CE82877B9E7BF4D39F0DCAC781E269E892B800F0551C68D6F548` |
| `080` | 追逐星辰 | advanced | `EF05EF9C99143308D34121776F49B584F899A13AFFF12BC3D8AE58C6ACAC8898` |
| `119` | 追逐星辰·特权 | privileged | `EF05EF9C99143308D34121776F49B584F899A13AFFF12BC3D8AE58C6ACAC8898` |

`066/105` 和 `080/119` 各自是合法的“普通/特权共享同一图标”组；本项不是要求在组内猜测具体 ID，而是当前选择了错误的视觉组。

使用图像能力逐项查看原截图、066 和 080 图标后：

- 原图角色 44 下方可见的是显示器/三角矩阵图标，与 `066/105` 本地图标一致；
- `080/119` 本地图标是鞋/喷射器造型，与原图明显不符；
- 因此现有测试的 `[066,105]` 期望有直接画面和资产证据，不是仅依据名称猜测。

## 当前生产裁剪

`reward_shop` 使用 `RewardShopCharacterSlots1920[0] = (730,414,116,142)`，Front 行采用 compact 装备布局。对 2559×1439 原图，三个装备槽为：

| 子槽 | 像素区域 |
|---:|---|
| 0 | `(973,722,53×57)` |
| 1 | `(1024,722,53×57)` |
| 2 | `(1075,722,53×57)` |

可见装备位于子槽 1；门禁已经正确把它保留为有内容，错误发生在后续模板匹配/候选组选择阶段。

## 待闭合的根因证据

在修改生产代码前仍需保存：

1. 当前 53×57 裁剪对所有高级装备模板的 top 分数；
2. 对裁剪 Y/高度、透明边缘、颜色预处理和 resize 策略的单变量诊断；
3. `066/105` 与 `080/119` 在原始完整图标和匹配预处理后的视觉差异；
4. 能先红后绿、同时约束正确共享组和 `Unknown/null ID/不可决策/pending` 的最小测试；
5. 证明修复不改变 ISSUE-005/006、其他装备、阵容、库存和页面分类的关联回归。

根因闭合前不得修改候选映射、硬编码该截图或直接把 `080/119` 替换为 `066/105`。
