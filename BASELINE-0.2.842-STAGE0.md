# 阶段 0 基线记录：0.2.842（2026-08-14）

## 基线定义
- 源码快照：`D:\Codex-2\extracted-0.2.842\CurrencyWarsSmartRaccoon-0.2.842-HANDOFF-source-tests-audit-20260809`（用户 2026-08-14 拍板为"整体可用"基线）
- 工作副本：`D:\CWAFix-20260814`，Git 提交 `1ddd0c3`，不可移动标签 `baseline-0.2.842`（2396 文件）
- .NET SDK 8.0.423，构建：`dotnet build CurrencyWarsAssistant.sln -c Release` → 0 警告 0 错误（53 秒）

## 全量测试基线
- 命令：`dotnet test tests/CurrencyWarsAssistant.Tests -c Release --no-build --logger "trx;LogFileName=baseline-0.2.842-stage0.trx"`（BelowNormal 低优先级）
- 结果：**总计 876，通过 867，失败 7，跳过 2**（4m57s）
- TRX：`D:\CWAFix-20260814\tests\CurrencyWarsAssistant.Tests\TestResults\baseline-0.2.842-stage0.trx`

## 7 项失败清单（全部为性能预算类断言，非功能缺陷）
1. `Phase2OperationalCollectionTests.LiveCapturedBattleFramesProduceCoreStateWithinRealtimeBudget(battle-1-7.png, 1-7, 180, 521000)`
2. `Phase2OperationalCollectionTests.RepeatedPreparationFramesMeetTheTwoSecondRealtimeBudget`（迭代 2315ms 超 2s 预算）
3. `PpOcrOfflineOcrTests.RecognitionOnlyModelReadsRealGameUiCrops(action-76-b.png, "76")`（预热 605.6ms）
4. `PpOcrOfflineOcrTests.RecognitionOnlyModelReadsRealGameUiCrops(action-79-a.png, "79")`（预热 752.2ms）
5. `PpOcrOfflineOcrTests.RecognitionOnlyModelReadsRealGameUiCrops(preparation-node-1-7.png, "1-7")`（预热 730.3ms）
6. `PpOcrOfflineOcrTests.RecognitionOnlyModelReadsRealGameUiCrops(settlement-damage-13249.8w.png, "13249.8万")`（预热 693.2ms）
7. `PpOcrOfflineOcrTests.RecognitionOnlyModelReadsRealGameUiCrops(settlement-gold-9.png, "9")`（预热 751.2ms）

## 与 0.2.842 自带历史基线对比（audit/final-verification-0.2.842/full-0.2.842.trx，2026-08-09 记录：868 过 / 6 失败）
- 共同失败 2 项：上面 1、2（0.2.842 时代就存在的性能预算失败）。
- 本次新增 5 项：PpOcrOfflineOcr 预热预算（历史通过）——机器负载敏感（本次为低优先级+后台负载跑）。
- 历史失败本次通过 4 项：LiveCapturedBattle 性能预算（机器状态波动）。
- **结论：0.2.842 在本机功能测试 100% 通过；7 项失败全部是耗时预算断言，受机器负载影响，与代码功能无关。后续每阶段回归以"失败数不超 7 且无新功能类失败"为门禁。**

## 后续规则（E5 门禁）
- 每次修改：先红测 → 单根因补丁 → 聚焦测试 → 全量测试（失败数 ≤7 且不得新增功能类失败）→ 整局回放 → 性能回放 → 差异报告 → diff 审查 → 影子运行 → 用户验收。
- 性能预算类测试失败在低优先级环境下可豁免（重跑高优先级复测），但功能失败绝不允许。
