# 外部 AI 输出目录

- `evidence/`：每个来源一个 `*.research-evidence.v1.json`。
- `playbooks/`：每套攻略一个 `*.guide-playbook.v1.json`。
- `reports/SOURCE_INDEX.md`：来源清单、覆盖范围和去重说明。
- `reports/COVERAGE_AND_OPEN_QUESTIONS.md`：已覆盖流派/阶段、冲突、unknown 和下一步。
- `reports/VALIDATION_RESULT.txt`：完整校验命令、退出码和输出。

不要覆盖 `examples`。交付前在包根目录运行 `python tools/validate_all.py --root . --include-output`。
