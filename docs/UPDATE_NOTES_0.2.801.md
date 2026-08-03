# 0.2.801 更新说明（2026-08-03）

## 修复：敌人概览页识别失败导致快速刷开局卡死（上次卡在敌人识别页的根因）

### 问题
快速刷开局路径在点击"开始本局"后，会等待敌人概览页（enemy_overview）稳定出现再点确认。
但分类器对敌人概览页**识别不到**（返回 null），导致永远等不到该页面 → 卡死进不去。

### 根因（用系统真实帧回放测试定位）
1. **锚点阈值过高**：enemy_overview 唯一锚点"本场对局首领"阈值 90%。
   真实游戏截图（2048x1152 标准 16:9）匹配分只有 **88.1% / 88.8%**——被 90% 阈值拒绝。
2. **搜索区域错位**：SearchRegion 配置为 (0.58, 0.6, 0.4, 0.24)，但"本场对局首领"
   标签实际在画面中部偏左（x≈0.35），搜索区域完全错过标签位置。

### 修复（config/page-recognition.1920x1080.json）
- enemy_overview 锚点阈值 **90% → 80%**（真实帧 88% 通过；负样本备战帧 31.5% 仍正确排除）
- 去掉错误 SearchRegion，改为**全帧搜索**

### 验证（新增 3 个真实帧回放回归测试）
- `FastRerollRealFrameReplayTests.EnemyOverviewRealFrames_AreClassifiedAsEnemyOverview`：
  2 张真实敌人概览帧 → 识别为 enemy_overview（88.1%/88.8%）✓
- `PreparationRealFrames_AreClassifiedAsPreparation`：3 张真实备战帧 → preparation_1_1（92%）✓
- `InvestmentStrategyRealFrame_IsClassified`：真实投资策略帧 → investment_strategy（96.7%）✓
- 全量回归 **650/650 通过**（647 原有 + 3 新增）

### 交付
- exe: `artifacts\CurrencyWarsSmartRaccoon-0.2.801-win-x64-portable\CurrencyWarsAssistant.App.exe`
- 版本 0.2.801，冒烟通过（进程正常启动）

### 需实机验证点
1. 快速刷开局：主界面 → 开始本局盲点连点 → **敌人概览页应能识别到并自动点确认**（不再卡）
2. 整局流程：敌人概览 → 位面进度 → 投资环境 → 备战 1-1 全流程
