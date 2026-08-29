# 可直接复制：视觉识别与窗口捕获对话

```text项目路径：D:\Codex-2

请先完整阅读：

D:\Codex-2\docs\PROJECT_STATUS.md
D:\Codex-2\docs\ARCHITECTURE_BOUNDARIES.md
D:\Codex-2\docs\DECISIONS.md
D:\Codex-2\docs\handoffs\vision-recognition.md

本对话负责：
游戏窗口发现与捕获、WGC/GDI 兼容性、模板匹配、页面分类、OCR、角色卡和其他视觉读数；只输出“看见了什么”，不决定页面流程或执行点击。

本轮任务：
用户会在本提示词后直接描述刚刚实机遇到的 Bug。先主动检查最新 logs、截图、异常堆栈和相关代码，判断是否归属于 Vision；属于本模块时修复最高优先级根因，不属于时不要越界修改，明确告诉用户应转发给哪个模块。Vision 内部优先级依次为：应用启动/WGC 捕获失败、黑帧或尺寸错误、危险页面误判、关键 OCR/角色识别失败、低优先级准确率改进。

先使用已有日志、异常堆栈、截图和 PageReplay fixture 离线复现。若 Bug 是旧日志中的 CreateDirect3DDevice InvalidCastException，先确认当前代码和 live-test-30 是否仍能复现，不能仅根据旧日志再次改写捕获后端。一次只解决一个根因，不顺带重写 Tasks 状态机。

验收标准：
1. 给出最小复现、根因、修改边界和失败前后的证据；没有可靠复现时明确写“尚未确认”。
2. 新增或更新能真实覆盖该缺陷的 L2/L3 回归测试，完整解决方案编译和测试通过。
3. 不把离线修复宣称为 L4；更新 vision-recognition.md 和 PROJECT_STATUS.md，并列出仍需测试对话执行的实机回归步骤。

工作要求：

- 先检查现有实现，不要从头重做。
- 区分“代码存在”“测试通过”和“游戏实机通过”。
- 不修改页面业务顺序、筛选规则、奖励关动作或 UI。
- 保持识别失败可重试、低置信度返回 Unknown、多方法/多帧确认和备用恢复思想。
- 不要把 GDI 类存在误报为生产备用路径；若要接入必须说明 DI 和质量影响。
- 完成后更新对应 handoff 文档和 PROJECT_STATUS.md。
- 未经我明确要求，不要启动或操纵游戏、BGI 或 Windows 桌面；不得运行 driver-repair.ps1。
```

