# 文件清单与版本

版本信息见 `VERSION.json`；逐文件大小和 SHA-256 见构建生成的 `PACKAGE_MANIFEST.json`。

| 路径 | 内容 |
|---|---|
| `README_START_HERE.md` | 独立使用入口。 |
| `TASK_PROMPT.md` | 可直接交给另一 AI 的完整任务。 |
| `schemas/research-evidence.v1.schema.json` | 原始证据格式。 |
| `schemas/guide-playbook.v1.schema.json` | 程序攻略格式。 |
| `docs/PROJECT_BACKGROUND_AND_GOALS.md` | 项目背景与目标。 |
| `docs/GAME_GLOSSARY.md` | 游戏术语。 |
| `docs/FIELD_REFERENCE_AND_MAPPING.md` | 字段与现有 Advisor 映射。 |
| `docs/SOURCE_AND_QUALITY_RULES.md` | 来源、冲突、版权和隐私规范。 |
| `docs/VALIDATION_GUIDE.md` | 校验工具说明。 |
| `docs/ACCEPTANCE_CHECKLIST.md` | 外部交付验收清单。 |
| `standard-ids/*.json` | 4.4 标准实体 ID 目录。 |
| `examples/valid/*.json` | 完整、分支/转型、信息不完整/冲突示例。 |
| `examples/invalid/*.json` | 必须被拒绝的非法夹具。 |
| `tools/validate_all.py` | 零第三方依赖的全量校验器。 |
| `tools/build_standard_ids.py` | 主项目维护者重新导出 ID 的工具。 |
| `tools/build_examples.py` | 可重复生成包内审计示例。 |
| `tools/build_package.py` | 测试、审计、打包和干净解压复验。 |
| `tests/test_contracts.py` | 合同、非法样本和安全边界测试。 |
| `output-template/` | 外部 AI 的唯一输出目录。 |

本包不包含游戏截图、图标、可执行文件、项目构建物或完整来源正文。
