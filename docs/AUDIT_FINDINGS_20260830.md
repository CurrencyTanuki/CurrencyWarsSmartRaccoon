# 对抗式审计发现汇总（2026-08-30，四战线全查）

## 甲、停止失效的真正根因（战线：停止链路）
1. 【阻断】Ctrl+Shift+F12 热键/"全部停止"完全不作用于三星五费：MainViewModel.Stop() 只取消自己的令牌，不碰 grail 令牌、不置急停闸 → 热键停止后 grail 继续点屏（用户实测现象的直接根因）。修：MainWindow 热键处理器叠加 Armed=true + 取消 grail 令牌；OnClosing 同理。
2. 【阻断】在途动作停止后照发：入口闸只在方法第一行，MoveMouse/SendInput 之间无二次检查。修：移动与按下前各补闸。
3. 【阻断】DragAsync 取消后左键卡在按下状态（SendLeftUp 不在 finally）。修：移入 finally。
4. 【应修】急停闸残留：grail 停止后 Armed 恒 true → 普通刷开局/记录的输入全被拒（"正常功能坏了"的另一成分）。修：grail 任务 finally 解除闸。
5. 【应修】录屏 FinishAsync/Dispose 全库零调用 → ffmpeg 永不停、成功录像永不转正。修：finally 中以 CancellationToken.None 收尾。

## 乙、状态与生命周期（战线：决策执行契约）
6. 【严重】GrailRunStateHolder/Executor 跨轮不复位：第 2 局继承第 1 局的旗标/计数 → 假成功或运营全废；_frontDeployCount 跨局累加。修：每轮 Reset()。
7. 【阻断】opening 弹框泵未取消（openingCts 从不 Cancel）→ 与 1-3 循环双泵并发处理同一弹框 → WishesResponded 双计数 → G1③ 提前判死/盲选左跳过。修：opening 返回后 Cancel+await pump；ExecuteWishDialogAsync 加互斥。
8. 【阻断】LatestSnapshot 被商店帧污染：商店帧 Formation=Unknown → 组装出空阵容快照 → 奇迹代偿被误拒。修：非 Preparation 页帧不组装。
9. 【应修】买经验无校验可重复烧金币（人口 Unknown 折叠为 4 → N16 恒触发）。修：Population 用 int? 进决策器；BuyXp 后校验。
10. 【应修】血量缓存时间戳被逐 tick 墙钟重盖 → 陈旧度窗口失效。修：传帧时间戳。

## 丙、数据事实错误（战线：数据核对，对照 json/米游社表）
11. 【硬伤】回路过载诅咒=购买经验价格+1（非刷新+1）→ 买经验真实成本 9-10 金而 N17 按 8 放行 → 空转。行为限制=刷新+1（当前实现恰好对）。修：拆 refreshSurcharge/xpSurcharge 两旗标，N17/N18 分别用。
12. 【硬伤】"四费聘用书"与"五费聘用书"编辑距离=1 误匹配（数据中确有四费聘用书奖励试炼）+ LettersObtained 恒+2（王之财宝等只给 1 本）→ L4 永不过整局白跑。修：聘用书奖励用精确匹配并排除四费；按实际本数计数。
13. 【硬伤】银狼LV.999 costs=[3,4,5] 被 Contains(5) 误判 5 费 → 投影仪复制到非 5 费本体。修：5费=costs.Count==1&&costs[0]==5。
14. 【硬伤】策略偏好缺 333 且实际选择按槽位顺序（非 N10 优先级）；GrailInvestmentStrategyDecider 是死代码未接线。修：偏好集补 333；接入决策器或按优先级排序。
15. 【备忘】奇迹代偿"扣所有金币"未建模（选中后应把金币视为 0）；333 官方名"都是这家伙的错！"（非"全是"）；名单三处硬编码无一致性校验。

## 丁、逐字段拷贝丢字段（战线：选项传递链）
16. 【严重】同型陷阱存活两处：Phase1RunConfiguration.CopyOptions 丢 FastReroll/EnablePresetPriorityDeployment/IgnoreActiveRun（**旧代码既有 bug：用户选的快速刷开局全程失效**）；BuildPreparationCompletionOptions 丢 FastReroll/EnablePresetPriorityDeployment（补位静默退回慢速验证——**1-1/1-2 变慢的成分之一**）。修：补齐字段+反射覆盖测试。
17. 【中】formationReservedNames 死参数：1-2 商店用 1-1 前的旧部署名单判定 → 可能重复购买。修：改传实时部署名集。
18. 【低】listener 首帧信号一次性（Subscribe 不重建）；MarkLettersObtained 死方法；CapturedAt 无读者。

## 修复批次计划
- 第一批（停止+生命周期）：#1 #2 #3 #4 #6 #7 #8
- 第二批（数据事实+经济）：#11 #12 #13 #14 #10
- 第三批（购买/卖人口径）：#9 #16 #17 + 上场非命杯可卖（待用户确认卖法：场上直接拖出售区 or 先换备战席）
