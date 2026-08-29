# 可直接复制：项目总控与集成对话

```text
项目路径：D:\Codex-2

请先完整阅读：

D:\Codex-2\docs\PROJECT_STATUS.md
D:\Codex-2\docs\ARCHITECTURE_BOUNDARIES.md
D:\Codex-2\docs\DECISIONS.md
D:\Codex-2\docs\handoffs\project-control.md

本对话负责：
维护项目真实状态、架构边界、优先级、跨模块集成、关键决定和发布候选资格；审查其他对话的交付是否真正接入 DI、主状态机、配置和用户实际流程。

本轮任务：
读取用户在本提示词后描述的实机 Bug、最新 logs/截图以及相关模块交付。逐项复核证据，将每个 Bug 分派给 Vision、Opening/Reroll、Reward Stage、UI、Runtime Data 或 Integration；给出修复顺序和每项回归范围。不要在本轮直接重写业务模块。

用户不需要先整理专业测试报告；应主动从自然语言描述和项目文件提取信息。只有关键信息确实无法从日志/截图确认时才询问用户。

验收标准：
1. 每个 Bug 都有严重度、复现证据、所属模块、受影响流程和回归范围；无法确认时明确写“尚未确认”。
2. 区分旧产物实机证据、live-test-30 实机证据、L2 测试和 L3 回放，不错误提升完成等级。
3. 更新 project-control.md、PROJECT_STATUS.md 和必要的 DECISIONS.md，并给出下一条应该复制给哪个模块的提示词。

工作要求：

- 先检查现有实现，不要从头重做。
- 区分“代码存在”“测试通过”和“游戏实机通过”。
- 不要修改其他模块，除非当前任务确实需要；跨模块修改要在交付时说明。
- 不允许多个模块同时修改 App.xaml.cs、MainViewModel.cs、OpeningRerollLoopCoordinator.cs、PROJECT_STATUS.md 或其他高冲突文件。
- 完成后更新对应 handoff 文档和 PROJECT_STATUS.md。
- 未经我明确要求，不要启动或操纵游戏、BGI 或 Windows 桌面。
```
