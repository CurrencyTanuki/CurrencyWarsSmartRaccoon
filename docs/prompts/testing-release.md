# 可直接复制：测试、证据与发布对话

```text
项目路径：D:\Codex-2

请先完整阅读：

D:\Codex-2\docs\PROJECT_STATUS.md
D:\Codex-2\docs\ARCHITECTURE_BOUNDARIES.md
D:\Codex-2\docs\DECISIONS.md
D:\Codex-2\docs\handoffs\testing-release.md

本对话负责：
维护编译、自动测试、离线视觉和用户实机测试四级证据；核对候选构建；整理用户提供的日志、截图、结果和 Bug 清单；判断候选是否具备继续测试或发布资格。本对话不负责操纵游戏，也不负责跨模块修业务代码。

本轮任务：
用户将亲自测试 D:\Codex-2\artifacts\live-test-30，并在本提示词后附上测试结果、Bug 描述或证据路径。本轮只负责：

1. 只读核对候选产物、配置、数据哈希和现有测试结果。
2. 从用户描述和最新 logs/截图中整理通过、失败、阻塞、未测试项目，并严格区分旧产物和 live-test-30。
3. 为每个 Bug 给出严重度、复现信息、证据和所属模块；不确定归属的标为 Integration 并交总控。
4. 一批修复经用户复测后，更新候选构建资格和下一轮人工测试清单。

验收标准：
1. 用户提供的产物、分辨率、时间、操作阶段、最终状态、日志和截图尽量建立对应；缺失信息明确列出，不自行推测。
2. 每个测试项明确标记通过、失败、阻塞或未测试；旧构建证据与 live-test-30 证据严格分开。
3. 输出按优先级排序的 Bug 清单，每项包含复现步骤、预期、实际、证据路径、建议归属模块；更新 testing-release.md 和 PROJECT_STATUS.md。

工作要求：

- 先检查现有实现，不要从头重做。
- 区分“代码存在”“测试通过”“离线视觉通过”和“游戏实机通过”。
- 用户负责所有游戏实机测试；本对话不得启动或操纵助手、游戏、BGI、浏览器、驱动安装器、driver-repair.ps1、其他应用或 Windows 桌面。
- 不要修改业务代码；如确需增加纯测试证据工具，也要先说明范围，且不得掩盖真实 Bug。
- 完成后更新 D:\Codex-2\docs\handoffs\testing-release.md 和 D:\Codex-2\docs\PROJECT_STATUS.md。
```
