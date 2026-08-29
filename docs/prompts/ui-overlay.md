# 可直接复制：主界面、悬浮窗与用户控制对话

```text
项目路径：D:\Codex-2

请先完整阅读：

D:\Codex-2\docs\PROJECT_STATUS.md
D:\Codex-2\docs\ARCHITECTURE_BOUNDARIES.md
D:\Codex-2\docs\DECISIONS.md
D:\Codex-2\docs\handoffs\ui-overlay.md

本对话负责：
深蓝色现代 UI、主窗口、设置绑定、状态/日志展示、悬浮操作面板、鼠标穿透、全局停止热键和实验能力提示；把用户选择转换为共享规则/任务参数，不复制业务状态机。

本轮任务：
用户会在本提示词后直接描述刚刚实机遇到的 UI Bug。先主动检查最新 logs、截图、XAML、MainViewModel 和相关代码，判断是否归属于 UI；属于本模块时修复最高优先级根因，不属于时不要越界修改，明确告诉用户应转发给哪个模块。优先处理停止按钮/热键不可用、设置传递错误、实验能力误导、窗口遮挡或不可操作，再处理主题/DPI/布局问题。

若尚无实机 UI 报告，不进行大规模重设计；先只读核对当前 XAML、MainViewModel、热键注册和设置持久化，输出需要 testing-release 验证的清单。奖励关默认关闭与“实验功能”标记属于 DECISIONS.md 中待确认决定，未获总控确认前不要擅自实施。

验收标准：
1. 修改复用现有深蓝主题，所有新增控件包含正常、禁用、错误和焦点状态，不引入默认白色控件。
2. 停止命令、热键注册结果、设置到任务参数的映射有回归证据；筛选方案不得被 UI 扁平混合。
3. 完整构建/相关测试通过；更新 ui-overlay.md 和 PROJECT_STATUS.md，并单列仍需实机确认的 UI 项。

工作要求：

- 先检查现有实现，不要从头重做。
- 区分“代码存在”“测试通过”和“游戏实机通过”。
- 不把页面识别、鼠标输入或 Tasks 流程写进 ViewModel/XAML 后台。
- 投资环境 UI 只支持正选；阵营/负面词条保留 Require/Reject；多套方案保持独立。
- 修改 MainViewModel.cs 前确认没有 Opening/Reroll 或其他对话同时修改。
- 完成后更新对应 handoff 文档和 PROJECT_STATUS.md。
- 未经我明确要求，不要启动或操纵游戏、BGI 或 Windows 桌面。
```
