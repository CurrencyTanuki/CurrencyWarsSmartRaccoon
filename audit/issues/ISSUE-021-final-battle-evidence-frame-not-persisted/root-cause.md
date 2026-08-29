# ISSUE-021 根因与最小修复边界

## 根因

实时收集器只对“应持久化的当前状态”保存截图，但 final battle 的可靠字段可以来自更早的 Battle 帧。状态模型保留了这些旧 EvidenceReference，截图保留策略却没有同步保留来源像素，形成 JSON 引用存在、PNG 缺失的确定性断链。

## 已实施的最小修复

在单次 `RunAsync` 内维护有界、run-local 的战斗证据缓存：

- 每次非 heartbeat 完整分析只在内存登记内部生成的 canonical SourceId、文件名和原始帧；
- 保留 tracker 的 active damage/action/pending-action/rollback/context/settlement 候选，以及最近一次完整分析帧；
- 候选替换后立即丢弃不再可能进入 final 的帧；
- finalize 时递归收集 `FinalNodeBattleState` 实际引用的 canonical 同-run screenshot SourceId，去重、原子保存并解码验证对应原始 PNG，再写 analysis/node-final；
- 不得用 successor/heartbeat 当前帧冒充旧 SourceId；
- 同帧确认 new-run boundary 与旧节点 final 时，必须先把旧证据和旧 node-final 写回 previous run，再切换 runId；新 run 的首个 observation 不携带旧 final；
- 新 run、节点完成、取消与异常都清空缓存；
- 缓存上限为 12 帧 / 512 MiB，可同时容纳 12 张原生 4K BGRA 帧；超限不淘汰 active 候选而是 fail closed；
- 拒绝跨 run、绝对路径、`..`、子目录、损坏 PNG 或缺失缓存；PNG 写入失败时不写 final JSON。

默认保留截图时，final 的 canonical screenshot 引用必须全部存在且可解码。用户显式开启 `DeleteScreenshotsOnCompletion` 时，报告生成并归档后仍按既有隐私清理语义删除截图目录；这属于主动清理模式，不是证据持久化失败。
