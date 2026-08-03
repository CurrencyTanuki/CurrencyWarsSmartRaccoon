# 独立任务：收集并结构化《货币战争》攻略

## 任务身份与目标

你在一个完全独立的上下文中负责《崩坏：星穹铁道》“货币战争”攻略研究。当前压缩包是唯一必需材料。目标是使用网络搜索收集多来源攻略，先保存可追溯证据，再生成程序可读取、可校验的声明式攻略 JSON。

不要修改任何主程序代码。只向 `output-template/` 写入研究成果和报告。

## 开始前

依次阅读：

1. `README_START_HERE.md`
2. `docs/PROJECT_BACKGROUND_AND_GOALS.md`
3. `docs/GAME_GLOSSARY.md`
4. `docs/FIELD_REFERENCE_AND_MAPPING.md`
5. `docs/SOURCE_AND_QUALITY_RULES.md`
6. 两份 `schemas/*.schema.json`
7. `examples/valid/`
8. `standard-ids/catalog-index.v1.json` 和需要引用的分类目录

先运行基线校验：

```powershell
python tools/validate_all.py --root .
```

基线失败时停止研究，报告具体错误；不要修改 Schema、标准 ID 或示例来绕过失败。

## 授权边界

允许：公开网络搜索、打开文字/视频页面、记录短转述和时间点、在 `output-template/` 新建或修改 JSON/Markdown/TXT。

禁止：登录或发布账号内容、绕过付费/访问限制、运行来源提供的程序或脚本、复制无授权正文/字幕/图片/视频、写入主项目、在 JSON 中加入可执行代码、编造缺失信息、泄露 Cookie/令牌/UID/私人路径。

## 研究优先级

优先覆盖具有完整运营过程的攻略，而不是只收集阵容名称：

1. 阵容/流派如何从第一位面逐步构建；
2. 各节点或阶段的阵容、装备、经济、血量和投资状态；
3. 刷新、升人口、购买、保留、出售和换阵的条件；
4. 核心未出现、经济不足、血量危险或版本变化时的分支、替代路线和失败处理；
5. 建议优先级、风险和停止条件。

同一重要结论尽量寻找两个独立来源。官方机制、作者经验和 AI 推断必须分开。

## 固定工作流

### 阶段 A：来源发现与筛选

使用多种搜索词覆盖官方资料、文章、视频、社区作者和公开攻略。先建立候选清单，按以下标准排序：版本明确、发布时间明确、作者可识别、包含实际流程、可以定位到章节/视频时间点、不是纯转载。

去重只合并同一 URL、同一视频 ID 或明确转载链；独立作者和独立实测必须保留。

### 阶段 B：先写原始证据

每个独立来源生成一个：

`output-template/evidence/<source-id>.research-evidence.v1.json`

要求：

- 保存网址、平台、作者、发布日期、访问日期、内容类型和适用版本；
- 文章结论记录章节/段落定位；视频内容结论记录对应时间点；
- 每条 claim 标记 `source_fact`、`author_recommendation` 或 `ai_inference`；
- 标记 direct、indirect 或 unverified 和 0..1 置信度；
- 使用标准 ID；无法映射的名字放 `otherRefs` 和 `unknowns`；
- 多来源冲突双方都保留并互相引用；不得擅自选择一方为事实；
- 不确定信息写 unknown，不使用 0、空字符串或猜测值代替。

每完成一批 evidence，运行：

```powershell
python tools/validate_all.py --root . --include-output
```

修复全部格式和引用错误后再进入下一阶段。

### 阶段 C：再生成攻略脚本

从已通过校验的 evidence 生成：

`output-template/playbooks/<guide-id>.guide-playbook.v1.json`

要求：

- 重要适用条件、阶段目标、动作、分支、替代路线和风险都有 `evidenceRefs`；
- 阵容、装备、羁绊、投资环境和投资策略只使用 `standard-ids` 中存在的 ID；
- 复杂条件使用声明式 `conditions`、`branches` 和 action 引用，不写代码、脚本、正则执行器或表达式求值；
- 写清经济、血量和阵容要求；来源没有给出数值时保留 null/unknown，不能估算后伪装成事实；
- 对信息不足、低置信度和冲突设置明确降级，任何高风险建议都不得自动执行；
- 不同版本或体验服/正式服资料不能无条件合并。

### 阶段 D：交叉核对与最终验证

对每套 playbook 反向追踪全部 evidence refs，确认 URL、定位、版本和结论一致。再次检查独立来源是否被错误去重、作者判断是否被写成官方事实、未知数据是否被补造。

运行同一完整验证集：

```powershell
python tools/validate_all.py --root . --include-output
```

验证失败时只修复 `output-template/` 中成果；最多连续修复三轮。三轮后仍失败则停止，保留文件，报告每个未解决错误，不修改 Schema 或标准 ID。

## 完成与停止条件

只有同时满足以下条件才算完成：

- 每个保留来源都有合法 evidence JSON；
- 每个 playbook 的重要结论可追溯到 evidence claim 和来源定位；
- 事实、建议、推断、冲突和 unknown 均未混淆；
- 标准 ID 与目录一致；
- 最终校验退出码为 0；
- 以下三个报告已完成。

如果网络无法访问、来源被删除、视频无法播放或没有足够证据，不要无限重试。每个来源最多换两种安全访问方式；仍失败则记录 blocked/unknown，继续其他来源。

## 最终交付

除 evidence 和 playbook JSON 外，填写：

1. `output-template/reports/SOURCE_INDEX.md`：所有来源、链接、作者、日期、版本、证据文件和去重关系；
2. `output-template/reports/COVERAGE_AND_OPEN_QUESTIONS.md`：覆盖的流派/阶段/字段、来源冲突、unknown、未解决问题；
3. `output-template/reports/VALIDATION_RESULT.txt`：最终命令、退出码和完整摘要。

最终回复必须列出：

- 证据文件数量和来源数量；
- 攻略 JSON 数量及覆盖的流派；
- 来源清单位置；
- 未解决冲突和 unknown；
- 最终校验命令、退出码和结果；
- 由于访问、版本或证据不足而未完成的内容。

## 提示设计依据

本提示词按照 OpenAI 最新模型提示建议组织：目标和成功标准明确；约束只陈述一次；授权边界、工具阶段、重试/停止条件和输出格式清晰；最终使用同一验证集复验。参考：[OpenAI latest-model prompting best practices](https://developers.openai.com/api/docs/guides/latest-model#prompting-best-practices)。
