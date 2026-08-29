# ISSUE-008 复现：两个历史界面的共享 HTML 报告遗漏人口

## 范围

本项只处理已经进入 `Phase2OperationalState.Population` 并被持久化的人口值，
在“历史记录”和“悬浮窗详细历史”共用的 HTML 报告中完全不显示的问题。

不属于本项：

- `HistoricalUiFieldCoverage` 静态登记表缺少字段；
- 旧 `HistoricalDetailPresentationBuilder` 的 WPF 投影；
- 人口 OCR 或状态合并算法；
- Health、StoreLevel 的 state/snapshot 回退策略；
- 识别性能、装备数据管线或其他剩余失败。

## 真实输入与识别前置

- 审计输入：`inputs/user_ref_000032_prep22.png`
- 原 fixture：
  `tests/CurrencyWarsAssistant.Tests/Fixtures/PageReplay/user_ref_000032_prep22.png`
- 两者 SHA-256：
  `4202A01F8685D9B623BE6B25B74479F13C79499A353C5CB9C58D7F30D0777755`
- 既有真实图测试：
  `UserReferenceGroundTruthTests.PopulationRecognizesAs8`
- 修复前结果：1/1 通过，识别值为 8；TRX：
  `test-results/ISSUE-008-before-recognition.trx`，SHA-256：
  `F25FF3F8AF7170DD696D725B3AA235F6278B0DAF73F3D3068EB1F02BB480A942`。

这证明本项不是人口识别器本身未产出值。

## 持久化与生产渲染复现

为把真实图人工真值 8 固定送入后半段链路，保存了一个使用正式
`CompletedRunRecord` JSON 结构的最小历史 fixture：

- `inputs/runs/run-population-history/completed-run.v1.json`
- SHA-256：
  `49E3DCDD8B9513AD1A207F668BD4973417FFCD7E61512CA83135DF0AF0B8A011`
- JSON 中 `population` 存在，且 observation 的 `value` 为 8；
- 同一节点的 Health=98、StoreLevel=7，用于保护相邻已有展示。

用应用实际部署的 `src/CurrencyWarsAssistant.App/gen_report.py` 直接生成：

- `outputs/before-population-report.html`
- SHA-256：
  `C77609DC46401C17A47E2CE26D995C67D4FB7DD425FDD16F5B9CE1979B4FF66D`
- HTML 中“人口”出现 0 次；
- HTML 中仍有玩家可读的“血量 98”和“商店 Lv7”。

本地现有旧 `completed-run.v1.json` 没有一份包含 Population，因此本 fixture
是从真实截图真值构造的持久化链路复现，不冒充既有用户历史记录。

## 自动化修前红测

新增测试只调用正式 `ReportHtmlRenderer.GenerateFromAsync`，由它定位并执行
部署到测试输出目录的同一份 `gen_report.py`：

- `HistoricalPopulationHtmlTests.SharedWebViewReportRendersKnownPopulationFromPersistedState`
- JSON 中 Population=8 的断言通过；
- Health=98、StoreLevel=7 的 HTML 断言通过；
- 只在期望 HTML 出现玩家可读“人口 8”时失败。

结果：

- `test-results/ISSUE-008-before-focused.trx`
- SHA-256：
  `FF8711D71A791F91A7D5F33947583660522C1668A1F7CBADB538D62BBB7E244D`
- 1 项：0 通过、1 失败，退出码 1；失败行仅为人口 HTML 断言。

随后增加 Unknown/null 降级契约，要求节点、Health 和 StoreLevel 继续显示，
人口显示“未记录”；该测试用于防止单项未知导致整组丢失。

## 两个用户界面的共同路径

- `DetailedHistoryWindow.xaml.cs:62` 调用
  `ReportHtmlRenderer.GenerateFromAsync`；
- `CompletedRunsWindow.xaml.cs:130` 调用
  `ReportHtmlRenderer.GenerateAsync`；
- `GenerateAsync` 随后委托给同一个 `GenerateFromAsync`；
- `RealTimeReportBuilder.cs:60-61` 同时序列化
  `FinalPreparationSnapshot` 与 `FinalPreparationState`；
- `gen_report.py:273-296` 已取得 state，并读取难度，却没有读取 Population；
- `gen_report.py:427-434` 的节点统计区没有人口项。

因此两个实际 WebView2 窗口不是两个独立绑定缺陷，而是同一共享 HTML 渲染器
遗漏一个已经序列化的字段。

## 修复后可见界面复现补充

修复后再次检查时已经没有其他助手进程持有单实例锁。直接 EXE 启动触发项目
既有 UAC 清单，没有进入应用；随后以同一 Release 输出的
`dotnet CurrencyWarsAssistant.App.dll` 启动相同 WPF 程序逻辑，并为 dotnet
宿主指定独立可写 WebView2 用户目录。该做法只绕过 EXE 清单和 dotnet 默认
WebView2 数据目录权限，不替换 App、窗口、renderer、Python 脚本或业务数据。

把上述唯一命名的 fixture 临时复制到正式 LocalAppData runs 目录后：

- 主界面“对局历史记录”打开 `CompletedRunsWindow`；刷新后第一项为
  `run-population-history`；
- 生产日志显示 WebView2 初始化、HTML 生成和 Source 设置全部成功；
- 窗口 UI Automation 同时读到相邻的“人口”和“8”，滚动到节点明细后保存
  `screenshots/ui-02-completed-runs-population-visible.png`；
- 实际生成的 `%TEMP%/currencywars-report-runpopulationhistory.html` SHA-256
  与审计 `outputs/after-population-report.html` 完全相同。

继续从主界面“节点历史”打开悬浮看板，再点击“详细历史”，实际
`DetailedHistoryWindow` 显示“未在记录对局”。源码证明窗口虽然调用
`LoadLatestArchiveIntoDetailedHistory()` 把最近归档放入旧 ViewModel 集合，随后
却只调用 `RealTimeReportBuilder.BuildReportDirectory()`；后者只读取
`RealtimeNodeEntries` 中 `FinalBattle != null` 的实时节点，归档不在输入域。这个
上游问题与 Population HTML omission 根因不同，保存为
`screenshots/ui-03-detailed-history-archive-not-loaded.png` 并另立下一问题。

程序随后通过正常关闭请求退出。LocalAppData 中本轮创建的唯一测试记录和独立
WebView2 临时目录已移入回收站（可恢复）；审计目录内的输入、HTML、TRX 和截图
全部保留。最终正式 EXE 的 UAC 启停仍属于发布包终局验收，不能由本次 DLL 宿主
可见运行替代。
