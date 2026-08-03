# 可直接复制：开局导航与自动重刷对话

```text
项目路径：D:\Codex-2

请先完整阅读：

D:\Codex-2\docs\PROJECT_STATUS.md
D:\Codex-2\docs\ARCHITECTURE_BOUNDARIES.md
D:\Codex-2\docs\DECISIONS.md
D:\Codex-2\docs\handoffs\opening-reroll.md

本对话负责：
HUD 到 1-1 的正向导航、开局规则评估、多方案选择与完整方案锁定、不合格开局退出结算、返回首页和连续重刷；协调识别结果与安全输入，但不实现底层视觉算法或奖励关内部动作。

本轮任务：
用户会在本提示词后直接描述刚刚实机遇到的 Bug。先主动检查最新 logs、截图和相关代码，判断是否归属于开局导航/重刷；属于本模块时修复最高优先级根因，不属于时不要越界修改，明确告诉用户应转发给哪个模块。优先顺序为：已有进行中对局安全停止、错误页面/危险输入、正向导航回归、不合格退出恢复、多轮循环、失焦/回焦和未知页恢复。

如果尚无 live-test-30 的捕获冒烟证据，不开始重写导航；先把阻塞退回 testing-release 或 vision-recognition。修复时复用 CurrencyWarsNavigationTask、OpeningRerollLoopCoordinator 和 CurrencyWarsRejectedOpeningRecovery，不启用未注册的旧 OpeningRerollTask 平行路径。

验收标准：
1. 修复有明确复现、根因和最小代码范围；已有对局保护、取消令牌、前台检查和后置页面确认不能退化。
2. 筛选测试保持：投资环境仅正选；阵营/负面词条支持正选与排除；特殊组合全部同时出现；多方案为 OR；同时命中随机锁定一整套且不混合。
3. 完整测试通过；更新 opening-reroll.md 和 PROJECT_STATUS.md，并给出 testing-release 可直接执行的最小实机回归步骤，不自行宣称 L4。

工作要求：

- 先检查现有实现，不要从头重做。
- 区分“代码存在”“测试通过”和“游戏实机通过”。
- 自动刷取不设总轮数和总时间上限，但每个危险动作必须有有限尝试、重新识别、恢复和安全停止。
- 未知页面默认 Esc 后重新识别；失焦暂停、回焦恢复；已有对局必须停止。
- 不修改 Vision 算法、奖励关动作、数据导入或 UI，除非接口确实必须变化；跨模块变化要单列。
- 完成后更新对应 handoff 文档和 PROJECT_STATUS.md。
- 未经我明确要求，不要启动或操纵游戏、BGI 或 Windows 桌面。
```
