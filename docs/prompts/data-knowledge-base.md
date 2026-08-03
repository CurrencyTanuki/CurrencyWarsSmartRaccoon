# 可直接复制：运行时数据 Schema 与导入对话

```text
项目路径：D:\Codex-2

请先完整阅读：

D:\Codex-2\docs\PROJECT_STATUS.md
D:\Codex-2\docs\ARCHITECTURE_BOUNDARIES.md
D:\Codex-2\docs\DECISIONS.md
D:\Codex-2\docs\handoffs\data-knowledge-base.md
D:\Codex-2\docs\DATA_IMPORT_4.4.md

本对话负责：
把“数据与攻略知识库”对话生成的 JSON 视为上游 raw，建立 schema、版本/字段/ID/枚举/引用校验、确定性转换、未知字段保留和运行时规范化输出。本对话不负责联网采集攻略，也不修改自动化流程。

本轮任务：
先只读盘点现有 data/4.4、GameDataCatalog、Import-GameData.ps1 和上游已交付 JSON。确认上游是否仍在写文件；若在写，只做 schema 草案和只读验证，不修改同名数据文件。

在不覆盖现有 data/4.4 的前提下，设计并实现最小可重复流水线：raw 输入目录 → JSON Schema → 结构/枚举/稳定 ID/跨文件引用验证 → 显式转换 → 独立 runtime 输出目录 → 转换报告。优先用一类最小数据做纵向样板，不一次迁移全部数据。若当前上游样本尚未到位，先完成接口、目录约定和 fixture，不伪造数据。

验收标准：
1. 同一 raw 输入重复执行得到稳定输出；字段映射、默认值、拒绝项和未知字段均有报告，未知字段没有被删除。
2. 校验覆盖 schema_version、game_version、ID 唯一性、枚举和值域、跨文件引用；不兼容数据不会进入运行时。
3. 旧 data/4.4 和 DATA_IMPORT_4.4.md、SCREEN_FLOW_1920x1080.md 均未被覆盖；新增测试通过，并更新 data-knowledge-base.md 和 PROJECT_STATUS.md。

工作要求：

- 先检查现有实现，不要从头重做 GameDataCatalog。
- 把上游 JSON 当 raw，不要直接塞进运行时，不要擅自删字段。
- 所有导入/转换必须脚本化、可重复，禁止手工复制。
- 投资环境只正选；阵营和负面词条支持正选/排除；特殊组合保留组合边界；方案不能混合。
- 若上游对话正在写相同文件，立即停止写入并只输出冲突清单。
- 不修改 Vision、Automation、Tasks 或 UI；运行时模型确需变化时先提交接口影响说明。
- 完成后更新对应 handoff 文档和 PROJECT_STATUS.md。
- 未经我明确要求，不要启动或操纵游戏、BGI 或 Windows 桌面。
```
