# ISSUE-013 根因分析

`Phase2OperationalState` 后续新增 `Health` 与 `Population` 时，静态 `HistoricalUiFieldCoverageRegistry` 的同类型字段清单没有同步更新。现有测试按类型逐个比较排序后的公开属性集合，在 `Phase2OperationalState` 首个差异 `Health` 处失败，因此错误消息没有继续显示 `Population`。

两个字段属于同一模型、同一 registry 段和同一契约漂移根因，可作为一个单项修复。

候选最小修改仅在 `HistoricalUiFieldCoverage.cs` 的“节点概览”登记中加入：

```csharp
nameof(Phase2OperationalState.Health),
nameof(Phase2OperationalState.Population),
```

不修改测试、模型、识别、状态合并、存储、Python renderer、窗口或旧 WPF builder。

最终实现与候选一致，仅在同一 registry 段新增上述两个 `nameof`。修改后源码全集为 33 个公开属性、33 个唯一映射，重复 0、缺失 0、多余 0。
