# 字段说明与现有项目映射

## `research-evidence.v1`

| 字段 | 用途 |
|---|---|
| `evidenceSetId` | 一个来源的一组证据，包内唯一。 |
| `source` | 标题、平台、作者、URL、发布日期、访问日、内容类型、版本和复用状态。 |
| `claims[].statement` | 对来源内容的短转述，不保存大段原文。 |
| `assertionType` | `source_fact`、`author_recommendation` 或 `ai_inference`。 |
| `supportStatus` | direct、indirect 或 unverified。 |
| `locator` | 章节、页面、段落或视频时间点。视频内容结论必须用 timestamp。 |
| `subjectRefs` | 标准实体 ID；未映射名字放 `otherRefs`。 |
| `conflicts` | 保留与其他 evidence claim 的冲突和处理状态。 |
| `unknowns` | 无法取得的字段、原因及已尝试来源。 |

## `guide-playbook.v1`

| 字段 | 用途 |
|---|---|
| `signals` | 核心/可选角色、装备、羁绊、环境和策略的识别信号。 |
| `applicability` | 全局适用和禁止条件；每项含 unknown policy。 |
| `phases` | 按位面、节点和节点类型组织推荐状态。 |
| `recommendedState` | 经济、血量、等级、行动值、阵容、装备、羁绊和投资目标。null 代表未取得边界，不代表 0。 |
| `actions` | 可执行建议、优先级、条件、收益、代价、风险、失效条件和降级动作。 |
| `branches` | 声明式条件分支；只引用 action/phase ID，不包含代码。 |
| `alternativeRoutes` | 转型路线、触发条件和代价。 |
| `missingInformationPolicy` | unknown、冲突和高风险决策的统一降级规则。 |
| `evidenceRefs` | 指向 evidence set 和 claim；重要结论必须有引用。 |

## 与现有 Advisor `1.0.0` 的映射

| 现有字段 | 新字段 | 说明 |
|---|---|---|
| `schemaVersion: 1.0.0` | `schemaVersion: guide-playbook.v1` | 由导入适配器转换，不覆盖旧文件。 |
| `guideId/title` | 同名 | 语义保持。 |
| `applicableGameVersion` | `applicableGameVersions[]` | 支持 unknown 与多版本。 |
| `archetypeId/archetypeName/goalIds` | 同名 | 语义保持。 |
| `signals.coreCharacterIds` | 同名 | 必须使用标准角色 ID。 |
| `signals.optionalCharacterIds` | 同名 | 必须使用标准角色 ID。 |
| `signals.synergyIds` | `signals.bondIds` | 旧 `bond:名称` 应映射为 `currency_wars_bond_*`。 |
| `prohibitedConditions` | `applicability.prohibited` | operator/field 继续声明式表达。 |
| `rules[].ruleId` | `actions[].actionId` | 规则动作主体。 |
| `rules[].action` | `actions[].instruction` | 文本建议。 |
| `rules[].conditions[].expectedValues` | `actions[].conditions[].expected` | 新格式支持单值或列表。 |
| `UnknownPolicy` | `unknownPolicy` | `acceptWithPenalty` 对应 `accept_with_penalty`。 |
| `rules[].sources` | `evidenceRefs` | 来源元数据移动到独立 evidence 文件。 |
| 无 | `phases/branches/alternativeRoutes` | 补齐阶段目标和复杂转型。 |
| 无 | `missingInformationPolicy` | 统一处理缺失、冲突和高风险行为。 |

## 标准 ID 使用

`standard-ids/catalog-index.v1.json` 列出所有目录、数量和哈希。当前包含：71 个角色、157 个装备、33 个羁绊、135 个羁绊显示状态、83 个投资环境、334 个投资策略、51 个敌人负面词条。

羁绊显示状态 ID 只用于图标/识别状态，不应替代 playbook 中的逻辑羁绊 ID。无法映射时保留原名和 unknown，不得创造形如 `currency_wars_character_999` 的猜测 ID。
