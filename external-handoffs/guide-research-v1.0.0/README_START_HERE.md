# 《货币战争》攻略研究交接包 v1.0.0

这是一个可独立使用的研究包。解压后无需读取原项目或先前对话，即可开始：

1. 在网上寻找多来源《货币战争》攻略；
2. 先把每个来源整理为 `research-evidence.v1`；
3. 再把经过引用和冲突保留的结论整理为 `guide-playbook.v1`；
4. 使用包内校验器检查全部 JSON；
5. 将 `output-template` 中的成果目录交回“货币战争智能狸”项目。

## 五分钟开始

1. 阅读 [TASK_PROMPT.md](TASK_PROMPT.md)，将其作为独立 AI 的完整任务提示词。
2. 阅读 [docs/SOURCE_AND_QUALITY_RULES.md](docs/SOURCE_AND_QUALITY_RULES.md)。
3. 查询 ID 时使用 `standard-ids/catalog-index.v1.json` 及同目录各分类文件。
4. 复制 `examples/valid` 中最接近的结构，不要复制其中的玩法结论到无关攻略。
5. 将来源证据写入 `output-template/evidence/`，攻略写入 `output-template/playbooks/`。
6. 在包根目录运行：

```powershell
python tools/validate_all.py --root . --include-output
```

成功时会显示 `PASS`。失败信息会包含具体文件、JSON 路径和原因。

## 两种 JSON 各自解决什么问题

- `research-evidence.v1`：保存“哪里说了什么”，包括网址、作者、日期、视频时间点、事实/作者建议/AI 推断、可信度、未知项和来源冲突。它是可追溯的原始研究层。
- `guide-playbook.v1`：保存“程序在什么条件下给出什么建议”，包括阶段目标、阵容、装备、经济、分支、转型、风险和缺失信息降级。它只引用证据，不把来源原文塞进程序规则。

二者分开可以在来源更新时重新生成攻略，而不丢失原始证据；也能在多来源冲突时保留争议而不是擅自选边。

## 重要边界

- 所有规则都是声明式 JSON；禁止脚本、表达式求值、宏或任意代码。
- 角色、装备、羁绊、投资环境和投资策略优先使用 `standard-ids` 中的正式 ID。
- 找不到 ID 时写入 evidence 的 `otherRefs` 和 `unknowns`，不要发明新 ID。
- 不确定内容必须为 `unknown`/未解决冲突；禁止为了“完整”而编造。
- 只保存必要的短转述、定位和链接；不要打包无授权正文、字幕、图片或视频。

## 目录导航

- `schemas/`：冻结的两份 JSON Schema。
- `docs/`：背景、术语、字段、映射、质量和验收说明。
- `standard-ids/`：从当前项目只读导出的 4.4 标准 ID。
- `examples/valid/`：完整、分支/转型、信息不完整/冲突示例。
- `examples/invalid/`：必须被校验器拒绝的故意非法样本。
- `tools/validate_all.py`：零第三方依赖的一次性校验器。
- `output-template/`：外部 AI 的交付目录。
- `PACKAGE_MANIFEST.json`：文件清单、大小和 SHA-256（构建时生成）。

## 本包不做什么

本包不修改主程序、不接入运行时、不包含截图识别代码，也不替代“货币战争智能狸”的现有 Advisor。正式导入由主项目在当前 Bug 修复稳定后另行完成。
