# 架构边界与集成约定

> 生效日期：2026-07-26  
> 目的：让新对话先确定“功能由谁负责、从哪里接入、不能越过什么边界”，再开始写代码。

## 1. 当前依赖方向

```text
CurrencyWarsAssistant.Core
  ↑                 ↑
  │                 └── CurrencyWarsAssistant.Game
  └── CurrencyWarsAssistant.Vision
            ↑
CurrencyWarsAssistant.Automation
            ↑
CurrencyWarsAssistant.Tasks
            ↑
CurrencyWarsAssistant.App

CurrencyWarsAssistant.Tests → 以上所有模块
```

依赖约束：

- `Core` 只保存几何、事件和跨层基础契约，不引用 WPF、Win32、游戏数据或具体流程。
- `Game` 保存事实数据模型和纯规则，不截图、不 OCR、不点击。
- `Vision` 只负责窗口、帧、模板和视觉结果，不决定保留哪个开局。
- `Automation` 只负责焦点、安全输入和坐标执行，不解释游戏业务。
- `Tasks` 组合感知、规则和输入，拥有状态机、重试、恢复及任务结果。
- `App` 只做装配、设置、展示和用户命令；不得在 code-behind 中复制任务状态机。

## 2. 模块职责和禁止事项

### 2.1 Core

核心入口：`src/CurrencyWarsAssistant.Core/Geometry.cs`、`TaskEvents.cs`

负责：

- 像素点、矩形等无平台或低平台耦合模型；
- 结构化任务事件接口；
- 真正需要跨多个项目复用的稳定契约。

禁止：业务页面 ID、角色名、固定坐标、文件路径、WPF 控件。

### 2.2 Vision

核心入口：

- `GameWindowService.cs`
- `WindowsGraphicsGameCapture.cs`
- `GamePageClassifier.cs`
- `TemplateMatching.cs`
- `CharacterCardRecognition.cs`

负责：

- 发现和刷新游戏窗口；
- 捕获游戏客户区；
- 1920×1080 标准空间归一化；
- 页面锚点、角色卡等纯视觉分类；
- 输出置信度、槽位和证据位置。

禁止：决定点击哪个业务按钮、读取用户筛选偏好、直接写 UI 状态。

视觉安全约定：

- 危险页面至少应有两个独立锚点，或模板 + OCR/布局的交叉确认；
- 单帧命中不得直接触发危险动作；
- 每个视觉结果必须保留置信度和 ROI；
- WGC 和未来 GDI/其他捕获路线对上层暴露同一 `IGameCapture` 契约；备用捕获不能改变业务流程语义。

### 2.3 Game

核心入口：`GameDataCatalog.cs`、`OpeningFilters.cs`

负责：

- 运行时事实数据模型；
- ID、枚举、数量和引用校验；
- 不依赖屏幕的纯筛选逻辑；
- 用户配置转换后的稳定业务规则。

禁止：把 OCR 原文当稳定 ID、保存鼠标坐标、引用 WPF ViewModel。

`OpeningRules.cs` 当前属于未接入的旧路径，不是生产规则入口。当前生产规则由 `MainViewModel.BuildFilterSet()` 生成 `OpeningFilterSet`，再交给 `OpeningFilterEvaluator`。

### 2.4 Automation

核心入口：`GameForegroundGuard.cs`、`Win32InputController.cs`

负责：

- 游戏失焦时暂停，回焦稳定后恢复；
- 点击、拖动、按键和修饰键输入；
- 点击前刷新窗口和坐标映射；
- 输入失败的底层错误结果。

禁止：在输入控制器里猜测页面、实现奖励关或开局流程、绕过焦点保护。

### 2.5 Tasks

核心入口：

- `CurrencyWarsNavigation.cs`
- `OpeningRerollLoopCoordinator.cs`
- `CurrencyWarsRejectedOpeningRecovery.cs`
- `PreparationFormation.cs`
- `RewardStageAutomation.cs`
- `UnknownPageEscapeRecovery.cs`

负责：

- 识别当前状态并生成语义动作；
- 动作前置条件、执行、后置验证；
- 多帧稳定、重试、备用路线和恢复；
- 可取消的长任务；
- 把可恢复失败和危险失败区分开。

每个任务动作必须遵循：

```text
重新捕获并确认当前状态
→ 确认动作适用于该状态
→ 执行一次输入
→ 等待并确认后置状态
→ 成功 / 重试识别 / 进入恢复 / 安全停止
```

不得使用“无限重复固定坐标点击”代替状态机。自动任务可以没有总轮数和总时长上限，但每个动作阶段必须有有限尝试预算；预算耗尽后应重新识别、走备用恢复或对危险场景安全停止。

### 2.6 App

核心入口：`App.xaml.cs`、`MainViewModel.cs`、`MainWindow.xaml`

负责：

- DI 装配和实际运行时入口；
- 深蓝现代主题和用户设置；
- 启动、观察、自动、停止命令；
- 显示结构化日志和成熟度提示；
- 把用户选择转换成 Game/Tasks 契约。

集成完成的最低条件是：

1. 相关实现已在 `App.xaml.cs` 注册；
2. `MainViewModel` 或现有任务能够到达该实现；
3. 所需配置/模板/数据会复制到输出目录；
4. 失败会返回主状态机，不会只在内部吞掉；
5. UI 对实验能力默认采取保守开关。

仅新增类、测试或 JSON，不满足“已接入”。

## 3. 当前生产主流程

```text
MainViewModel.AutoRerollCommand
  → OpeningRerollLoopCoordinator
    → CurrencyWarsNavigationTask
      → GameForegroundGuard
      → IGameCapture
      → IGamePageClassifier / OcrOpeningPageReader
      → Win32InputController
    → OpeningFilterEvaluator
      ├─ 不匹配 → CurrencyWarsRejectedOpeningRecovery → 下一轮
      └─ 匹配
          → CurrencyWarsNavigationTask 选择投资环境并到 1-1
          → PreparationBoardController
          → RewardStageAutomationController（设置开启时）
```

安全短路：

- `rank_difficulty_in_progress` → `ActiveRunDetected`，立即停止且不得破坏用户进度；
- 失焦 → `GameForegroundGuard` 等待回焦，不消耗活动运行时预算；
- 未知页面 → `UnknownPageEscapeRecovery` 使用 Esc 后重新分类；
- 相同识别失败连续出现三次 → 协调器停止无效重复；
- 用户 `Ctrl+Shift+F12` 或停止按钮 → 取消令牌传播到任务。

## 4. 配置权威

### 当前运行时会加载

- `config/navigation-flow.json`
- `config/page-recognition.1920x1080.json`
- `config/templates/1920x1080/`
- `data/4.4/`
- 用户本地设置文件（由 `MainViewModel` 管理）

### 当前不会加载或只作参考

- `config/recognition.json`
- `config/opening-rules.json`
- `config/screen-layouts.1920x1080.json`
- `OpeningRerollTask` 及其 `OpeningRecognitionConfig` 路径

修改“不会加载”的文件不会改变当前应用行为。任何对这些文件的工作都必须先决定是重新接入、迁移还是标记 legacy。

## 5. 上游数据边界

“数据与攻略知识库”对话是上游生产者，其 JSON 一律视为原始数据，不是运行时契约。

目标流水线：

```text
data/raw/<game-version>/<dataset>/
  → schemas/<dataset>.schema.json
  → tools/Validate-GameData.*
  → tools/Transform-GameData.*
  → data/runtime/<schema-version>/<game-version>/
  → GameDataCatalogLoader
```

在目录迁移完成前，现有 `data/4.4/` 仍是生产运行时目录，不允许直接覆盖。

导入层必须：

- 校验 `schema_version`、`game_version`、记录 ID、枚举和值域；
- 校验跨文件引用和重复 ID/名称；
- 保留上游未知字段到 raw 层，不得静默删除；
- 输出转换报告，列出保留、映射、默认、拒绝和未知字段；
- 转换可重复执行，同一输入得到稳定输出；
- 不依赖手工复制；
- 在上游对话写同一文件时避免并发修改。

运行时代码只消费规范化字段和稳定 ID，不直接猜测上游字段。

### 5.1 共享数据模型与输入输出

| 边界 | 输入 | 输出 | 当前权威模型 |
|---|---|---|---|
| 窗口捕获 → 视觉 | 游戏窗口句柄、客户区尺寸 | `CaptureFrame`/位图帧 | Vision 捕获接口与 `WindowsGraphicsGameCapture` |
| 视觉 → 导航 | 位图帧、页面识别配置 | 页面 ID、置信度、锚点结果 | `GamePageClassifier`、`PageClassificationResult` |
| OCR → 开局规则 | 敌方阵营、敌方词条、三个投资环境 | `OpeningSnapshot` | Tasks 读取器 + Game 模型 |
| 规则 → 重刷协调器 | `OpeningFilterSet`、多个 `OpeningFilterProfile` | 命中/拒绝原因、锁定的完整方案 | `OpeningFilterEvaluator`、`OpeningRerollLoopCoordinator` |
| 协调器 → 奖励关 | 已锁定方案、角色/商店/投资设置 | `RewardStageAutomationResult` 和阶段事件 | `RewardStageAutomationController` |
| App → Tasks | 用户配置、取消令牌、日志回调 | 状态文本、最终任务结果 | `MainViewModel` |
| 上游知识库 → 运行时 | 原始 JSON、版本和来源元数据 | 校验报告、规范化 JSON、转换报告 | **尚未实现；不得由运行时直接消费原始 JSON** |

### 5.2 数据知识库导入边界

建议固定为 `raw → schema validation → semantic/reference validation → transform → runtime` 五层：

1. 上游对话拥有 raw 文件内容和来源字段；总控不得擅自删除未知字段。
2. schema 层拥有字段类型、必填项、版本和枚举定义。
3. 语义校验层拥有稳定 ID 唯一性、跨文件引用、枚举合法性和游戏版本一致性。
4. transform 层负责显式映射、默认值和兼容报告；必须可重复执行。
5. Runtime 层只消费规范化输出，不反向修改 raw。

当前 `tools/Import-GameData.ps1` 只覆盖四份 Markdown 报告，不是完整 JSON 导入层；`data/4.4/characters.generated.json` 含有运行时未消费的上游字段，不能据此宣布 schema 已建立。

## 6. 新功能归属判定

开始新功能前按下表判断：

| 问题 | 归属 |
|---|---|
| 只是从像素得到“看见了什么” | Vision |
| 只是定义游戏事实、ID 或纯规则 | Game |
| 只是安全执行输入或焦点控制 | Automation |
| 包含页面序列、重试、恢复、业务动作 | Tasks |
| 只是用户设置、展示、命令、装配 | App |
| 只是原始资料清洗和转换 | 数据工具层，不进入运行时模块 |

若一个功能横跨两层，先定义接口和结果模型，再分别实现；不要把 OCR、业务判断、鼠标点击和 UI 更新塞进同一个方法。

## 7. 交付审查清单

其他对话提交代码或数据后，总控至少检查：

- 修改文件是否属于声明的模块；
- 是否重复实现已有服务或绕开主状态机；
- 是否注册到 DI、命令或任务入口；
- 输出目录是否包含所需配置、模板和数据；
- 页面 ID、动作 ID、稳定数据 ID 是否一致；
- 是否有有限动作预算、取消令牌、失焦保护和后置验证；
- 未知页、动画、OCR 不完整、窗口失效时如何处理；
- 测试属于 L1/L2/L3/L4 哪一级；
- 是否需要更新 `PROJECT_STATUS.md` 和 `DECISIONS.md`；
- 是否与上游数据对话同时写相同文件。

### 7.1 最终解释权

| 规则/资产 | 最终解释模块或文件 | 说明 |
|---|---|---|
| 功能完成等级与发布资格 | 总控：`PROJECT_STATUS.md`、测试/实机证据 | 类或测试存在不能自行提升等级 |
| 开局筛选语义 | Game：`OpeningFilters.cs` 与相应测试 | UI 只能构造规则，Tasks 不能另写一套语义 |
| 多方案选择和整套锁定 | Tasks：`OpeningRerollLoopCoordinator.cs` | 命中后必须锁定一个完整方案，不跨方案拼接 |
| 页面 ID、锚点和识别阈值 | Vision + `config/page-recognition.1920x1080.json` | Tasks 只消费页面结果 |
| 页面顺序、恢复和动作后置验证 | Tasks + `config/navigation-flow.json` | 坐标属于动作配置；业务状态机决定何时可执行 |
| 前台保护和输入安全 | Automation | 任何模块都不得绕过前台检查直接输入 |
| UI 视觉规范和实验能力提示 | App + `DECISIONS.md` | 业务层不持有颜色、控件和文案逻辑 |
| 上游原始数据 | 数据知识库对话 | 运行时项目只读接收，不覆盖上游字段 |
| Runtime schema 与转换结果 | 数据导入/转换层（待建立） | 建立后由 schema、验证器、转换报告共同定义 |

### 7.2 高冲突文件

以下文件应避免被多个对话同时修改，交付前必须由总控协调：

- `src/CurrencyWarsAssistant.App/App.xaml.cs`：所有生产依赖装配汇合点。
- `src/CurrencyWarsAssistant.App/MainViewModel.cs`：UI 设置、规则构造、任务参数和日志汇合点。
- `src/CurrencyWarsAssistant.Tasks/OpeningRerollLoopCoordinator.cs`：开局、恢复、布阵和奖励关的总状态机。
- `src/CurrencyWarsAssistant.Tasks/RewardStageAutomation.cs`：多个奖励子流程集中在单文件。
- `src/CurrencyWarsAssistant.Game/OpeningFilters.cs`：筛选语义和共享模型。
- `config/page-recognition.1920x1080.json`、`config/navigation-flow.json`：页面 ID/动作 ID 被代码和测试共同引用。
- `data/4.4/` 与未来 raw/schema/normalized 目录：上游数据对话和运行时导入任务存在覆盖风险。
- `docs/PROJECT_STATUS.md`、`docs/DECISIONS.md`：只能由总控整合结论，模块对话提交 handoff 建议。

## 8. 禁止事项

- 未确认前重写已经有旧产物实机证据的正向开局流程；
- 因为类、方法或测试存在就宣布功能完成；
- 把上游不兼容 JSON 直接复制进运行时目录；
- 在未知页或低置信度页连续点击；
- 用总时长上限掩盖卡死，也不得用无限点击掩盖恢复缺失；
- 将游戏实机测试授权扩展到桌面、BGI 或其他应用；
- 在根目录加入与项目无关、带机器专属路径或硬件 ID 的运维脚本作为发布依赖。
