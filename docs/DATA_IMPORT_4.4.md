# 货币战争 4.4 数据导入记录

## 导入结果

来源目录：

```text
货币战争_当前完整数据报告_4.4/
```

后续人工核对可参考 Bilibili 游戏 Wiki 的
[货币战争资料页](https://wiki.biligame.com/sr/%E8%B4%A7%E5%B8%81%E6%88%98%E4%BA%89)。
该站点存在访问频率限制，本项目不在程序运行时抓取网页，也不实现高频或绕过限制的采集逻辑。
需要更新数据时，应由维护者在遵守站点规则的前提下进行一次性人工整理，再通过导入器生成版本化的离线数据包。

已生成程序使用的数据包：

```text
data/4.4/
  metadata.json
  investment-environments.json
  investment-strategies.json
  enemy-affixes.json
  competitors.json
```

数量：

| 数据集 | 记录数 |
|---|---:|
| 投资环境 | 83 |
| 投资策略 | 334 |
| 敌人负面词条 | 51 |
| 竞争对手 | 20 |

所有数据均通过：

- JSON 解析；
- 报告声明数量核对；
- ID 非空；
- 名称非空；
- ID 唯一性；
- 名称唯一性；
- 投资策略品质检查；
- 敌人词条层级检查。

## 概念区分

### 投资环境

开局三个候选卡片属于投资环境，共 83 个。

本次截图中的三个候选为：

- `investment_environment_078`：量子同频邀请；
- `investment_environment_036`：敌后破坏；
- `investment_environment_055`：白银时代。

### 投资策略

投资策略共 334 个，具有银色、金色、棱彩品质和可出现位面。投资策略要在通过两个奖励关、到达 1-3 后才允许选择，三项均不合适时游戏允许刷新一次；其页面和投资环境页不同，不应与开局投资环境混在同一识别器中。当前阶段不处理“只在第二、第三位面出现”等后期分布，等自动流程稳定到达 1-3 并取得页面样本后再实现品质、位置和投资环境联动等复杂约束。

### 竞争对手

敌人概览页的三张大型卡片属于竞争对手，共 20 个。

本次截图中的三个竞争对手为：

- `competitor_04`：火线动力机甲；
- `competitor_15`：金血记忆体联盟；
- `competitor_13`：增熵能源集团。

### 敌人词条

敌人概览页底部的四项属于敌人负面词条，共 51 个，并分为三个层级。

本次截图中的四个词条为：

- `enemy_affix_t1_05`：随从强化；
- `enemy_affix_t2_02`：榜样激励；
- `enemy_affix_t3_25`：形单影只；
- `enemy_affix_t3_11`：紧急止血。

## 死龙与额外打击

“死龙＋额外打击”跨越两个数据集：

- `competitor_12`：灰手生命科技，其首领图片字段包含“冥魂渡者，死龙残躯，玻吕刻斯”；
- `enemy_affix_t2_16`：额外打击。

因此组合规则必须同时检查竞争对手和敌人词条：

```json
{
  "id": "death_dragon_plus_extra_strike",
  "displayName": "灰手生命科技 + 额外打击",
  "requiredCompetitors": [
    "competitor_12"
  ],
  "requiredModifiers": [
    "enemy_affix_t2_16"
  ]
}
```

该规则已经写入 `config/opening-rules.json`。

## 事实层与策略层

最终报告适合作为事实层，包含名称、效果、品质、位面和敌人构成。以下内容不能直接由报告得出，仍需单独维护：

- 某流派应保留或排除哪些投资环境；
- 某竞争对手对哪些流派不利；
- 某个单独词条对哪些流派不利；
- 多个竞争对手与词条的组合风险；
- 稳定通关、三星五费和核爆玩法的不同优先级。

这些策略规则应引用事实数据的稳定 ID，不能重复保存名称和效果。

## 重新导入

导入工具：

```text
tools/Import-GameData.ps1
```

当报告修订后可以重新运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Import-GameData.ps1
```

导入器会拒绝数量错误、重复 ID、重复名称或缺失必要字段的数据。
