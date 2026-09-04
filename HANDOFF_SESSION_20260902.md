# HANDOFF——货币战争智能狸（2026-09-04 21:3x 全量重整版）

> 本文档已于 2026-09-04 21:3x 全量重整：删除 1.2.20~1.2.56 时代的过时历史与旧待办（git 历史可查旧版），只保留当前有效状态。
> 新 AI 必读四件套：**本文件** + **rule.md**（规则与坑账本 1~45，含铁律 1~8）+ **docs/DECISION_LAYER_BLUEPRINT_20260903.md**（决策层总纲 S1~S8/X1~X14/防错清单）+ **docs/GRAIL_COMMAND_SET_v43_final.md**（指令集 v4.3.2）。
> 决策树：docs/DECISION_TREE_1-3三星五费_定稿版_20260829.mmd（行为规范）。

---

## 一、当前状态快照（2026-09-04 21:3x）

- **稳定目录（桌面 CurrencyWarsAssistant-测试台-当前）= 1.2.63，已部署但从未实测**（用户发包禁令中：未经允许不发包不实测）。
- **稳定目录（桌面 CurrencyWarsAssistant-测试台-当前）= 1.2.67 已发布（22:22，含 1.2.66 提速+1.2.67 机制纠正）**；1.2.66 曾于 21:42~21:59 实机跑过 7-8 轮 M8 重刷（入口 I1 判定/学者补位/提速路径正常，未命中环境未进正式运营段）。**当前状态（22:4x）：游戏已由用户手动关闭，软件 1.2.67 运行待命；实机测试等用户明确指令，不搞自动检测/自动启动**。
- **源码 = 1.2.67（提速批次 1.2.66 + 机制纠正 1.2.67，见第二节）；用户已授权实机测试（不在电脑边），发布后 DECIDE 自主验证**。
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
- **1.2.66（提速批次，2026-09-04 晚）**：用户令审计"冗余操作+操作间隔长"。决策层编排六项重构+两处修复，全部在 GrailDecisionEngine.cs：①EnsureWishAnsweredAsync 参数化（例行单查，部署后 4×3s；买非昔涟成员且未部署时外层补轮询——原无差别 6×3s 轮询=每轮 ~20 秒纯等待，S5 每轮都跑）；②DeployBondMembersAsync 复用调用方快照（消除背靠背双 I10）+返回是否部署过；③**学者补位可达性修复**（1.2.64 功能缺陷：pass 循环无候选时 return 致学者补位段基本不可达，改 break+snapshotFresh 新鲜度标记——交接单实机验证项⑤在实机前已救回）；④EnsureFrontHasUnitAsync 条件等待（justActed 才先等 3s，坑43 双读判据保留）；⑤快照退避 [1,1,2,2,3,5,5,5]s（原 8×5s）；⑥M7 后 2s→500ms；⑦引擎入口判定 I10→I1 页面前缀（非备战页 I10 8 次全败=每局边界白等 ~19s，独立审计 TOP1）+**IsStale 新鲜度检查**（增量复查 FAIL 项修复：识别流冻结时 I1 报旧页，陈旧读数=无现状绝不据此发 A9）。子代理对抗审查 7 项全 PASS+增量复查（项3 FAIL 已处置）；构建 0/0、Grail 短套件 111/111。**用户已授权实测（我不在电脑边场景），发布后 DECIDE 实机验证**。
- **1.2.67（机制纠正批次，2026-09-04 晚，实机监督中用户两条质询触发）**：①**删除环境页"免费刷新"固定假设**（用户机制纠正：免费额度来自部分投资策略、不固定存在，不能写死进逻辑；节点推进商店自动出新=自动刷新，与免费刷新无关）——CurrencyWarsNavigation.cs 环境页未命中不再点刷新按钮（原每轮点一次(676,984)），直接按未命中弃局重开（重刷才是免费的）；InvestmentRefreshPoint 坐标常量一并清理；②**"轮岗被选中"事件定性**（坑 47）：日志"选择投资环境：轮岗"=弃局恢复面对强制模态环境页的必要过路动作（不选不能进局，进局才能弃局）——不是"刷不到就接受开局"；决策层轮数上限本为 null（无限刷，用户令核实），M8 的 30 分钟 MaximumRuntime 是卡死看门狗（超时后引擎重发 M8 继续刷，实质无限）。构建 0/0、Grail 短套件 111/111；rule.md 新增四.19 机制口径+坑 46/47。
- **1.2.68（深度优化批次，2026-09-04 深夜，用户令"更细致的优化"，离线未发布）**：上轮独立审计 TOP 清单逐项落实+新审计，五组改动：①刷新动画等待 900→400ms（ShopRefresh.cs，与商店读门禁前置合并，readFailures<2 重试兜底；省 ~15-30s/局）；②策略识别前置 850→500ms（Battle.cs，对齐 1.2.29 口径）；③A9 弃局后固定 4-5 秒×4 处→SettleAfterAbandonAsync 有界 I1 轮询（命中主页即早退，IsStale 契约遵守；省 3-4s/次）+M8 重试间隔 5→2s；④**S7 快照新鲜度**：DeployBondMembersAsync 返回（部署, 最新快照），S5 循环快照覆盖——修"收工判定晚一轮多跑一整轮 M5"（命中时省 6-15s）；学者部署后重读（低频）；⑤已开店热路径商店读入口 500→250ms（Shop.cs 加 initialSettleDelay 可选参数，其他调用点不变）。**未动**：AfterActionDelay 全局 250ms（无实测数据，逐点下调风险高，待实机批次）、逐卖 1 秒等待（坑38 高危区）、备战席拖拽 650ms（操作语义非空闲等待）。识别管线审计结论：节流已成熟（2s 全量/1.5s 增量/黑屏快速判定/静止只补失败字段），CPU 非主要矛盾；参考业界手法（ROI/金字塔/灰度/并行）本项目已有等价实现。构建 0/0、Grail 短套件 111/111。
- **1.2.69（防错批次，2026-09-04 深夜，离线未发布）**：①**补审 P2-1 修复**——M8 竞态放行改 preparation_ 族匹配
- **1.2.70（可观测性批次，2026-09-05 凌晨，离线未发布）**：架构审查 P-14/16/20/21/22 五项全落地（审计子代理定位+方案，四批实施）：①**P-16 购买决策留痕**——controller 加 internal PublishGrailTelemetry 转发口（executor 经已有 rewardStage 引用发布，零构造改动），M5 圣杯循环每条指令发一条 `GrailShopLoopSummary`（买/刷/轮数/终态金/结束原因枚举 9 种/跳过已拥有/金币不足跳过/上场失败），非常规路径即时事件 GrailShopBought/PurchaseUncertain/PurchaseNotConfirmed/ShelfStale/DeploySkipped；GrailShopPassResult 增 SkippedOwnedNames/SkippedUnaffordableNames（nullable 默认）；②**P-14 策略选择终态**——`InvestmentStrategySelectionDecided` 事件（选中 ID+名+槽位+来源五类）+引擎 Describe 补 RewardStageAutomationResult 分支（M7 回执不再空尾）；③**P-20 双写去重**——协调器 Result 加 writeEventLog 参数（RecoveryFailed 终态不再重复落盘，组件层 Error 行保留）+Battle 超时恢复失败改指针式文案；④**P-21 文案诚实化**——M7 成功文案去"前两层奖励关"残留+引擎 M8 回执区分"命中/未成功"；⑤**P-22 长等待心跳**——SendAsync 在途心跳（30s 一条 WaitHeartbeat，AwaitWithHeartbeatAsync 包装，超时仍由 WaitAsync 裁决）+战斗等待循环心跳+1-3 无快照等待心跳（15s，经 executor.PublishTelemetry 转发；帧停流分支不发）。**交叉对抗复核（第三子代理）无 P1/P2**，9 条 P3 已处置：F1 streak 在 M8 到达时清零/F2 死分支删除/F5 注释口径 650ms/F7 注释互斥澄清（族匹配=防御性收紧）/F8 退避窗口注释如实（24s=原 60%）/F9 rule.md 四.10/11 拆行；F4 领域前提备案、F6 excludedOptionIds 死机制留待清理。构建 0/0、Grail 短套件 111/111。①**补审 P2-1 修复**——M8 竞态放行改 preparation_ 族匹配（GrailMacroCommands.cs，精确匹配 generic 时族内 ID 会让放行形同虚设误弃好局）；②**P-07 刷新失效防护**——M5 圣杯循环加货架签名校验（连续 2 轮无购买且全名单签名不变→停止本店循环，防刷新失效白烧金币）；③**P-10 清场模式**——引擎入口 A9 连续失败≥2 次时退避 20-30s 重试且绝不发 M8（修弃局失败后"A9 失败→M8 被守卫拒→再 A9"活锁）；④**P-09 继承局对账留痕**——入口弃局决策不变（用户拍板"未知节点不操作"），但先 I10 对账局面落日志（羁绊/金/血/盘面）；⑤AutomationPageIds 清理 preparation_1_1/1_2 永假条目（JSON 从无定义，Create 永远空选）。**新发现疑点（下批排查）**：CurrencyWarsNavigation.cs:62 PreparationPageId 默认值="preparation_1_1"——该 ID 识别流永不产出，不传此参的调用方门禁可能永假。构建 0/0、Grail 短套件 111/111。**审查 FAIL 一项已处置（D1）**：AwaitWithHeartbeatAsync 的 beatInterval=remaining 可为负——Task.Delay(负值) 在 net8.0 同步抛 ArgumentOutOfRangeException，逃过 SendAsync 的 TimeoutException/InvalidOperationException 两个 catch，"卡死跑满超时"的压线拍场景会把可自愈超时变成决策层整循环死亡（审查者 net8.0 实证）——已按审查方案 clamp（remaining≤0 直接 await awaited 返回超时异常）；心跳 emit 进 UI 的注释口径同步修正。审查 P3 备案：SkippedOwnedNames 实际混装"非目标+已拥有"口径（下批拆分）、4 种 Warning 级圣杯遥测会进 UI 日志（量小可接受）、GrailRunLoop 吞异常弃局路径无痕（既有遗留）。
- **1.2.71（运行时行为修复批次，2026-09-05 凌晨，用户授权实机测试后首批）**：四线审计（交付就绪/运行时风险/日志逐操作复盘/行为缺陷）后的修复：①**P1-1 清场模式活锁修复**——入口弃局前先应答在屏祈愿框（A9 在备战页会被模态确定性挡死）；streak≥5 高声停机等人工（绝不无限空转）；A9 名义成功后复核页面真离开才清零；②**P1-2 一次异常即死修复**——SendAsync 加兜底 catch（非取消异常降级失败事实，引擎存活）；A9 通路（GrailOperationCommands）照 M1 先例包装组件异常；③**P2-1 半回归修复**——前置门 justActed 纳入"执行器内部上场"（M5 买到成员的上场动画同坑43，21:57 命中好局被弃实例正对此）；④**P2-2 僵尸指令修复**——orphanCts 先 Cancel 再 Dispose（DECIDE 停止后在途 M8 不再继续点游戏）；⑤**M7 配额 60s→8min**（六轮环合法 ≈6min，原配额静默腰斩策略选择）；⑥**DismissUnknown 去 (960,720) 中心盲点+IsStale 门禁**（战斗过场误点消除）；⑦**P-10 补全**——M8 失败/耗尽分支的 A9 纳入清场计数（RecordAbandonOutcomeAndCheckShutdownAsync，streak≥5 停机）；⑧**R1 启动期错误可见化**——App.xaml.cs 启动阶段致命错误 MessageBox+退出释放互斥量（修"僵尸闪屏+怎么点都没反应"，2026-08-08 实锤同链）；⑨**R2 过期部署脚本归档** artifacts/deploy-scripts-archived（防误跑回退 1.2.62）。构建 0/0、Grail 短套件 111/111。
- **1.2.73（实测根因修复，2026-09-05 凌晨，实机监督发现）**：02:18 命中英雄登场的好局被弃实锤+根因闭合——M8 用导航器实时分类确认到达 1-1、M2 实时截图开矿成功，但识别流 LatestAnalysis 滞后（实测进 1-1 后 19s+ 无新分析帧）→ I10 门禁连续 8 次全败 → Interrupted 弃掉好局。修复=引擎加 SnapshotWithRetrySlowTailAsync（识别流追帧长尾：8 次退避全败后再以 5s×12=60s 慢速重试，管线恢复即自愈），S2 快照点接入。构建 0/0、Grail 短套件 111/111。
- **1.2.74（实测根因修复补全，2026-09-05 凌晨，实机监督发现）**：02:40 第 13 轮命中局的运营链复盘——M1 出战 OK（P2-1 前置门修复实机生效✓）→进 1-2→M5 买到黑塔部署成功→**I10 又连续 8 次全败→Interrupted 弃局**：1.2.73 追帧长尾只接了 S2，S3/S4 同款覆盖缺口。修复=S3/S4 快照点全部接入 SnapshotWithRetrySlowTailAsync。构建 0/0、Grail 短套件 111/111。另：verify_deployment.ps1 验收脚本两处修复（STATUS 回执匹配逻辑改逐行+计划任务拉起替代 Start-Process——app.manifest=requireAdministrator 无人值守 UAC 必被取消；验收通过后实例保持待命不写 STOP/exit）。
- **1.2.75（P1-4 部署竞态修复，2026-09-05 凌晨，实机监督发现）**：03:37 命中局远坂凛"买而未上"根因闭合——买后卡飞向备战席的动画期，DeployBoughtToRealEmptySlotAsync 首读"未见卡"即放弃（且裸 M5 路径无留痕）；叠加部署段快照全败放弃 → 前置门判 Dead → 带着备战席上的远坂凛弃掉好局。修复=①DeployBought 首读未见卡时等 2.5s 重读一次仍未见才放弃（X13 精神）并补 GrailShopDeploySkipped 留痕（裸 M5 路径首次有留痕）；②DeployBondMembersAsync 首读全败（仅 S2 现读场景）走追帧长尾后重试部署。构建 0/0、Grail 短套件 111/111。
- **1.2.76（前置门滞后误判修复，2026-09-05 凌晨，实机监督发现）**：04:08 第 10 轮命中局复盘——追帧长尾+S3 生效（I10×8 全败→长尾第 2 次成功→M5 买到艾丝妲→学者补位黑塔 A1 OK），但前置门两读 I10 前台空（识别滞后）→误判 Dead 弃掉已部署好局。修复=EnsureFrontHasUnitAsync 在 justActed 场景判 Dead 前加 60s 追帧终判（管线恢复前台可见即放行；仍空才如实 Dead）。构建 0/0、Grail 短套件 111/111。
- **1.2.77（CPU 优化首批，2026-09-05 凌晨）**：页面分类器两级探针（coarse-to-fine，GamePageClassifier.Classify 重写）——第一级只探每页"必要代表锚点集"（Count-Matches+1 个最高阈值锚点：页达标则代表集必有达标者），排除不可能页；第二级仅对候选页补探全锚点。判定语义精确等价，锚点匹配次数显著下降（CPU 审计实测：fast 分类 254ms/次常驻 1 核、3.2 核常载实机数据坐实优化必要性）。Grail 111+分类器专项 21 测试全过。
- **1.2.78（CPU 优化第二批，2026-09-05 凌晨）**：enemy_overview_leader_label 锚点加 searchRegion（x0.50/y0.45/w0.46/h0.35）——原默认全帧搜索占 fast 分类 63% 成本（单锚点 90.8 G-MAC，模板 515×85 全帧扫描）。证据=两张 PageReplay 参考图（1920/2559 变体）标签分别位于 (0.79,0.70)/(0.625,0.55)，搜索区覆盖两布局+余量（面积=全帧 16%，成本降 ~6 倍）。离线验证闭环：PageReplay 夹具回放 21 项全过（敌概页识别在缩小搜索区后仍成功）。构建 0/0、Grail 111。
- **1.2.79（前置门放行策略，2026-09-05 凌晨，实机两连案例驱动）**：1.2.76 追帧终判后仍前台空的场景（04:08/04:56 两命中局黑塔 A1 OK 后前置门 4 次 I10 跨 18s 前台空）**改为放行出战**——不对称风险决策：误放行=出战弹窗→M1 失败→A9 自愈（1 分钟可逆）；误弃局=好局不可逆。A1 像素差验证（目标槽画面变化 65.8% 才回 OK）是强证据，I10 滞后读数让位。非 justActed 场景维持 Dead。构建 0/0、Grail 111。
- **1.2.80（夜间值守终局，2026-09-05 05:20，游戏掉线不可抗力终止实测）**：①**发布 1.2.72-79 八批**（command-test 单实例旁路/前置门放行策略/部署竞态/追帧长尾/识别流滞后/清场活锁/一次异常即死/M7 配额/DismissingUnknown 盲点/P-10 补全/R1 启动可见化/R2 脚本归档/CPU 两级探针/enemy_overview 搜索区），全部经审查+构建+短套件流水线；②**实机验证成果**：STATUS 端到端验收通过（R4 脚本首次完整走通）；50 秒/轮稳定重刷 60+ 轮；13 轮命中英雄登场两次（02:38/03:37 实锤）；追帧长尾实机生效（24s 全败→长尾第 2 次成功）；P-16 留痕全量落盘（GrailShopLoopSummary/Bought/DeploySkipped）；P-22 心跳实机生效（30s 精确节奏）；前置门/清场模式/继承局对账全部按设计工作；③**CPU 实测数据**：重刷期 6.1-6.6 核常载（8 核机），普通 4 核机需 1.2.77/78 的两级探针+搜索区收缩减负后实测；④**终止原因**：05:05 游戏与服务器断开连接（弹窗"请重新登录"），三次模拟点击无响应（掉线后游戏主循环停摆），需人工重新登录——非软件缺陷，事件日志完整记录掉线前健康运行状态。⑤**早响交接**：游戏需人工登录→软件实例已停（干净）→稳定目录=1.2.79 待 DECIDE 即可续测→待办见下（白名单口径/CPU 普通机实测/关键帧取证 A-B 假设/代码减量）。
- **1.2.80（P3 遗留三件+代码减量试点，2026-09-05 凌晨，净减 30 行——首个净减批次）**：①**SkippedOwned 拆口径**（审查 P3-1）：非目标与已拥有分开统计（GrailShopPassResult 增 SkippedNotTargetNames；Summary 消息分别报"跳过已拥有×N"与"非目标×N"——原混装让 N 虚高误读）；②**守护暂停心跳**（行为审计卡住#1）：GameForegroundGuard 失焦暂停期每 60s 一条 GameFocusPausedHeartbeat（原无限静默=所有有界等待的公共放大器不可见）；③**导航表 B-1 撤销**：复盘确认现版恢复例程已完整接管弃局确认页（02:1x/02:4x 实机多次验证），不做导航表补步骤（避免双重弃局竞争）；④**代码减量试点**（用户令+交叉复核 F6）：删除免费刷新死机制 excludedOptionIds/optionsChanged/两个 refresh_static_failure 死分支（1.2.67 删免费刷新后永不可达），保留等价可达行为（完整读数三票稳定+不完整连续 6 帧提前止损+30s deadline），CurrencyWarsNavigation.cs 净-74 行。构建 0/0、Grail 111。
- **1.2.82（CPU 中收益项，2026-09-05 凌晨）**：CharacterCardRecognition.Normalize 改 Mat.FromPixelData 零拷贝包裹（替代 Marshal.Copy 整帧入原生内存，与 TemplateMatching.cs:145 同款）——每次全帧识别省一次 8-14MB 拷贝。WGC CopyFrameAsync 的 SoftwareBitmap.Copy 经分析为帧池快照保护必要环节（CreateCopyFromSurfaceAsync 后防覆写）且无测试覆盖——保守不动，待白天批次截图对比验证后改。构建 0/0、Grail 111。
- **1.2.83（断线自愈，2026-09-05 凌晨，掉线卡死痛点的直接修复）**：游戏与服务器断开弹窗入识别表（disconnect_prompt，priority 90，锚点=1.2.80 取证截图裁片"与服务器断开连接，请重新登录"，searchRegion 弹窗区）+AutomationPageIds+引擎入口检测分支——检测到即点确认关闭弹窗（960,699 模态标准位）→高声停机等人工重新登录（断线态任何操作无意义，避免 Unknown 空转）。模板来自 1.2.80 取证截图（unknown_page_evidence.png 裁 1920 基准文字区 405×58）。构建 0/0、Grail 111。
- **1.2.84（断线自愈补丁，2026-09-05 凌晨，实机验证暴露）**：1.2.83 发布后实机验证发现入口 I1 仍报未知——根因=Phase2 fast 分类器有独立页白名单 FastPageIds（14 页，与 AutomationPageIds 是两套），断线页不在其内→fast 报未知→页面结论被覆盖为未知→断线分支未触发。修复=FastPageIds 加 disconnect_prompt。教训入档：**识别表新增页面时须同步三处**——JSON 识别表、AutomationPageIds（指令层子集）、Phase2FastPageIds（管线 fast 集）。构建 0/0、Grail 111。
- **1.2.71 日志复盘遗留（实测日志深度复盘发现，待处理）**：①M5 部署互换/部署失败后带残阵出战（已立案 P1，需实机调试）；②"放弃并结算提示"页无导航步骤→空转 14s+Esc 误按（导航表补直连动作）；③Esc 前恒定 2.51s 写死等待；④备战页识别置信度 ~41% 刀口阈值（需实机更新模板）；⑤启动首动作延迟 47-58s 根因；⑥RecoveryFailed 后无主页兜底验证；⑦**白名单口径需用户拍板**：实测刷出"命运圣杯契约""量子同频契约"均判未命中——若用户意图含圣杯系变体请告知。R4 验收脚本（STATUS 端到端）待写。
- **1.2.66 独立效率审计遗留（下批清单；①③⑥⑦已随 1.2.68 落实）**：②AfterActionDelay 默认 250ms/点击全局开销（InputModels.cs:14，10-17s/局，风险高逐点实测）；④备战席稳定读 650ms 前置（3-5s/局，观察）；⑤逐卖固定 1 秒改"识别帧晚于卖出时刻"有界等待（2-5s/局，坑38 复核保留）。

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
