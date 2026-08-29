# ISSUE-009 根因与最小修复边界

## 根因

这是报告来源选择断链，不是识别、保存、Population 渲染或归档排序问题。

`MainViewModel.RefreshCompletedRunsAsync()` 已从 `LocalRunStore` 读取 canonical 完成归档，
且存储层按完成时间降序返回；`ReportHtmlRenderer.GenerateAsync(runId)` 也已经能够直接
从该归档生成正式 HTML。断点仅位于 `DetailedHistoryWindow`：归档被加载进遗留的
`DetailedHistoryNodes` 后，没有把最新归档的 RunId 交给可见 WebView2 的 renderer。

## 被否决的方案

没有扩展 `RealTimeReportBuilder` 去接收归档或从
`HistoricalDetailNodeViewModel` 重新拼装 completed-run JSON。该 builder 的职责是把
瞬时 `RealtimeNodeEntries` 转为临时报告；让它重建 canonical 归档会混合职责，并可能
丢失 identity、diagnostics、source 和已保存字段。

也没有修改 `gen_report.py`、`ReportHtmlRenderer`、归档模型、状态合并、OCR、旧详情
builder 或静态字段 registry。

## 最小修改

唯一生产文件：

- `src/CurrencyWarsAssistant.App/DetailedHistoryWindow.xaml.cs`

窗口现在显式选择三类来源：

1. 正在实时运行且已有完整战斗节点：继续用既有实时目录和
   `GenerateFromAsync`；
2. 正在实时运行但尚无完整战斗节点：显示等待状态，不把旧归档冒充当前对局；
3. 明确没有实时活动：取 `CompletedRuns` canonical 顺序中的第一个有效 RunId，调用
   既有 `ReportHtmlRenderer.GenerateAsync`；没有归档则保持无数据。

为避免不可变归档每 10 秒重复启动 Python，同一归档 RunId 在一个窗口实例中只生成
一次；生成中的 RunId 与成功 RunId 分开记录，失败可以在下一次 tick 重试，来源切换
会使迟到的旧结果失效。只有真正开始生成时才显示 loading，去重 tick 不改变已成功
页面的可见状态。

当来源是无数据或“实时运行但首个战斗节点尚未完成”时，窗口会显式折叠 WebView2，
避免其独立 HWND 继续显示旧归档或盖住 WPF 状态提示；报告生成成功后才重新显示
WebView2。窗口关闭时会使在途归档失效、停止刷新 timer，并在 WebView/renderer await
之后再次检查关闭状态，避免关闭后启动 timer 或写回页面。

## 独立问题边界

下列问题没有夹带到本项：

- 实时 renderer 自身的异步重入、超时与陈旧输出；
- 静态 `HistoricalUiFieldCoverageRegistry` 缺 Health/Population；
- 识别性能和装备数据管线资产缺失；
- 其他全部历史字段的最终双界面逐项验收。
