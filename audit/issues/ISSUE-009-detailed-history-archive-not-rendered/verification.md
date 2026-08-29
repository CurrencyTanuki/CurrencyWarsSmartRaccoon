# ISSUE-009 验证记录

## 最终修改范围

- 唯一生产代码文件：`src/CurrencyWarsAssistant.App/DetailedHistoryWindow.xaml.cs`
- 最终 SHA-256：`16B171030433AA06710481D557186F002C57F8C12CBB78FF2C3C9AB1F246971C`
- 未修改 `RealTimeReportBuilder`、`ReportHtmlRenderer`、`gen_report.py`、识别模型、归档格式、状态合并或静态字段 registry。

新增的测试文件：

- `DetailedHistoryReportSourceSelectorTests.cs`：`1793BB0ABA48D6AE6A85D22130C4AFBEE2E18DDAFCD92858F1AACBD39651A1E`
- `DetailedHistoryArchiveRenderStateTests.cs`：`C41D0DE2CBC8020C1CD5EC949D682C2EF61F7885BA0D0726F841A846C6721FAF`
- `DetailedHistoryArchiveDedupUiContractTests.cs`：`0C5EBD0B5D0EA54B4D97AD8BD6E7D1BFD4690879F7D715D7415749C12398FC38`

## 修前失败证据

- 初始来源选择：`test-results/ISSUE-009-before-focused.trx`，SHA-256 `E57634E8714170F926ABCFF7F4FCE3B8495CE371AF59FC7141C5D94DE91C4FA1`，0/5。
- 独立审查发现的生命周期问题：`test-results/ISSUE-009-before-lifecycle-review-fixes.trx`，SHA-256 `95BA7F1A4A4122807F47243CB8B4980447CFAB6728161D473AFBA6D63D1342AF`，0/4。
- 独立审查发现的去重 tick UI 状态问题：`test-results/ISSUE-009-before-dedup-ui-state-fix-clean.trx`，SHA-256 `3588635E32E3BB436AC70457B3E72D608B647287EE93A44E59D6244CCACA4FD9`，0/1。

三轮修前证据分别覆盖：归档没有进入实际 WebView2 renderer、生成失败后无法重试/关闭后仍可能启动 timer/陈旧实时节点阻断归档，以及成功归档的下一次去重 tick 错误显示 loading。

## 自动化验证

- 针对性测试：`test-results/ISSUE-009-after-focused-final-v3.trx`
  - SHA-256：`C6C0DA8044B439395685C053B7181F8715004E9F4360AB3F337C3542A3AE883B`
  - 10/10 通过。
- 相关回归：`test-results/ISSUE-009-after-related-final-v2.trx`
  - SHA-256：`1A636D27D34BC02057F1161AFB3BADE4DF1EF4DC6C28AF2D902D2BCBD61FFC43`
  - 77/77 通过。
- 全量测试：`test-results/ISSUE-009-after-full-final-v2.trx`
  - SHA-256：`B4820D20BDC7C31EFB18817F5BD028256FB646F50A8DEB8251EE98D53EC82117`
  - 806 项：798 通过、7 失败、1 NotExecuted；命令退出码 1。
  - 与 ISSUE-008 基线 796 项（788/7/1）逐测试名比较：只新增本项 10 个测试且全部通过；既有测试没有结果变化，没有新增失败。
  - 保留的 7 个既有失败是 5 个 EquipmentDataPipeline、1 个 HistoricalUiFieldCoverage、1 个 RecognitionPerformance；Phase2 performance 仍为 NotExecuted。本项不宣称这些问题已修复。

最终 Release 构建：

- `test-results/ISSUE-009-release-build-final.binlog`：`9709E20BA14C39C81FF21539B4D7F2F0E33354A927779C2DBFABF7C9BFDB95F2`
- `test-results/ISSUE-009-release-build-final.log`：`3CA485ED5321661EC8775F7767E8032149903BE92E736ABDF2CE8B2EA0BAB6B0`
- 退出码 0，0 警告、0 错误；最终程序集时间晚于生产源码时间，以上测试运行时间又晚于最终程序集。

## 真实 WPF + WebView2 验证

测试输入：

- 审计原始归档：`inputs/completed-run.v1.json`，SHA-256 `49E3DCDD8B9513AD1A207F668BD4973417FFCD7E61512CA83135DF0AF0B8A011`。
- UI 专用副本：`inputs/runs/run-issue009-archive/completed-run.v1.json`，SHA-256 `68B084F46F090B7394E3886E449F0E46929D7343530ACE118B1C7C800CAAE265`；只调整 RunId/时间以成为最新归档，业务字段保持节点 2-2、Health=98、StoreLevel=7、Population=8。

运行对象：

- 从最终 Release 输出以 `dotnet CurrencyWarsAssistant.App.dll` 启动实际程序。
- 进程 PID `40416`，最终 App DLL SHA-256 `2604C2EEF28E9E7CCC10538C0FAADE6A8B6F803E63CFB6B36367907BFC9CD6EB`。
- 实际入口链：主窗口“节点历史” -> 悬浮操作面板“详细历史” -> `对局历史详细信息` WPF 窗口 -> WebView2 报告。

真实显示结果：

- `screenshots/after-detailed-history-archive-loaded-context.jpg`
  - SHA-256：`4454B43334B9F7C309BB772C7A572E1B423F76E28834A5649D85BD6828FD19E0`
  - 证明详细历史窗口运行在实际桌面程序上下文中。
- `screenshots/after-detailed-history-archive-loaded-cross-tick.jpg`
  - SHA-256：`A2984D25B3D19300C2A0DDC81B4111E721B49EEC3EF549FC3F14DA25DFFDE5E0`
  - 画面清楚显示：节点 `2-2`、血量 `98`、商店 `Lv7`、人口 `8`；未知字段继续以“未记录/无”降级显示，没有丢掉整组数据，也没有暴露内部属性名。
- 生成的正式 HTML 已保存为 `outputs/after-generated-report.html`
  - 9840 字节，SHA-256 `509BB45B705F7EC8A9BF15CFDA469BACFE5AE3C900438040DA2AC05367BF8FDC`
  - 初次生成及最后写入时间均为 `2026-08-09 05:19:02`。

跨刷新周期验证：

- 保持详细历史窗口打开并滚动到字段卡片，跨过至少一次 10 秒 DispatcherTimer tick 后再次截图。
- 跨 tick 截图落盘时间为 `2026-08-09 05:22:37`；此时 HTML 仍为 9840 字节、同一 SHA-256，LastWriteTime 仍为 `05:19:02`。
- 因此同一 archive RunId 没有被下一次 tick 重复运行 Python，已显示内容和 loading 状态也没有被破坏。

正常退出：

- 通过主窗口右上角关闭按钮退出，而不是终止进程。
- 等待 6.5 秒后 Computer Use 枚举中主窗口已消失；随后 `Get-Process -Id 40416` 返回不存在。
- 详细历史及悬浮操作面板随主程序退出，没有遗留可见窗口或卡住主进程。

测试结束后，注入到 LocalAppData 的 `run-issue009-archive`、两个专用 WebView2 临时目录和临时 HTML 已精确核对路径后移入 Windows 回收站；它们可恢复。审计目录中的输入副本、HTML、截图和测试证据均保留。

## 验收边界

本记录只证明 ISSUE-009 的“无实时对局时，悬浮详细历史加载 canonical 最新保存归档，并在跨 tick 后保持稳定”已真实通过。它不代表全项目完成；上述 7 个失败、静态字段 registry、识别性能、装备数据管线、全字段/全图集审计和最终正式发布包仍需按队列逐项处理。

## 独立审查结论

独立只读审查员最终结论：`APPROVE ISSUE-009`。

审查员独立核对了根因、唯一生产文件范围、三轮修前红测、10/10 针对性测试、77/77 相关回归、806 项全量结果、Release 构建、两张真实 WPF + WebView2 截图、跨 tick HTML 哈希/时间戳、正常退出和测试数据清理。批准严格限定为“无实时对局时，悬浮详细历史加载最新归档并稳定显示”，不得外推到 7 个既有失败、实时 renderer 重入/超时、静态字段 registry 或全字段双界面审计。
