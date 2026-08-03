# 外部研究交付验收清单

- [ ] 每个来源都有独立 `research-evidence.v1` 文件。
- [ ] 每个来源保存 URL、作者、平台、发布日期/unknown、访问日和适用版本。
- [ ] 视频内容结论包含精确时间点；无精确时间点的内容标为 unverified。
- [ ] 事实、作者建议和 AI 推断已分开。
- [ ] 多来源冲突被保留，没有擅自删去一方。
- [ ] 不确定内容使用 unknown，没有填 0、空字符串或猜测值。
- [ ] 独立来源保留，转载和重复内容有 deduplicationKey。
- [ ] playbook 的重要动作、阶段、分支和风险都有 evidenceRefs。
- [ ] 角色、装备、羁绊、环境和策略使用包内标准 ID。
- [ ] 无法映射的名字保留在 otherRefs/unknowns，没有发明 ID。
- [ ] playbook 只有声明式 JSON，没有代码或表达式求值。
- [ ] `python tools/validate_all.py --root . --include-output` 退出码为 0。
- [ ] 输出报告列出来源、攻略、覆盖范围、未解决问题和校验结果。
- [ ] 不包含账号、密钥、UID、Cookie、私人路径或无授权长段内容。
