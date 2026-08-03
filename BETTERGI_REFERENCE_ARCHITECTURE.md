# 参考 BetterGI 的技术架构方案

## 1. 架构决策

本项目采用与 BetterGI 相近的 Windows 原生桌面技术路线：

- 开发语言：C# 12；
- 运行平台：.NET 8，Windows x64；
- 桌面界面：WPF；
- 界面架构：MVVM；
- 依赖管理：.NET Generic Host 与依赖注入；
- 图像处理：OpenCvSharp；
- OCR：PaddleOCR 的 ONNX 推理实现；
- 模型推理：ONNX Runtime；
- 截图：Windows Graphics Capture 为主，BitBlt 为兼容方案；
- 鼠标输入：Windows `SendInput` 为主；
- 配置与资料：JSON；
- 日志：结构化日志与按次任务证据目录；
- 测试：xUnit，加离线截图回放测试。

选择这条路线的原因不是单纯追求与 BetterGI 一致，而是：

- 工具只在 Windows 上运行，C# 对窗口、输入、热键和系统托盘支持直接；
- BetterGI 已证明 .NET 8、WPF、OpenCV、OCR、截图和模拟输入的组合能够长期维护；
- 用户已经熟悉 BetterGI，操作习惯和问题排查方式可以保持接近；
- 单一桌面进程即可覆盖当前需求，不需要过早引入前后端分离；
- 后续仍然可以通过 ONNX 接入图像检测或局势评估模型。

## 2. 从 BetterGI 借鉴什么

### 2.1 借鉴的架构思想

- 使用桌面主程序统一管理生命周期；
- 使用依赖注入装配截图、识别、输入、任务和配置服务；
- 将实时监听、独立任务和完整流程分开；
- 图像识别与具体任务逻辑分离；
- 为每个任务单独保存识别资源和配置；
- 使用遮罩层显示识别框、日志和运行状态；
- 使用取消令牌统一停止自动化任务；
- 将用户数据、程序资源和日志分目录保存；
- 自动化操作以任务和状态机组织；
- 为社区数据和扩展内容保留独立仓库或数据包机制。

### 2.2 不需要照搬的部分

- 地图追踪、坐标导航和路径文件；
- 角色移动、战斗脚本与视角控制；
- 高频实时触发器体系；
- JavaScript 通用脚本引擎；
- 大规模脚本仓库订阅系统；
- 与货币战争无关的复杂任务编排。

第一阶段保持内核小而稳定。只有社区确实需要第三方扩展时，再设计公开插件或脚本接口。

## 3. 许可证隔离

BetterGI 使用 GPL-3.0。本项目目前希望禁止商业使用，因此不能直接复制、修改或合并 BetterGI 的 GPL 代码后再采用非商业许可证。

开发时遵守：

1. 只参考公开架构、功能表现和文档；
2. 不复制 BetterGI 源码片段；
3. 不复制它的图片、模型、配置或其他资源；
4. 本项目接口、类名和实现独立设计；
5. 所有第三方依赖单独记录许可证；
6. 引入代码前进行许可证兼容检查；
7. 如果未来需要直接复用 BetterGI 代码，先重新决定整个项目的许可证。

这是一条项目治理规则，不是对具体法律结果的保证；正式发布前仍应复核许可证文本。

## 4. 总体架构

```text
┌─────────────────────────────────────────────────┐
│                  WPF 桌面应用                    │
│ 首页 / 开局助手 / 攻略 / 局势 / 设置 / 日志     │
└──────────────────────┬──────────────────────────┘
                       │
┌──────────────────────▼──────────────────────────┐
│                 任务运行时 Runtime               │
│ 任务调度 / 状态机 / 取消 / 超时 / 异常恢复       │
└───────────┬──────────────────────┬──────────────┘
            │                      │
┌───────────▼──────────┐  ┌────────▼──────────────┐
│   领域与决策 Domain  │  │   自动执行 Automation │
│ 状态 / 规则 / 评分   │  │ 守卫 / 鼠标 / 验证    │
│ 攻略检索 / 建议      │  │ 热键 / 窗口检查       │
└───────────┬──────────┘  └────────┬──────────────┘
            │                      │
┌───────────▼──────────────────────▼──────────────┐
│                视觉系统 Vision                  │
│ 截图 / ROI / 页面识别 / 模板匹配 / OCR / ONNX  │
└──────────────────────┬──────────────────────────┘
                       │
┌──────────────────────▼──────────────────────────┐
│              基础设施 Infrastructure            │
│ Windows API / 配置 / 数据 / 日志 / 遮罩 / 通知  │
└─────────────────────────────────────────────────┘
```

依赖方向保持单向：具体任务可以使用视觉和输入接口，但视觉层不能引用具体任务。

## 5. 解决方案结构

```text
CurrencyWarsAssistant.sln
src/
  CurrencyWarsAssistant.App/
    App.xaml
    Views/
    ViewModels/
    Navigation/
    Hosting/

  CurrencyWarsAssistant.Core/
    Runtime/
    Tasks/
    StateMachine/
    Configuration/
    Diagnostics/

  CurrencyWarsAssistant.Vision/
    Capture/
    Windowing/
    Recognition/
    Ocr/
    Templates/
    Onnx/
    Overlay/

  CurrencyWarsAssistant.Automation/
    Input/
    Guards/
    Actions/
    Hotkeys/

  CurrencyWarsAssistant.Game/
    Models/
    Opening/
    RunState/
    Evaluation/
    Advice/

  CurrencyWarsAssistant.Knowledge/
    Strategies/
    Modifiers/
    Guides/
    Versioning/

  CurrencyWarsAssistant.Tasks/
    OpeningReroll/
    RunObserver/
    SituationAdvisor/

tests/
  CurrencyWarsAssistant.Vision.Tests/
  CurrencyWarsAssistant.Game.Tests/
  CurrencyWarsAssistant.Tasks.Tests/
  Fixtures/

assets/
  recognition/
    1920x1080/
  models/

data/
  strategies/
  modifiers/
  guides/

user/
  config.json
  rules/

logs/
docs/
```

`Core` 只保存通用运行机制；货币战争的具体概念进入 `Game`；每个可启动功能进入 `Tasks`。这样后续新增攻略和局势建议时不会把刷开局模块改成巨型类。

## 6. 核心接口

### 6.1 截图接口

```csharp
public interface IGameCapture
{
    ValueTask<CaptureFrame> CaptureAsync(
        CaptureRegion? region,
        CancellationToken cancellationToken);
}
```

`CaptureFrame` 至少包含：

- 图像数据；
- 捕获时间；
- 游戏客户区位置；
- 原始分辨率；
- 标准分辨率缩放信息；
- 当前窗口句柄；
- 是否仍在前台。

截图后续可以更换实现，任务层不关心使用 Windows Graphics Capture 还是 BitBlt。

### 6.2 识别接口

```csharp
public interface IRecognizer<T>
{
    ValueTask<RecognitionResult<T>> RecognizeAsync(
        CaptureFrame frame,
        RecognitionContext context,
        CancellationToken cancellationToken);
}
```

识别结果统一包含：

- 识别值；
- 置信度；
- 元素位置；
- 使用的识别方式；
- 调试证据；
- 失败原因。

页面识别、投资策略识别和敌人词条识别分别实现，不使用一个万能识别器。

### 6.3 游戏状态接口

```csharp
public interface IGameStateReader<TState>
{
    ValueTask<StateReadResult<TState>> ReadAsync(
        CancellationToken cancellationToken);
}
```

状态读取器负责组合多个识别器，并对连续帧结果进行稳定化。

### 6.4 任务接口

```csharp
public interface IAutomationTask
{
    string Id { get; }
    Task RunAsync(TaskContext context, CancellationToken cancellationToken);
}
```

`TaskContext` 提供：

- 截图服务；
- 游戏窗口信息；
- 识别服务；
- 输入控制器；
- 配置快照；
- 日志和证据记录；
- UI 状态发布；
- 通知服务。

### 6.5 输入接口

```csharp
public interface IInputController
{
    Task<ActionResult> ClickAsync(
        ClickTarget target,
        ActionPolicy policy,
        CancellationToken cancellationToken);
}
```

任务只表达“点击重开确认按钮”，而不是直接传入屏幕坐标。输入控制器负责：

1. 验证窗口；
2. 重新定位目标；
3. 检查置信度；
4. 转换坐标；
5. 执行点击；
6. 等待并验证后置状态。

## 7. 任务模型

参考 BetterGI 的任务分类，本项目使用三类运行单元。

### 7.1 观察器 Observer

持续或定时获取画面，只更新状态，不操作游戏。

示例：

- 开局信息观察器；
- 当前资源观察器；
- 未知弹窗检测；
- 游戏窗口状态检测。

### 7.2 独立任务 Task

由用户主动启动，完成一个明确流程后退出。

示例：

- 自动刷开局；
- 读取当前局势；
- 保存当前对局快照；
- 识别并打开对应攻略。

### 7.3 决策流程 Workflow

根据状态反复执行“观察—判断—建议或行动—验证”。

示例：

- 局势评估与下一步建议；
- 用户确认后的连续辅助；
- 后期有限自动运营。

第一版不实现通用脚本系统。任务数量增长到确有社区扩展需求时，再抽象任务包清单与权限声明。

## 8. 图像识别架构

### 8.1 标准坐标

首个版本以 `1920×1080、16:9、100% 游戏 UI 缩放` 为标准空间：

- 所有 ROI 和模板位置记录为标准坐标；
- 截图时计算从实际客户区到标准空间的变换；
- 点击前再从标准空间转换回实际窗口坐标；
- 非 16:9 分辨率首版提示不支持，不静默使用错误坐标。

后续通过布局锚点扩展分辨率，而不是简单拉伸全部模板。

### 8.2 页面优先

识别顺序固定为：

```text
识别当前页面
→ 找到页面锚点
→ 确定各信息 ROI
→ 识别图标或文字
→ 交叉校验
→ 多帧稳定
→ 生成状态
```

禁止在未识别页面时直接全屏搜索按钮并点击。

### 8.3 多引擎识别

- 固定按钮和图标：OpenCV 模板匹配；
- 轻微缩放或旋转的元素：特征匹配；
- 名称和数值：OCR；
- 卡片或复杂视觉对象：ONNX 分类/检测；
- 页面切换和动画结束：图像差异检测；
- 最终状态：多种识别证据交叉确认。

### 8.4 识别资源

每个功能模块拥有自己的资源目录和声明文件：

```text
OpeningReroll/
  Assets/
    1920x1080/
      restart_button.png
      confirm_button.png
    recognition.json
  OpeningRerollTask.cs
  OpeningRerollConfig.cs
  OpeningRerollStateMachine.cs
```

`recognition.json` 保存：

- 识别对象 ID；
- 模板文件；
- ROI；
- 匹配算法；
- 阈值；
- 适用页面；
- 适用游戏版本。

## 9. 自动化运行时

### 9.1 单任务执行

同一时间只允许一个会产生输入的任务运行。观察器可以共享同一截图帧，不能各自高频截图。

### 9.2 帧分发

建立单一 `FrameProvider`：

- 按需捕获；
- 向多个观察器分发只读帧；
- 控制最大帧率；
- 统计捕获与识别耗时；
- 不再使用的图像及时释放。

### 9.3 取消与暂停

所有循环、识别和点击必须接收同一个取消令牌。停止来源包括：

- 用户点击停止；
- 全局紧急热键；
- 游戏窗口失焦；
- 分辨率或窗口位置异常；
- 达到运行次数或时间限制；
- 连续识别失败；
- 发现未知页面。

### 9.4 状态机

刷开局任务使用显式状态机，不写成长串延时：

```text
LocateWindow
→ DetectPage
→ ReadOpening
→ Stabilize
→ Evaluate
  ├─ Keep → Notify → Completed
  ├─ Reroll → OpenRestart → ConfirmRestart → WaitLoading
  │                                      └──→ DetectPage
  └─ Unknown → Paused
```

每个状态具有：

- 允许进入的前置状态；
- 页面条件；
- 最大持续时间；
- 最大重试次数；
- 可执行动作；
- 成功后置状态；
- 失败恢复策略。

## 10. 桌面界面

参考 BetterGI 的主窗口加遮罩层形式，但按本项目功能重新设计。

主导航：

- 首页；
- 开局助手；
- 攻略资料；
- 局势分析；
- 识别调试；
- 设置；
- 日志与关于。

首页：

- 游戏窗口连接状态；
- 当前运行模式；
- 一键启动开局助手；
- 暂停和停止；
- 最近一次识别结果；
- 当前任务日志；
- 免费与非官方声明。

遮罩层：

- 当前任务名称；
- 运行/暂停状态；
- 开局重刷次数；
- 最近识别结果；
- 识别框和置信度；
- 紧急停止提示。

遮罩层默认不接收鼠标输入，避免遮挡游戏操作。

## 11. 配置和数据

目录分为：

- `assets/`：随程序发布的识别模板和模型；
- `data/`：公开维护的游戏事实和攻略数据；
- `user/`：用户个人规则和设置；
- `logs/`：运行日志和可选截图证据。

不要把程序更新覆盖用户目录。

数据更新和程序更新分开：

- 程序版本负责识别和运行能力；
- 数据版本负责词条、投资策略和攻略；
- 每个数据包声明支持的游戏版本和 schema 版本；
- 数据不兼容时拒绝静默加载。

## 12. 第一阶段实现边界

首轮只实现：

1. WPF 主程序与依赖注入；
2. 游戏窗口选择；
3. Windows Graphics Capture；
4. `1920×1080、16:9` 标准坐标；
5. 页面识别；
6. 投资策略与敌人词条识别；
7. 开局状态稳定化；
8. 开局规则判断；
9. 观察模式和识别调试页；
10. 离线截图测试。

第二轮再实现：

1. 输入控制器；
2. 重开状态机；
3. 遮罩层；
4. 全局紧急停止；
5. 操作前后验证；
6. 自动刷开局。

攻略、完整对局识别和建议引擎继续使用同一内核扩展，不进入首轮范围。

## 13. 需要从 BetterGI 使用经验中确认的设计偏好

用户深度使用过 BetterGI，可以优先根据实际体验决定：

- 是否保留类似 BetterGI 的启动页布局；
- 是否需要游戏内遮罩层；
- 日志应常驻显示还是默认折叠；
- 配置按功能分页还是集中在设置页；
- 自动化任务是否使用统一调度器；
- 是否需要类似脚本仓库的社区资料订阅；
- 哪些 BetterGI 操作体验值得保留；
- 哪些复杂或不直观的体验应避免。

