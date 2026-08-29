# ISSUE-020 根因与最小修复边界

## 根因

`ReadBattleDamageAsync` 无条件复用了 `ParseSettlementDamageCandidates`。该解析器为单位被 OCR 裁切时设计，会把 `1,234` 一类数字推断为万位；战斗实时排行榜中的规范整数千分位却表示基础数值，因此相同文本语义不同。与此同时，既有真实战斗图中的 `52.1`、`1633.5` 等无单位小数确实仍需按万位兼容，不能把 settlement fallback 从 Battle 全部删除。

此外，战斗总候选因单位不明确成为 Partial 时，`PreferCumulativeValue` 允许两帧相同 Partial 值稳定提升 Known；其歧义检测只识别特定中文不确定性文本，不能可靠拦截当前英文 `settlement unit inferred as 万` 行。故在恢复边界前驱帧前，必须先消除战斗路径中的错误结算推断。

## 最小修复

- 新增/使用仅供战斗页的候选解析入口；
- 该入口仍复用 settlement 兼容候选，保留无单位小数的隐含万位规则；
- 仅当原始候选符合无前导零的规范千分位 `^[1-9][0-9]{0,2}(,[0-9]{3})+$` 时，过滤对应的 inferred-wan 候选，保留基础整数并提高到可靠候选分；
- 结算路径和 `ParseSettlementDamageCandidates` 不改；
- 不修改 tracker、队列、OCR、区域、阈值、UI 或存储模型。

## 必须回归

1. `3,545 → 3,545`；
2. `8,863 → 8,863`；
3. 结算 `1,234 → 12,340,000`；
4. 既有四张参考战斗图与七张 live 战斗图的总候选合同不退化；
5. Phase2 Operational 关联集、全量测试与 Release build；
6. 完成后才恢复 ISSUE-019 队列修复并做真实短战斗 E2E。
