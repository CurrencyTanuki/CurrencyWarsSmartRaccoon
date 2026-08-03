# 可直接复制：备战、奖励关与投资策略对话

```text
项目路径：D:\Codex-2

请先完整阅读：

D:\Codex-2\docs\PROJECT_STATUS.md
D:\Codex-2\docs\ARCHITECTURE_BOUNDARIES.md
D:\Codex-2\docs\DECISIONS.md
D:\Codex-2\docs\handoffs\reward-stage.md

本对话负责：
命中开局后的 1-1 角色卡识别协作、布阵计划与验证、商店、晶矿球、1-1/1-2 战斗、结算、投资策略识别/刷新/选择和超频选项；只消费已经锁定的一套完整开局方案。

本轮任务：
用户会在本提示词后直接描述刚刚实机遇到的备战/奖励关 Bug。先主动检查最新 logs、截图和相关代码，判断问题属于 Reward Stage 业务状态机还是 Vision 像素/OCR；不属于本模块时不要越界修改，明确告诉用户应转发给哪个模块。本模块当前代码已接入但没有可靠完整实机成功证据。

按用户实际测到的单段推进，一次只处理一个阶段：A. 1-1 只识别不拖动；B. 单次布阵并验证；C. 商店识别/开关；D. 1-1 出战与结算；E. 晶矿球和 1-2；F. 投资策略识别与单槽刷新。不要把整条连续自动化作为一个修复单元。

验收标准：
1. 当前阶段有明确进入页面、允许动作、有限尝试、成功后置页面、失败恢复和安全停止条件。
2. 回归测试证明只使用 OpeningRerollLoopCoordinator 锁定的完整方案，不混合其他方案的环境、策略、角色或排除条件。
3. 完整测试通过；更新 reward-stage.md 和 PROJECT_STATUS.md，只把完成的单段标为相应 L2/L3/L4，不把单段成功提升为全流程成功。

工作要求：

- 先检查现有实现，不要从头重做 RewardStageAutomationController。
- 区分“代码存在”“测试通过”和“游戏实机通过”。
- 不重新判断开局方案，不修改 OpeningFilterEvaluator 语义。
- 危险输入必须前台保护、有限次数、页面重识别和后置验证；失焦暂停，未知页先 Esc 恢复。
- 在完整 L4 前，奖励关仍按实验能力处理；默认是否关闭由总控决定，不自行扩大默认自动化范围。
- 完成后更新对应 handoff 文档和 PROJECT_STATUS.md。
- 未经我明确要求，不要启动或操纵游戏、BGI 或 Windows 桌面。
```
