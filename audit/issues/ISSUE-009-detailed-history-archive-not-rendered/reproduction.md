# ISSUE-009 复现：悬浮详细历史不渲染最近保存归档

## 用户可见现象

在没有实时对局时，主界面的“对局历史记录”能够打开保存记录并显示节点 2-2、
血量 98、商店 Lv7、人口 8；从“节点历史”进入悬浮操作面板，再点击“详细历史”，
同一份保存记录却没有显示，窗口主体为空白。

修前真实窗口截图：

- `screenshots/before-detailed-history-archive-not-loaded.png`
- SHA-256：`F03839FAE9E6E0C393FA77C69970E873ADE1BDB005363B2CFB3A91BF5F09B1CD`

截图来自实际 Release WPF + WebView2 程序，不是模型、XAML 预览或单元测试。

## 复现输入

- 保存归档：`inputs/completed-run.v1.json`
- SHA-256：`49E3DCDD8B9513AD1A207F668BD4973417FFCD7E61512CA83135DF0AF0B8A011`
- 归档包含一个完整节点 `2-2`，并保存 Health=98、StoreLevel=7、Population=8。

为了保证真实界面测试时它是最新记录，另存了仅修改 RunId 和时间戳的测试副本：

- `inputs/runs/run-issue009-archive/completed-run.v1.json`
- 业务字段、节点和观察值不变。

## 修前源码证据

- `inputs/DetailedHistoryWindow.before.xaml.cs`
- SHA-256：`E0E785F93E07B98A78D416802DB34A846B28ED5E5C45BA33E199EB99977F6EC7`
- 与交接包同名源码 SHA-256 完全相同。

修前调用链：

1. 窗口刷新 `CompletedRuns`，再调用 `LoadLatestArchiveIntoDetailedHistory()`；
2. 该方法只把最近归档投影到 `DetailedHistoryNodes`；
3. 当前 XAML 的可见主体是 WebView2，没有绑定 `DetailedHistoryNodes`；
4. 窗口随后只调用 `RealTimeReportBuilder.BuildReportDirectory(_viewModel)`；
5. builder 只读取 `RealtimeNodeEntries`，并过滤为 `FinalBattle != null`；
6. 没有实时完成节点时返回 `null`，保存归档的 RunId 从未传给
   `ReportHtmlRenderer.GenerateAsync`。

## 自动化修前证据

新增的最终来源选择契约在不修改生产代码时运行：

- `test-results/ISSUE-009-before-focused.trx`
- SHA-256：`E57634E8714170F926ABCFF7F4FCE3B8495CE371AF59FC7141C5D94DE91C4FA1`
- 结果：0/5；五项都因生产程序集不存在归档/实时报告来源选择器而失败。

五项分别覆盖：无实时时选最新归档、实时完成节点优先、实时尚未完成时不冒用旧
归档、无任何来源时保持无数据、同一归档 RunId 不重复渲染。

独立审查否决首版后，继续保存了两组修前证据：

- `test-results/ISSUE-009-before-lifecycle-review-fixes.trx`；SHA-256
  `95BA7F1A4A4122807F47243CB8B4980447CFAB6728161D473AFBA6D63D1342AF`；
  0/4，复现 in-flight 重复、失败后不可重试、成功去重生命周期缺口，以及残留实时
  节点/旧 WebView 状态问题；
- `test-results/ISSUE-009-before-dedup-ui-state-fix-clean.trx`；SHA-256
  `3588635E32E3BB436AC70457B3E72D608B647287EE93A44E59D6244CCACA4FD9`；
  0/1，复现成功归档后的下一次去重 tick 会把 loading 状态错误设为可见。

这些否决点在最终源码前均先形成失败证据；中间实现和中止的全量运行不作为最终
通过证据。
