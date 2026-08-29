# ISSUE-021 验证记录

## 修前红灯

- 测试：`FinalNodeBattleReferencesOnlyBattleScreenshotsThatExist`
- 结果：`0 Passed / 1 Failed`
- 失败：final 引用的 `20260809-133111200.png` 不存在
- TRX：`test-results/before-targeted.trx`
- TRX SHA-256：`25C5F51A2570514A78D7A680828EAEE3AF912EF7017B05FAE908B0C7CB1392F0`
- 测试源码 SHA-256：`E98B4CD996EDD23F6C6331C4ADFBBBC74C367AC3281A34145BD561E01F1F8949`
- 生产源码仍为：
  - LiveCollectionService `55E169B1FA9D826EEAA95CACA287CBAE1AAD8E55BD3FCDA8D68E96F8A72C5645`
  - StateTracker `C0A298A537C2C465CC0DDE7D8DB8D4B52F316062BF136E09988AC9CE51AC31C3`

## 修后定向与关联验证

- 配置：`Release`、`--no-restore`
- 过滤范围：
  - `Phase2FinalEvidenceFrameCacheTests`
  - `FinalBattleEvidencePersistenceTests`
  - `Phase2BattleOutcomeHealthTests`
- 结果：`38 Passed / 0 Failed / 0 NotExecuted`
- TRX：`test-results/after-targeted-related-v3.trx`
- TRX SHA-256：`E5A0B9A0AE693A49CBA0173E53CB8FE6EDA78704F90842B6C618737F9D8C3976`

覆盖内容：

- normal、heartbeat、whole-run terminal 三条 finalization 路径；
- heartbeat 只保存旧完整分析的原始像素，不把当前 successor 像素冒充旧 SourceId；
- new-run boundary 与旧节点 final 同帧时，旧 node-final 只归 previous run，新 run analysis 不携带旧 final；
- 实际落盘 Battle PNG 集合精确等于 final 引用集合，未引用战斗帧不落盘；
- 12 帧 / 512 MiB 上限可容纳 12 张原生 4K BGRA 帧；active 候选超限不被静默淘汰；
- 跨 run、绝对路径、`..`、子目录、缺缓存、损坏既有 PNG 和 PNG 写入失败均 fail closed；临时文件不残留；
- 默认保留模式下所有 final screenshot 引用存在、可解码且像素来源正确；显式 `DeleteScreenshotsOnCompletion=true` 时，归档完成后仍按既有隐私设置删除截图目录。

## 当前源码 SHA-256

- `Phase2FinalEvidenceFrameCache.cs`：`1C89FCFB9B256CF74D41B4AD0A3F49538D59B3CEC5D6D36BA84497586B920112`
- `Phase2OperationalStateTracker.cs`：`8CCE612963F1B2EC6CDEDFA17A7752CBFDC11B7B0647A65CBBAE6F58C17E5D2F`
- `Phase2LiveCollectionService.cs`：`1CBC78685BCF44BD56B37D1B8B16BDCEDED886E2B0BFCE1AD85FC97A70B10711`
- `Phase2FinalEvidenceFrameCacheTests.cs`：`3A50A22E0B567822DFD9C877F2EA23D10C1BA9838AE043D9C44B3C9C8CDDCF74`
- `FinalBattleEvidencePersistenceTests.cs`：`6597AA10D81F8698A3C7820244B3BB1C4C0E19BF3778A183949693A5E2FB79BE`

## 独立终审

- verdict：`APPROVE`
- 审查方式：生产与测试补丁只读终审；逐项核对 run-local 缓存、active 候选、heartbeat 原像素、PNG-before-JSON、new-run 归属、容量/路径 fail-closed 与显式截图清理语义。

当前状态：ISSUE-021 最小生产修复、Release 定向/关联验证和独立终审均已完成；本问题修后的全量测试由主流程下一阶段统一执行。
