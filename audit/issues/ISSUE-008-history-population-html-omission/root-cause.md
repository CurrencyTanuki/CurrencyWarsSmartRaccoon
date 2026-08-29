# ISSUE-008 根因：共享 Python HTML 渲染器未读取人口字段

## 根因链

1. `Phase2OperationalState.Population` 已存在，真实截图既有测试可识别为 8。
2. `RealTimeReportBuilder` 把 `FinalPreparationState` 写入完成记录；保存后的 JSON
   确实包含 Population observation 和 value 8。
3. 两个历史窗口都通过 `ReportHtmlRenderer` 调用同一份 `gen_report.py`。
4. 脚本节点循环已经取得 `state = FinalPreparationState`，但只从 state 读取
   `EnemyDifficulty`；Population 从未被读取或格式化。
5. 节点统计 HTML 已显示金币、血量、行动、商店、难度、结算和总伤害，但没有
   人口项。因此数据在最终共同渲染边界被确定性丢弃。

## 已排除

- 不是 OCR 根因：真实图人口 8 测试通过。
- 不是序列化根因：保存后的正式 JSON 结构包含 Population=8。
- 不是某一个窗口的绑定根因：两个窗口共用同一 renderer。
- 不是 Unknown 导致整节点丢弃：人口为 Known 时同样缺失，其他统计仍显示。
- 不是 `HistoricalUiFieldCoverage` 红测的直接运行时影响：实际窗口不使用旧 WPF
  detail builder；该静态登记缺口应另立问题。

## 最小修改边界

只在 `src/CurrencyWarsAssistant.App/gen_report.py`：

1. 通过既有 `known_value` 从 `state["Population"]` 读取人口；
2. Known 时显示数值；Unknown/null 时显示“未记录”；
3. 在既有节点统计区添加玩家可读标签“人口”。

不修改 C# contracts、识别、状态合并、保存、窗口代码、旧 detail builder、
Health/StoreLevel 回退或静态字段 registry。

## 独立上游阻断边界

共享脚本修复后，主历史窗口已经真实显示 Population。悬浮详细历史在没有实时
对局时仍无法把最近归档交给该脚本：`DetailedHistoryWindow` 只调用
`RealTimeReportBuilder`，而后者只接收有 FinalBattle 的 `RealtimeNodeEntries`。
因此“脚本收到保存 state 却漏字段”和“窗口没有把保存归档交给脚本”是两个
可独立复现、不同修改文件和不同回归风险的问题。本项不顺带修改窗口回退；后者
必须作为下一单项修复，并在完成后重新对两个窗口做同一记录的可见验证。
