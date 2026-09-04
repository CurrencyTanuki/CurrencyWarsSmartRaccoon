# HANDOFF——货币战争智能狸（2026-09-04 21:3x 全量重整版）

> 本文档已于 2026-09-04 21:3x 全量重整：删除 1.2.20~1.2.56 时代的过时历史与旧待办（git 历史可查旧版），只保留当前有效状态。
> 新 AI 必读四件套：**本文件** + **rule.md**（规则与坑账本 1~45，含铁律 1~8）+ **docs/DECISION_LAYER_BLUEPRINT_20260903.md**（决策层总纲 S1~S8/X1~X14/防错清单）+ **docs/GRAIL_COMMAND_SET_v43_final.md**（指令集 v4.3.2）。
> 决策树：docs/DECISION_TREE_1-3三星五费_定稿版_20260829.mmd（行为规范）。

---

## 一、当前状态快照（2026-09-04 21:3x）

- **稳定目录（桌面 CurrencyWarsAssistant-测试台-当前）= 1.2.63，已部署但从未实测**（用户发包禁令中：未经允许不发包不实测）。
- **源码 = 1.2.66 代码批次（学者补位部署 1.2.64 + 补审 P1-2 修复 1.2.65 + 提速批次 1.2.66，见第二节）；2026-09-04 晚用户授权实机测试（用户不在电脑边），发布后 DECIDE 自主验证**。
- **游戏（StarRail）与软件进程当前均已关闭**——实机测试在游戏重新打开并经用户允许后进行。
- 决策层引擎目标模式=单人。识别会话由软件启动时自动开启。
- 计划任务 CWSmartRaccoonCmdTest（RL HIGHEST）= 唯一合法拉起方式；普通 shell 无法强杀该进程（最高权限），停软件只走 exit.txt 空闲消费流程。

## 二、版本与修复历史（2026-09-04 白班，全部已提交 git）

- **1.2.52**：reward_shop 两锚点阈值 0.86→0.72（游戏 UI 改版致 1-3 商店内嵌面板匹配掉到 0.777~0.835，开店死循环根因）。**已实测验证**：1-3 内嵌面板开店一次成功。
- **1.2.53**：证据缓存 RetainOnly 对跨会话来源跳过清理而非抛异常（修每 5 分钟 UnhandledUiException，全天 20+ 次）。**已实测验证**：异常计数归零。
- **1.2.54/55**：结算页导航步骤（challenge_failed/challenge_success）+ clickLoop 动作类型（原地连点）+ F 序列两轮迭代。
- **1.2.56**：弃局兜底页面感知分流第一版（AbandonCurrentRunAsync）+「前台区域无角色，无法出战」弹窗入识别表（uncompleted_battle_prompt，阈值 0.80，实拍帧置信 0.987）+ 确认步骤。
- **1.2.57**：补 RecoverAsync 第二处 fallback 的页面分流（1.2.56 漏改）。
- **1.2.58**：①CloseShopAsync 收店失败 1.5 秒动画容忍重读+点击失败日志；②S5 循环 M5 失败重试一轮再判定（封「收店失败→I10 门禁死锁→弃局」损失链）；③M1 前置门 EnsureFrontHasUnitAsync（前台≥1 否则判 Dead）；④备战席卖人三重保护（bond/纯5费/[星徽]，此前漏保护=误卖最高危）；⑤卖出后 I10 复核+逐卖重读（防错③④/坑38/X5 落地）；⑥Unknown 页封兜底盲点（两处 fallback）；⑦存量时序测试对齐提速节奏。
- **1.2.59**：①StartingBattle 未知观察先确认「无法出战提示」（ConfirmUncompletedBattlePromptIfPresentAsync+确认点 960,699）；②迁移白名单对齐「人数不足确认」；③M8 守卫竞态修复（ProbeLatestPageId 10 秒新鲜窗复核，preparation 族放行——15:15 命运圣杯邀请局 63ms 被误弃根因）；④弃局 Esc 前等 2.5 秒+重试 1→2 次（首试失败率 22%）；⑤导航层加 wish_trial_selection 步骤（确认钮 1920 系 1499,641）。
- **1.2.60**：部署拖拽重试 5→2（rule 16：Archer@1 号位 15 连败/银狼@5 号位 6 连败证明 5 连发无鉴别力）。
- **1.2.61**：M1 前置门补部署动画等待（3 秒+双读；19:17 局部署成功后立即 I10 撞动画误判「前台无人」弃好局——蓝图 X13 落地）。**已实测验证**：067 局部署远坂凛后正确放行出战。
- **1.2.62**：部署/出售拖拽加 MouseButtonHoldDelay=250ms（同参数拖拽一成一败，诊断对比实证输入一致；星徽装配 250ms 为实测可靠参数）。
- **1.2.63**：结算推进点击后 350ms 单帧主页复查（主页出现立即停——修复「回主界面后仍连点页面中部」实拍异常）；快照失败弃局前先点收店开关解除面板死锁（**待修：需按补审 P1-1 加页面分流**）。
- **1.2.64（源码已提交未发布）**：学者补位部署——bond 候选部署完毕后前台有空槽且备战席有银河学者（凑 2 羁绊）→A1 补位（艾丝妲滞留备战席案 19:47）。**同提交 6d6b30f 还包含补审 P1-1 修复**（收店兜底前加 pageBeforeRescue 页面分流），当时未入档——09-04 晚班接手核对发现（git blame 证实），已补档（漏记处置流程）。
- **1.2.65（已提交）**：①补审 P1-2 修复——"uncompleted_battle_prompt" 加入 AutomationPageIds.Ids（GamePageClassifier.cs，激活死分支：确认器此前永远不触发，P-02 修复整体不生效；已核 JSON 识别表 578 行有该页定义）；②清理 1.2.64 编辑残留（GrailDecisionEngine.cs 连续两个相同 `if (snapshot is null)` 死代码块）；③handoff 补档 P1-1 漏记。
- **1.2.66（提速批次，2026-09-04 晚）**：用户令审计"冗余操作+操作间隔长"。决策层编排六项重构+两处修复，全部在 GrailDecisionEngine.cs：①EnsureWishAnsweredAsync 参数化（例行单查，部署后 4×3s；买非昔涟成员且未部署时外层补轮询——原无差别 6×3s 轮询=每轮 ~20 秒纯等待，S5 每轮都跑）；②DeployBondMembersAsync 复用调用方快照（消除背靠背双 I10）+返回是否部署过；③**学者补位可达性修复**（1.2.64 功能缺陷：pass 循环无候选时 return 致学者补位段基本不可达，改 break+snapshotFresh 新鲜度标记——交接单实机验证项⑤在实机前已救回）；④EnsureFrontHasUnitAsync 条件等待（justActed 才先等 3s，坑43 双读判据保留）；⑤快照退避 [1,1,2,2,3,5,5,5]s（原 8×5s）；⑥M7 后 2s→500ms；⑦引擎入口判定 I10→I1 页面前缀（非备战页 I10 8 次全败=每局边界白等 ~19s，独立审计 TOP1）。子代理对抗审查 7 项全 PASS+增量复查；构建 0/0、Grail 短套件 111/111。**用户已授权实测（我不在电脑边场景），发布后 DECIDE 实机验证**。
- **1.2.66 独立效率审计遗留（下批清单，按每局浪费排序）**：①商店读固定 500ms 前置在刷新循环内重复支付（Shop.cs:543，加"刚刷新免前置"参数，5-20s/局）；②AfterActionDelay 默认 250ms/点击全局开销（InputModels.cs:14，10-17s/局，风险高逐点实测）；③A9 后固定 5 秒×4+局间 4 秒改 I1 轮询早退（4-10s/局）；④备战席稳定读 650ms 前置（3-5s/局，观察）；⑤逐卖固定 1 秒改"识别帧晚于卖出时刻"有界等待（2-5s/局，坑38 复核保留）；⑥策略识别 850ms 前置调参（1-2s/局）；⑦S7 判定用部署前快照致收工晚一轮（低优先）。

## 三、两份独立分析结论（已交叉印证）+ 修复对照

- **架构审查**（引擎 vs 决策树/铁律）：15 处不一致+8 处冲突。**已修**：备战席卖人三重保护、卖出 I10 复核、A4/Unknown 相关。**未修清单见第四节 P2**。
- **独立日志逐局分析**（8333 事件/129 局）：23 问题 P-01~P-23+14 项正确防御甄别。16 命中局全部被弃/17 战 17 胜/Esc 首试失败率 22%。
- **修复对照**：P-01(部分)/P-02/P-03(前置门)/P-04/P-05/P-08/P-11/P-12/P-13 已修。P-06/P-07/P-09/P-10/P-14/P-15/P-16/P-20/P-21/P-22 待后续批次。

## 四、最新待办（按优先级）

### 需实机（游戏打开后第一批）
1. **P-03**：challenge_health_depleted 页面误分类（1-3 胜利结算页被识别为败北页→2 继承局胜局被弃）——需实机抓 1-3 胜利结算帧，裁「1-3 战斗」副标题或金币总览判别模板。
2. **备战页弃局菜单子流程**：备战页 Esc 打开系统菜单（识别表无此页），「放弃对局」在菜单内——需实机抓菜单帧→裁模板→建 system_menu 页面 ID→定标菜单「放弃对局」点位→弃局恢复加菜单分支（当前备战页 Esc 弃局结构上必败，1.2.61 的等待只是缓解）。
3. **P-06**：备战席 5 号位拖拽标定（银狼 LV.999 与历史黑塔同槽位连败，input-diagnostics.jsonl 有数据）。
4. **补审 P1-1/P1-2/P2-1/P3-4 修复的实机验证**（见第五节，代码修复已完成待实机确认）。

### 代码层（离线可做）
5. **P-07**：1-3 商店刷新加「货架内容变化校验+金币≥2 门」（刷新失效后仍连点 5-7 次）。
6. **P-09**：继承局（弃局失败遗留）先对账投资环境再决定运营/弃局。
7. **P-10**：弃局失败后进清场模式循环，禁止立即开新导航段。
8. **P-14/P-16/P-20/P-21/P-22**：可观测性（策略选择落日志/购买决策留痕/RecoveryFailed 双写去重/Matched 文案修正/长等待心跳）。
9. **规则 13a 策略弃局例外**（全员模式三条件）——当前单人目标不触发，全员批次实现。
10. **R67 067 赠体部署排除**（部署候选排除赠体；日志实况：Archer 尝试部署浪费且搅乱阵容）。

### 发包禁令期间禁止事项
- 禁止覆盖稳定目录/计划任务拉起/写 DECIDE 指令/启动游戏实测——全部需用户明确允许。

## 五、补审遗留（1.2.64 复查子代理结论；09-04 晚班接手逐项核对代码后更新现状）

1. **P1-1 ✅ 已随 1.2.64 修复**（GrailDecisionEngine.cs 快照失败兜底：pageBeforeRescue 报 reward_shop 才点收店开关 1620,975）——提交 6d6b30f 时未入档，接手核对发现后补档。
2. **P1-2（致命死分支）✅ 已随 1.2.65 修复**：ConfirmUncompletedBattlePromptIfPresentAsync 判定用 uncompleted_battle_prompt，但控制器 pageClassifier 是 IAutomationPageClassifier 子集（AutomationPageIds.Ids，GamePageClassifier.cs:38-57）不含该 ID→确认器永远不触发，P-02 修复整体不生效。修法=ID 加入 Ids（已核：JSON 识别表 578 行有该页定义，加 ID 即生效）。**待实机验证**。
3. **P2-1（核实仍未修，下批）**：M8 竞态放行精确匹配 preparation_generic（GrailMacroCommands.cs:356-359 string.Equals），识别流可能产出 preparation_1_1 族→放行形同虚设。修法=StartsWith("preparation_") 族匹配。
4. **P3 备案**：RecoverAsync fallback 缺 isPrompt 特判；双确认器串行空转 ~11s；确认钮坐标三处硬编码待收敛；wish_trial 点位 (1499,641) 78% 分位需实机核验；expected 列表 preparation_1_x 永假死条目待清理。

## 六、监督与纪律（用户令，最高优先）

1. **漏记处置流程**（rule 铁律 2 强化）：发现任何改动未及时记录→立刻停止手中一切工作→先补档→补档完成**停下等用户指示**，严禁自作主张继续。
2. **发包禁令**：未经用户允许禁止发包（覆盖稳定目录/计划任务拉起）与实测。
3. **Unknown 零容忍**（rule 坑 42）：任何「识别 Unknown→无法继续→弃局」=软件问题，禁止放行为正常；监督中出现即最高级信号。
4. **每 2 分钟监督**：高频读日志（result.txt+最新 test-session jsonl+Unknown/Exception 计数），逐行深度检查；禁止长 sleep 轮询弧；禁止只看单一日志源（result.txt 与事件日志互补）。
5. **审查纪律**：每批代码发布前子代理对抗审查硬性一轮，FAIL 全部处置；审查未完成不发布。
6. **新增兜底点击**必须先按页面身份分流（备战页禁点出战区；Unknown 禁点；先回答「该坐标在当前页面是什么」）。
7. **测试局弃局**：测试期「万能解」长期授权有效；正式交付前 R3 自动重开须接用户闸门。

## 七、机制与文件速查

- 三层指令：I1~I10 识别 / A1~A15 操作 / M1~M8 宏 + STATUS/START/STOP/DECIDE/GOAL；指令文件=稳定目录 指令测试-command.txt（写=立即发出），回执=指令测试-result.txt。
- 事件日志：%LOCALAPPDATA%\CurrencyWarsSmartRaccoon\logs	est-session-*.jsonl（UTF8）；黑匣子 logs\grail-flight-*.jsonl；拖拽诊断 logs\input-diagnostics.jsonl；自动截图 runs\cmdtest-*\screenshots\（文件名 UTC=本地-8h）。
- 关键代码：决策层引擎 src/CurrencyWarsAssistant.Tasks/GrailDecisionEngine.cs；导航流执行器 CurrencyWarsNavigation.cs（消费 config/navigation-flow.json）；弃局恢复 CurrencyWarsRejectedOpeningRecovery.cs；商店 RewardStageAutomation.Shop.cs；页面识别表 config/page-recognition.1920x1080.json；拖拽输入 Win32InputController.cs（InputKey 枚举含 F=0x46，拖拽 MouseButtonHoldDelay 已 250ms）。
- 官方数据 data/4.4/*.json（名单零手写运行时解析）；5 费判定唯一口径=IsPureFiveCostCharacter。
- 关键机制：1-3 商店面板开=reward_shop（1.2.59 阈值 0.72 后）、面板关=preparation_generic；同一坐标不同页面语义不同（(960,899) 结算页=下一页/备战页=出战区），任何兜底点击必须先验页面身份。
- 测试：Grail 短套件 --filter "FullyQualifiedName~Grail"（139 项，全量门禁正常优先级跑）。
