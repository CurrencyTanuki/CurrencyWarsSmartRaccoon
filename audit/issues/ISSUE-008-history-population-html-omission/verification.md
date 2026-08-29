# ISSUE-008 验证：共享 HTML 渲染器遗漏 Population

## 结论边界

本项修复了一个已经闭合的数据链缺口：`FinalPreparationState.Population` 已被
识别和保存，但两个 WebView2 窗口共用的 `gen_report.py` 没有读取或显示它。

修复后：

- Known Population 以玩家可读“人口 8”显示；
- Unknown/null Population 显示“人口 未记录”；
- 人口未知不会丢弃节点、Health 或 StoreLevel；
- 主历史窗口已用真实 WPF + WebView2 保存可见截图。

本项不宣称整个“两历史界面”验收完成：悬浮详细历史另有确定的归档回退阻断，
导致没有实时对局时根本不把保存记录交给共享 renderer。该问题必须作为下一单项
修复；本项只可批准为“共享 renderer 字段遗漏已修复”。

## 输入、识别与保存前置

- 真实截图：`inputs/user_ref_000032_prep22.png`；SHA-256
  `4202A01F8685D9B623BE6B25B74479F13C79499A353C5CB9C58D7F30D0777755`；
- 既有真实图人口测试：1/1 通过；TRX
  `test-results/ISSUE-008-before-recognition.trx`，SHA-256
  `F25FF3F8AF7170DD696D725B3AA235F6278B0DAF73F3D3068EB1F02BB480A942`；
- 保存 fixture：
  `inputs/runs/run-population-history/completed-run.v1.json`，SHA-256
  `49E3DCDD8B9513AD1A207F668BD4973417FFCD7E61512CA83135DF0AF0B8A011`；
- JSON 确实包含 Population observation、Known 和 value 8，同时保存 Health=98、
  StoreLevel=7。

这条链证明真实输入能够产出人口 8，正式序列化结构也保留人口 8；修复点不在
OCR、状态合并或存储。

## 修复前证据

直接运行修改前生产脚本生成：

- `outputs/before-population-report.html`，SHA-256
  `C77609DC46401C17A47E2CE26D995C67D4FB7DD425FDD16F5B9CE1979B4FF66D`；
- “人口”出现 0 次；
- “血量 98”和“商店 Lv7”均存在。

第一条 focused 红测：

- `test-results/ISSUE-008-before-focused.trx`；SHA-256
  `FF8711D71A791F91A7D5F33947583660522C1668A1F7CBADB538D62BBB7E244D`；
- 0/1，JSON、Health、StoreLevel 断言均越过，只缺“人口 8”。

加入 Unknown 降级契约后的最终修前红测：

- `test-results/ISSUE-008-before-focused-two-contracts-v2.trx`；SHA-256
  `F97EFDA1D9D73FC110407A4BAEAD175981F754F124019B6A192616231D18EDCF`；
- 0/2；Known 和 Unknown 两项都只在缺少人口 HTML 项时失败。

## 最小生产修改

唯一属于 ISSUE-008 的生产文件：

- `src/CurrencyWarsAssistant.App/gen_report.py`。

交接原件与修改前工作副本 SHA-256 均为：
`5222A2CC2D0A7660179B7B3413D72D2F4431367FEE7D4C34C32259CB509E13E9`。

修复后 SHA-256：
`B7A4B6D1761280AD0D590438D01447795208440C23D460FF21F6D80A41D00AD3`。

逐行 diff 只有：

1. 用现有 `known_value` 从 `state["Population"]` 读取；
2. Known 格式化为数值，Unknown/null 格式化为“未记录”；
3. 在既有节点统计区增加“人口”一项。

没有修改 C#、窗口、contracts、OCR、状态、保存、Health/StoreLevel、旧 builder、
registry 或其他 HTML 字段。Release App 输出中的 `gen_report.py` SHA-256 与源码
完全一致。

新增测试文件：

- `tests/CurrencyWarsAssistant.Tests/HistoricalPopulationHtmlTests.cs`；
- SHA-256：
  `A40833742EC5473B2ECF8368B6A3544F413B5C3439C7EE91369E29455B5C659F`；
- 通过正式 `ReportHtmlRenderer.GenerateFromAsync` 调用部署脚本，而不是复制 Python
  逻辑到测试中。

## 定向与关联回归

定向：

- `test-results/ISSUE-008-after-focused.trx`；SHA-256
  `61F90853E7CE578C36A4631311283808D44E1F67A0D722A15DE073FA0FC6EF56`；
- 2/2 通过：Known=8 与 Unknown 降级均通过，Health/StoreLevel/节点保护通过。

生产输出脚本直接回归：

- `outputs/after-population-report.html`；SHA-256
  `CEC3FA6F7258FC7C6E5D4F63B6EC5A1ACDDB495FBAB4D363935DE3A760E25C20`；
- 精确包含“人口 8”“血量 98”“商店 Lv7”和节点 `2-2`；
- 部署脚本与源码脚本 SHA-256 均为最终值 `B7A4...AD3`。

历史/报告关联集：

- `HistoricalPopulationHtmlTests`、`ChallengeSummaryReportTests`、
  `RunCompletionArchiveTests`、`UiRedesignContractTests`、
  `HistoricalDashboardProjectionTests`；
- `test-results/ISSUE-008-after-related.trx`；SHA-256
  `26267D337D21A1754F66DC010227A2D9C333737D2C0B47248E18FEDAEABA6178`；
- 52/52 通过。

单独确认未夹带 registry 修复：

- `test-results/ISSUE-008-boundary-existing-registry-failure.trx`；SHA-256
  `D0F0C563244F45B569BCBC8B24E799F8EC532504A73B128145FD0FA0C53B6EE3`；
- 5 项中 4 通过、1 个既有 `Registry_CoversEveryPublicProperty...` 失败；
- 仍先报缺 `Health`，该静态登记缺口与实际 renderer 分开保留。

## 全量回归

- `test-results/ISSUE-008-after-full.trx`；SHA-256
  `8C31A2C418DDE8776CBC8C8424DC74C8E8FE39ED8BC7C436C54E87D5F58BC892`；
- 总计 796：788 通过、7 失败、1 跳过；退出码 1。

与 ISSUE-007 最终基线（794：786/7/1；TRX SHA-256
`B5C373E43BC54CECB509D4582CDD6556847D1BB824056AE992A33011AAACDF65`）
逐测试名比较：

- 新增失败：0；
- 消失失败：0；
- 新增 2 个 ISSUE-008 测试全部通过；
- 通过数净增 2，失败集合完全相同。

剩余失败仍为：

1. `EquipmentDataPipelineTests` 5 项：缺交付脚本/资产；
2. `HistoricalUiFieldCoverageTests` 1 项：静态登记缺口；
3. `RecognitionPerformanceTests` 1 项：本轮最快 8.57 秒，超过 2 秒合同。

全量结果仍为红色，不宣称全项目回归通过。

## Release 构建

命令：

```powershell
dotnet build .\CurrencyWarsAssistant.sln -c Release --no-restore
```

结果：退出码 0，0 警告，0 错误。

- 最终 binlog：`test-results/ISSUE-008-release-build-final.binlog`；SHA-256
  `B74F4773D1452608CA21E88C6797F2FDF25EF00B62E370E461781FBC6959B0F5`；
- 文本日志：`test-results/ISSUE-008-release-build.log`；SHA-256
  `4D1A55EB6C989325DA52C97DD147D8E336932159A69D2B31F42E4D65841164CF`。

## 真实 WPF + WebView2 验证

为了不把模型/单测当界面验收，本轮实际启动 Release App assembly：

1. 直接 EXE 触发既有 UAC 清单，未进入应用；
2. 使用 `dotnet CurrencyWarsAssistant.App.dll` 启动同一 App assembly；
3. 给 dotnet 宿主指定独立可写 WebView2 用户目录，否则默认目录返回
   `E_ACCESSDENIED`；
4. 通过真实主界面“对局历史记录”打开 `CompletedRunsWindow`，刷新并选中
   `run-population-history`；
5. 生产日志显示 `WebView2 已初始化`、HTML 生成完成、`已设置 Source`；
6. UI Automation 读取到节点统计中的相邻元素 `人口` 和 `8`，并用
   `ScrollItemPattern.ScrollIntoView()` 滚到可见区域；
7. 保存窗口截图
   `screenshots/ui-02-completed-runs-population-visible.png`，SHA-256
   `9CA3926DC632126430D898B4BD4CC3625F1092CC295E289F5254DC5CC2367050`。

截图同时可见节点 2-2、血量 98、商店 Lv7、人口 8；未暴露内部属性名或 ID。
实际窗口生成的临时 HTML SHA-256 与审计 after HTML 的 `CEC3...5C20`
完全相同。

程序通过正常主窗口关闭请求退出。直接 EXE 的 UAC/正式发布包启动与退出仍未
验证，必须留到正式包终局验收。

## 悬浮详细历史的独立失败

实际从主界面“节点历史”打开 `OperationPanelWindow`，点击“详细历史”后：

- `DetailedHistoryWindow` 确实打开；
- UI Automation 读到：
  `未在记录对局——开启实时记录后，此处实时显示对局报告`；
- 保存截图：`screenshots/ui-03-detailed-history-archive-not-loaded.png`，SHA-256
  `F03839FAE9E6E0C393FA77C69970E873ADE1BDB005363B2CFB3A91BF5F09B1CD`；
- 同一时刻主历史窗口已加载并可见 Population=8 的最近归档。

根因边界：`LoadLatestArchiveIntoDetailedHistory()` 只填充旧
`DetailedHistoryNodes`；HTML 路径随后调用的 `RealTimeReportBuilder` 只读取
`RealtimeNodeEntries` 且要求 `FinalBattle != null`。因此保存归档没有进入报告
输入。这不是 `gen_report.py` 漏读 Population 的同一根因，也不应塞进同一修改。

下一单项必须修复该归档回退，并用同一记录重新保存悬浮详细历史中“人口 8”的
可见截图；在此之前，项目级“两历史界面完整显示”继续标为未完成。

## 测试数据清理

本轮临时复制到 LocalAppData 的 `run-population-history` 和独立 WebView2 用户
目录在程序正常退出后已移入回收站，可恢复。审计目录内的原截图、持久化 JSON、
before/after HTML、TRX、build 证据和三张 UI 截图全部保留。
