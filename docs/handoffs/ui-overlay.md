# 主界面、悬浮窗与用户控制

## 模块目标

提供成熟深蓝色现代 UI、用户筛选/自动化设置、运行状态与日志展示、悬浮操作面板、鼠标穿透切换、停止命令和实验能力风险提示；把设置转换为 Tasks 可消费的参数。

## 不属于本模块的内容

不实现页面识别、业务状态机、规则最终语义、鼠标输入安全或数据转换；不得在 ViewModel 中复制 Tasks 的页面流程。

## 当前真实状态

主窗口、设置区、日志悬浮窗、操作面板和深蓝主题均有 XAML/代码实现。`MainViewModel` 负责启动/停止命令、设置持久化、筛选方案构造和日志。没有与 `live-test-30` 绑定的 UI 启动和交互验收记录。

## 已实机验证

从旧会话日志可确认应用曾启动并输出 UI 驱动的任务日志，但没有足够截图或验收记录证明当前主题、悬浮窗、热键和所有控件在当前产物上实机通过。当前 UI L4：**尚未确认**。

## 已实现但未实机验证

- `App.xaml`、`MainWindow.xaml` 和弹窗采用深蓝色资源与自定义样式。
- `Ctrl+Shift+F12` 全局停止与 `Ctrl+Shift+F11` 悬浮窗鼠标穿透切换。
- 用户设置保存、开局方案构造、奖励关/超频选项和状态日志绑定。
- `OperationPanelWindow`、`LogOverlayWindow` 与主窗口联动。

## 部分实现或占位功能

- 热键 `RegisterHotKey` 返回值未被检查，注册失败时 UI 不提示。
- 部分原生 Slider/ScrollBar 等控件可能仍依赖默认细节样式，尚无完整视觉清单。
- 未实机验证的奖励关自动化默认开启，缺少明确“实验功能”标识。
- 多 DPI、多显示器、窗口缩放、键盘可访问性和长文本布局尚未形成验收证据。

## 核心代码入口

- `src/CurrencyWarsAssistant.App/App.xaml`
- `src/CurrencyWarsAssistant.App/MainWindow.xaml`
- `src/CurrencyWarsAssistant.App/MainWindow.xaml.cs`
- `src/CurrencyWarsAssistant.App/MainViewModel.cs`
- `src/CurrencyWarsAssistant.App/OperationPanelWindow.xaml`
- `src/CurrencyWarsAssistant.App/OperationPanelWindow.xaml.cs`
- `src/CurrencyWarsAssistant.App/LogOverlayWindow.xaml`
- `src/CurrencyWarsAssistant.App/LogOverlayWindow.xaml.cs`

## 输入与输出

输入：用户选择、保存设置、任务状态、结构化日志和错误结果。输出：`OpeningFilterSet`/方案配置、任务选项、取消命令、可视状态和用户警告。输入参数必须经共享模型传递，不得以 UI 文案作为业务 ID。

## 依赖的其他模块

依赖 Game 的规则模型和数据目录、Tasks 的任务接口/状态、Automation 的安全停止能力、Core 的日志；依赖项目总控给出能力成熟度和是否默认开启的决定。

## 不允许破坏的既有规则

- 所有窗口和控件保持统一深蓝现代风格，不引入未经设计的默认白色控件。
- 停止操作必须始终可达；实验功能必须显式标识。
- 投资环境 UI 只允许正选；阵营/负面词条必须保留正选与排除的差别。
- UI 不得把不同刷取方案扁平合并，也不得宣称 L2/L3 能力已实机完成。

## 已知问题

- 当前构建没有正式 UI 截图验收包。
- 热键冲突/注册失败缺少可见诊断。
- 奖励关默认开启与当前证据等级不匹配。
- `MainViewModel.cs` 同时承担较多设置、装配参数和日志职责，是跨对话冲突热点。

## 下一步任务

1. 在不改变业务语义的前提下补实验能力提示和热键注册失败反馈。
2. 建立 1920×1080、2560×1440 和常见 DPI 的 UI 截图验收清单。
3. 核对所有控件的深蓝主题、禁用态、错误态、焦点态和长文本布局。
4. 用户明确授权后再对当前产物做 UI/悬浮窗/热键实机验收。

## 验收标准

- 所有可见控件使用项目主题，关键状态色和文字对比度一致。
- 启动、停止、失焦暂停、恢复、故障和实验状态对用户清晰可见。
- 热键注册结果可诊断，停止命令在主窗口和悬浮窗均可靠。
- UI 设置生成的方案通过规则测试，不跨方案混合。

## 测试与构建命令

```powershell
.\.tools\dotnet\dotnet.exe test tests\CurrencyWarsAssistant.Tests\CurrencyWarsAssistant.Tests.csproj -c Debug --no-restore --nologo --filter "MainViewModelTests|Settings"
.\.tools\dotnet\dotnet.exe build src\CurrencyWarsAssistant.App\CurrencyWarsAssistant.App.csproj -c Release --no-restore --nologo
```

若过滤器没有匹配到测试，必须按“缺少专项测试”记录，不能按通过处理。视觉验收和热键实机验收需要单独记录。

## 最近可用构建

**尚未确认。** `artifacts/live-test-30` 是最新 UI 候选产物，但没有绑定的当前启动截图和交互验收记录。

## 最后更新时间

2026-07-26（Asia/Shanghai）
