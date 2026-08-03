# 项目总控与集成

## 模块目标

维护项目真实状态、模块边界、依赖装配、关键决定、候选构建和跨对话集成结论；确保“代码存在”不被误报为“游戏实机完成”。

## 不属于本模块的内容

不直接拥有模板/OCR 算法、鼠标动作细节、开局筛选算法、奖励关动作实现、UI 视觉实现或上游知识数据内容；这些应由对应模块实现后交总控审查。

## 当前真实状态

交接入口已经建立。当前解决方案含 Core、Vision、Game、Automation、Tasks、App、Tests 七个项目；生产依赖在 `src/CurrencyWarsAssistant.App/App.xaml.cs` 装配。应用语义版本尚未定义，运行时数据版本为 4.4。2026-07-26 审计构建和 112 项测试通过，但当前候选产物缺少实机日志。

## 已实机验证

只能确认较早产物曾完成 HUD → 指南 → 货币战争 → 敌人/投资环境 → 1-1，并触发过已有对局保护和未知页 Esc 恢复。证据为 `logs/test-session-20260725-015803.jsonl`。这些证据不能自动授予 `live-test-30` L4 状态。

## 已实现但未实机验证

- `App.xaml.cs` 已把当前捕获、识别、导航、重刷、退出恢复、布阵和奖励关控制器接到同一生产路径。
- `PROJECT_STATUS.md` 已记录 112/112 测试结果、证据等级和当前风险。
- `artifacts/live-test-30` 的项目 DLL 与本地 Release 输出哈希一致，但没有绑定的启动或流程实机记录。

## 部分实现或占位功能

- 发布版本号、构建清单、变更记录和可追溯 Git 历史尚未建立。
- `OpeningRerollTask` 等旧平行路径仍在代码中，但未接入生产 DI。
- 正式 raw/schema/validate/transform/runtime 数据流水线尚未实现。

## 核心代码入口

- `src/CurrencyWarsAssistant.App/App.xaml.cs`
- `src/CurrencyWarsAssistant.App/MainViewModel.cs`
- `src/CurrencyWarsAssistant.Tasks/OpeningRerollLoopCoordinator.cs`
- `CurrencyWarsAssistant.sln`
- `docs/PROJECT_STATUS.md`
- `docs/ARCHITECTURE_BOUNDARIES.md`
- `docs/DECISIONS.md`

## 输入与输出

输入：各模块 handoff、代码差异、测试结果、离线回放报告、实机日志/截图和上游数据转换报告。输出：模块归属决定、集成结论、L1/L2/L3/L4 状态、关键决策和发布候选资格。

## 依赖的其他模块

依赖所有模块提供可复核证据；依赖 App 的 DI 入口确认功能是否接入，依赖 Testing/Release 确认证据等级，依赖数据模块确认数据契约。

## 不允许破坏的既有规则

- 不重写已有旧产物实机证据的正向流程，除非有明确缺陷和回归方案。
- 不把编译、单元测试或截图回放表述为当前实机跑通。
- 不在未获授权时启动或操纵游戏、BGI、桌面或其他应用。
- 其他对话的交付必须检查 DI、主状态机、配置复制、数据引用和真实用户流程。

## 已知问题

- `.git` 目录为空，无法核验变更归属和安全回滚。
- 最新日志早于 `live-test-30`，候选构建资格无法闭环。
- README/旧计划、运行时代码和现行规则存在少量历史冲突，必须以 `DECISIONS.md` 标示的现行决定为准。

## 下一步任务

1. 在用户授权后为候选产物建立最小实机冒烟证据，但本交接阶段不执行。
2. 建立版本/构建 manifest，并先确认是否存在应恢复的 Git 历史。
3. 按开局恢复、数据导入、布阵、奖励关的优先级审查独立模块交付。

## 验收标准

- 每项能力明确标注 L1/L2/L3/L4 或“尚未确认”。
- 每个生产功能可追溯到 DI 入口、状态机入口、配置/数据和测试。
- 候选构建有源指纹、构建命令、测试报告；称为可用构建还必须有对应实机记录。
- 项目级结论同步回三份总控文档，而不是只留在聊天或模块 handoff。

## 测试与构建命令

```powershell
.\.tools\dotnet\dotnet.exe test CurrencyWarsAssistant.sln -c Debug --no-restore --nologo
.\.tools\dotnet\dotnet.exe build CurrencyWarsAssistant.sln -c Release --no-restore --nologo
```

上述命令只构建/测试，不启动应用。实机测试必须另获用户明确授权。

## 最近可用构建

**尚未确认。** 最新候选为 `artifacts/live-test-30`；其 DLL 与本地 Release 一致，但缺少该产物对应的当前实机日志。

## 最后更新时间

2026-07-26（Asia/Shanghai）
