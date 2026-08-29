# 0.2.803 更新说明（2026-08-03）

## 修复：敌人概览页"下一页"被跳过导致流程卡死（严重 bug）

### 问题
快速刷开局在敌人概览（enemy_overview）识别完后，**没有点击"确认/下一页"
(1514,985)**，而是直接盲点连点 (960,720) 3 秒。而 (960,720) 在敌人概览页是
空白（无按钮），点不到"下一页" → 页面无法推进到位面进度 → 流程卡死。

### 根因（代码确认 CurrencyWarsNavigation.cs 523 行分支）
`enemy_overview_next` 快速分支的实现**跳过了 effectiveAction（确认/下一页）的
执行**，只做了 BlindClickAsync(960,720)。与用户方案不一致——用户方案明确：
"识别完敌人信息之后**点击确认**，接着再连点 3 秒"。

### 修复
`enemy_overview_next` 分支恢复正确顺序：
1. **先执行确认点击**（ExecuteActionWithRetryAsync → enemy_overview_next 1514,985）
2. **再盲点连点 (960,720) 3 秒**（位面进度页无按钮，点哪都继续）
3. 等 investment_environment 稳定出现

`select_first_investment` 分支（566 行）核对无此问题——它已先点选投资环境
(effectiveAction.Point) 再复点+确认。

## 验证
- 全量回归 **650/650 通过**
- 冒烟通过（进程正常启动）

## 交付
- exe: `artifacts\CurrencyWarsSmartRaccoon-0.2.803-win-x64-portable\CurrencyWarsAssistant.App.exe`

## 需实机验证点
1. 敌人概览识别完后：日志应出现"点击下一页进入位面进度"→"盲点连点位面进度 3 秒"，
   页面能正常推进到投资环境
2. 整局：主界面 → 开始本局(1690,967) 4s → 敌人概览识别+下一页 → 位面 3s → 投资环境 → 备战 1-1
